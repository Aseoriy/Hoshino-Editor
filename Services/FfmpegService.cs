using HoshinoEditor.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HoshinoEditor.Services;

public static class FfmpegService
{
    public sealed record VideoProbe(double DurationSeconds, int Width, int Height);
    private static string? _encoders;
    private static string? _encoderExecutable;
    private static readonly SemaphoreSlim EncoderDiscoveryGate = new(1, 1);
    public static string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Tools", "ffmpeg.exe"))
        };
        var local = candidates.FirstOrDefault(File.Exists);
        if (local is not null) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var folder = entry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    public static async Task<string> RenderSpeedAdjustedClipAsync(VideoClipItem clip, string outputPath,
        CancellationToken cancellationToken = default)
        => await RenderPreparedClipAsync(clip, outputPath, 1, false, null, cancellationToken);

    public static async Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable() ?? throw new FileNotFoundException("Media inspection requires ffmpeg.exe.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-i", path }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not inspect the video.");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var text = string.Join(Environment.NewLine, await outputTask, await errorTask);
            var durationMatch = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
            var videoMatch = Regex.Match(text, @"Video:.*?,\s*(\d{2,5})x(\d{2,5})(?:[\s,\[])" , RegexOptions.CultureInvariant);
            var duration = durationMatch.Success
                ? int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                    + int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture)
                : 0;
            var width = videoMatch.Success ? int.Parse(videoMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var height = videoMatch.Success ? int.Parse(videoMatch.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (duration <= 0 && (width <= 0 || height <= 0))
                throw new InvalidDataException("FFmpeg could not read duration or video dimensions from this file.");
            return new VideoProbe(duration, width, height);
        }
        catch (OperationCanceledException)
        {
            await KillAndWaitAsync(process);
            throw;
        }
    }

    public static async Task<string> RenderPreparedClipAsync(VideoClipItem clip, string outputPath, double upscaleFactor, bool preferGpu,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default, int? targetWidth = null, int? targetHeight = null)
    {
        var executable = FindExecutable() ?? throw new FileNotFoundException(
            "Speed-adjusted or upscaled export requires ffmpeg.exe. Run scripts/Get-Ffmpeg.ps1 once, then rebuild Hoshino Editor.");
        var start = clip.InPoint.ToString("0.######", CultureInfo.InvariantCulture);
        var sourceDuration = Math.Max(.05, clip.OutPoint - clip.InPoint);
        var duration = sourceDuration.ToString("0.######", CultureInfo.InvariantCulture);
        var editedDuration = sourceDuration / clip.Speed;
        var outputDuration = editedDuration.ToString("0.######", CultureInfo.InvariantCulture);
        var speed = clip.Speed.ToString("0.######", CultureInfo.InvariantCulture);
        upscaleFactor = Math.Clamp(upscaleFactor, 1, 10);
        var videoFilters = new List<string>();
        if (Math.Abs(clip.Speed - 1) > .001) videoFilters.Add($"setpts=PTS/{speed}");
        if (targetWidth is > 0 && targetHeight is > 0)
        {
            videoFilters.Add($"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease:flags=lanczos");
            videoFilters.Add($"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:color=black");
            videoFilters.Add("setsar=1");
        }
        else if (Math.Abs(upscaleFactor - 1) > .001)
        {
            var factor = upscaleFactor.ToString("0.######", CultureInfo.InvariantCulture);
            videoFilters.Add($"scale=trunc(iw*{factor}/2)*2:trunc(ih*{factor}/2)*2:flags=lanczos");
        }
        else videoFilters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        var videoFadeIn = Math.Min(clip.VideoFadeIn, editedDuration);
        var videoFadeOut = Math.Min(clip.VideoFadeOut, editedDuration);
        if (videoFadeIn > .001) videoFilters.Add($"fade=t=in:st=0:d={videoFadeIn.ToString("0.######", CultureInfo.InvariantCulture)}");
        if (videoFadeOut > .001)
        {
            var videoFadeStart = Math.Max(0, editedDuration - videoFadeOut);
            videoFilters.Add($"fade=t=out:st={videoFadeStart.ToString("0.######", CultureInfo.InvariantCulture)}:d={videoFadeOut.ToString("0.######", CultureInfo.InvariantCulture)}");
        }

        var hasAudio = await HasAudioStreamAsync(executable, clip.Path, cancellationToken);
        var candidates = await EncoderCandidatesAsync(executable, preferGpu, cancellationToken);
        string? lastError = null;
        foreach (var encoder in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", start, "-t", duration, "-i", clip.Path,
            };
            if (!hasAudio) arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
            arguments.AddRange([
                "-map", "0:v:0", "-map", hasAudio ? "0:a:0" : "1:a:0", "-filter_threads", "4"
            ]);
            if (videoFilters.Count > 0) { arguments.Add("-vf"); arguments.Add(string.Join(',', videoFilters)); }
            arguments.Add("-af");
            arguments.Add(BuildAudioFilters(clip, sourceDuration / clip.Speed));
            arguments.AddRange(EncoderArguments(encoder));
            arguments.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-t", outputDuration, "-movflags", "+faststart", "-progress", "pipe:1", outputPath]);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not be started.");
            var succeeded = false;
            try
            {
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var progressTask = ReadProgressAsync(process.StandardOutput, sourceDuration / clip.Speed, progress, cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                await progressTask;
                lastError = (await errorTask).Trim();
                if (process.ExitCode == 0)
                {
                    succeeded = true;
                    progress?.Report(100);
                    return outputPath;
                }
            }
            catch (OperationCanceledException)
            {
                await KillAndWaitAsync(process);
                throw;
            }
            finally
            {
                if (!succeeded)
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            }
        }
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastError) ? "FFmpeg could not process the clip." : lastError);
    }

    public static async Task ConcatPreparedClipsAsync(IReadOnlyList<string> paths, string outputPath, double expectedDuration,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) throw new InvalidOperationException("There are no prepared clips to join.");
        var executable = FindExecutable() ?? throw new FileNotFoundException("Video export requires ffmpeg.exe.");
        var concatFile = Path.Combine(Path.GetTempPath(), $"hoshino-concat-{Guid.NewGuid():N}.txt");
        try
        {
            var entries = paths.Select(path => $"file '{Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(concatFile, entries, cancellationToken);
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", concatFile,
                "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-movflags", "+faststart", "-avoid_negative_ts", "make_zero",
                "-progress", "pipe:1", outputPath
            }) info.ArgumentList.Add(argument);

            using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not join the prepared clips.");
            try
            {
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var progressTask = ReadProgressAsync(process.StandardOutput, expectedDuration, progress, cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                await progressTask;
                var error = (await errorTask).Trim();
                if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFmpeg could not join the prepared clips." : error);
                progress?.Report(100);
            }
            catch (OperationCanceledException)
            {
                await KillAndWaitAsync(process);
                throw;
            }
        }
        finally
        {
            try { if (File.Exists(concatFile)) File.Delete(concatFile); } catch { }
        }
    }

    private static async Task<bool> HasAudioStreamAsync(string executable, string path, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-frames:a", "1", "-f", "null", "-" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not inspect the clip audio.");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            await KillAndWaitAsync(process);
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> EncoderCandidatesAsync(string executable, bool preferGpu,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (preferGpu)
        {
            await EncoderDiscoveryGate.WaitAsync(cancellationToken);
            try
            {
                if (_encoders is null || !string.Equals(_encoderExecutable, executable, StringComparison.OrdinalIgnoreCase))
                {
                    var discovered = string.Empty;
                    try
                    {
                        var info = new ProcessStartInfo(executable)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        info.ArgumentList.Add("-hide_banner");
                        info.ArgumentList.Add("-encoders");
                        using var process = Process.Start(info);
                        if (process is not null)
                        {
                            try
                            {
                                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                                await process.WaitForExitAsync(cancellationToken);
                                await Task.WhenAll(outputTask, errorTask);
                                if (process.ExitCode == 0) discovered = outputTask.Result;
                            }
                            catch (OperationCanceledException)
                            {
                                await KillAndWaitAsync(process);
                                throw;
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* Encoder discovery failure should still fall back to libx264. */ }
                    _encoders = discovered;
                    _encoderExecutable = executable;
                }
            }
            finally { EncoderDiscoveryGate.Release(); }
            foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" }) if (_encoders.Contains(encoder, StringComparison.Ordinal)) result.Add(encoder);
        }
        result.Add("libx264"); return result;
    }

    private static IEnumerable<string> EncoderArguments(string encoder) => encoder switch
    {
        "h264_nvenc" => ["-c:v", encoder, "-preset", "p5", "-cq", "19"],
        "h264_qsv" => ["-c:v", encoder, "-preset", "faster", "-global_quality", "20"],
        "h264_amf" => ["-c:v", encoder, "-quality", "balanced", "-qp_i", "20", "-qp_p", "20"],
        _ => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-threads", "8"]
    };

    private static async Task ReadProgressAsync(StreamReader reader, double expectedDuration, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Equals("progress=end", StringComparison.Ordinal))
            {
                progress?.Report(100);
                continue;
            }
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || expectedDuration <= 0) continue;
            if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                progress?.Report(Math.Clamp(microseconds / 1_000_000d / expectedDuration * 100, 0, 99));
        }
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    private static string BuildAtempo(double speed)
    {
        var factors = new List<double>();
        while (speed > 2.0) { factors.Add(2.0); speed /= 2.0; }
        while (speed < 0.5) { factors.Add(0.5); speed /= 0.5; }
        factors.Add(speed);
        return string.Join(',', factors.Select(value => $"atempo={value.ToString("0.######", CultureInfo.InvariantCulture)}"));
    }

    private static string BuildAudioFilters(VideoClipItem clip, double outputDuration)
    {
        var filters = new List<string>();
        if (Math.Abs(clip.Speed - 1) > .001) filters.Add(BuildAtempo(clip.Speed));
        var volume = clip.IsMuted ? 0 : clip.Volume;
        filters.Add($"volume={volume.ToString("0.######", CultureInfo.InvariantCulture)}");
        var fadeIn = Math.Min(clip.FadeIn, outputDuration);
        var fadeOut = Math.Min(clip.FadeOut, outputDuration);
        if (fadeIn > .001) filters.Add($"afade=t=in:st=0:d={fadeIn.ToString("0.######", CultureInfo.InvariantCulture)}");
        if (fadeOut > .001)
        {
            var start = Math.Max(0, outputDuration - fadeOut);
            filters.Add($"afade=t=out:st={start.ToString("0.######", CultureInfo.InvariantCulture)}:d={fadeOut.ToString("0.######", CultureInfo.InvariantCulture)}");
        }
        filters.Add("apad");
        return string.Join(',', filters);
    }
}
