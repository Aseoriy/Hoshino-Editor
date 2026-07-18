using HoshinoEditor.Models;
using System.Diagnostics;
using System.Globalization;

namespace HoshinoEditor.Services;

public static class FfmpegService
{
    private static string? _encoders;
    public static string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Tools", "ffmpeg.exe"))
        };
        var local = candidates.FirstOrDefault(File.Exists);
        if (local is not null) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), "ffmpeg.exe")).FirstOrDefault(File.Exists);
    }

    public static async Task<string> RenderSpeedAdjustedClipAsync(VideoClipItem clip, string outputPath)
        => await RenderPreparedClipAsync(clip, outputPath, 1, false);

    public static async Task<string> RenderPreparedClipAsync(VideoClipItem clip, string outputPath, double upscaleFactor, bool preferGpu)
    {
        var executable = FindExecutable() ?? throw new FileNotFoundException(
            "Speed-adjusted or upscaled export requires ffmpeg.exe. Run scripts/Get-Ffmpeg.ps1 once, then rebuild Hoshino Editor.");
        var start = clip.InPoint.ToString("0.######", CultureInfo.InvariantCulture);
        var end = clip.OutPoint.ToString("0.######", CultureInfo.InvariantCulture);
        var speed = clip.Speed.ToString("0.######", CultureInfo.InvariantCulture);
        upscaleFactor = Math.Clamp(upscaleFactor, 1, 10);
        var videoFilters = new List<string>();
        if (Math.Abs(clip.Speed - 1) > .001) videoFilters.Add($"setpts=PTS/{speed}");
        if (Math.Abs(upscaleFactor - 1) > .001)
        {
            var factor = upscaleFactor.ToString("0.######", CultureInfo.InvariantCulture);
            videoFilters.Add($"scale=trunc(iw*{factor}/2)*2:trunc(ih*{factor}/2)*2:flags=lanczos");
        }

        var candidates = await EncoderCandidatesAsync(executable, preferGpu);
        string? lastError = null;
        foreach (var encoder in candidates)
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-ss", start, "-to", end, "-i", clip.Path, "-map", "0:v:0", "-map", "0:a?" };
            if (videoFilters.Count > 0) { arguments.Add("-vf"); arguments.Add(string.Join(',', videoFilters)); }
            if (Math.Abs(clip.Speed - 1) > .001) { arguments.Add("-af"); arguments.Add(BuildAtempo(clip.Speed)); }
            arguments.AddRange(EncoderArguments(encoder));
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outputPath]);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not be started.");
            var errorTask = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); lastError = (await errorTask).Trim();
            if (process.ExitCode == 0) return outputPath;
        }
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastError) ? "FFmpeg could not process the clip." : lastError);
    }

    private static async Task<IReadOnlyList<string>> EncoderCandidatesAsync(string executable, bool preferGpu)
    {
        var result = new List<string>();
        if (preferGpu)
        {
            if (_encoders is null)
            {
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("-hide_banner"); info.ArgumentList.Add("-encoders");
                using var process = Process.Start(info); if (process is not null) { _encoders = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync(); }
                _encoders ??= string.Empty;
            }
            foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" }) if (_encoders.Contains(encoder, StringComparison.Ordinal)) result.Add(encoder);
        }
        result.Add("libx264"); return result;
    }

    private static IEnumerable<string> EncoderArguments(string encoder) => encoder switch
    {
        "h264_nvenc" => ["-c:v", encoder, "-preset", "p5", "-cq", "19"],
        "h264_qsv" => ["-c:v", encoder, "-preset", "faster", "-global_quality", "20"],
        "h264_amf" => ["-c:v", encoder, "-quality", "balanced", "-qp_i", "20", "-qp_p", "20"],
        _ => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20"]
    };

    private static string BuildAtempo(double speed)
    {
        var factors = new List<double>();
        while (speed > 2.0) { factors.Add(2.0); speed /= 2.0; }
        while (speed < 0.5) { factors.Add(0.5); speed /= 0.5; }
        factors.Add(speed);
        return string.Join(',', factors.Select(value => $"atempo={value.ToString("0.######", CultureInfo.InvariantCulture)}"));
    }
}
