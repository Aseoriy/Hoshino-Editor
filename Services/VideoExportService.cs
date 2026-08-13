using HoshinoEditor.Models;
using Windows.Storage;

namespace HoshinoEditor.Services;

public static class VideoExportService
{
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public static async Task<TimeSpan> GetDurationAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.GetVideoPropertiesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (properties.Duration > TimeSpan.Zero) return properties.Duration;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* FFmpeg supports formats that the Windows property system cannot inspect. */ }
        var probe = await FfmpegService.ProbeAsync(path, cancellationToken);
        if (probe.DurationSeconds <= 0) throw new InvalidDataException("The video duration could not be determined.");
        return TimeSpan.FromSeconds(probe.DurationSeconds);
    }

    public static async Task ExportAsync(IReadOnlyList<VideoClipItem> clips, string outputPath, double upscaleFactor = 1, bool preferGpu = true,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (clips.Count == 0) throw new InvalidOperationException("Add at least one video clip before exporting.");
        var progressLock = new object();
        var lastProgress = -1d;
        var safeProgress = new InlineProgress(value =>
        {
            value = Math.Clamp(value, 0, 100);
            lock (progressLock)
            {
                if (value <= lastProgress) return;
                lastProgress = value;
                progress?.Report(value);
            }
        });
        upscaleFactor = Math.Clamp(upscaleFactor, 1, 10);
        outputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("Choose a valid export folder.");
        var stagedOutput = Path.Combine(outputDirectory, $".hoshino-export-{Guid.NewGuid():N}.mp4");
        var executable = FfmpegService.FindExecutable() ?? throw new FileNotFoundException(
            "Video export requires ffmpeg.exe. Reinstall Hoshino Editor or run scripts/Get-Ffmpeg.ps1, then rebuild.");
        _ = executable;

        if (clips.Count == 1)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ValidateOutputSizeAsync(clips[0], upscaleFactor, cancellationToken);
                await FfmpegService.RenderPreparedClipAsync(clips[0], stagedOutput, upscaleFactor, preferGpu, safeProgress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(stagedOutput, outputPath, true);
                safeProgress.Report(100);
                return;
            }
            finally { TryDelete(stagedOutput); }
        }

        var temporaryFiles = new List<string>();
        try
        {
            int? targetWidth = null;
            int? targetHeight = null;
            for (var index = 0; index < clips.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = clips[index];
                if (!File.Exists(item.Path)) throw new FileNotFoundException($"Missing clip: {item.Name}", item.Path);
                await ValidateOutputSizeAsync(item, upscaleFactor, cancellationToken, targetWidth, targetHeight);
                var sourcePath = Path.Combine(Path.GetTempPath(), $"hoshino-{Guid.NewGuid():N}.mp4");
                temporaryFiles.Add(sourcePath);
                var clipIndex = index;
                var clipProgress = new InlineProgress(value => safeProgress.Report(Math.Clamp((clipIndex + value / 100) / clips.Count * 80, 0, 80)));
                await FfmpegService.RenderPreparedClipAsync(item, sourcePath, upscaleFactor, preferGpu, clipProgress,
                    cancellationToken, targetWidth, targetHeight);
                if (index == 0)
                {
                    var size = await GetDimensionsAsync(sourcePath, cancellationToken);
                    targetWidth = size.Width > 0 ? size.Width : null;
                    targetHeight = size.Height > 0 ? size.Height : null;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var concatProgress = new InlineProgress(value => safeProgress.Report(Math.Clamp(80 + value * .2, 80, 100)));
            await FfmpegService.ConcatPreparedClipsAsync(temporaryFiles, stagedOutput, clips.Sum(clip => clip.EditedDuration),
                concatProgress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedOutput, outputPath, true);
            safeProgress.Report(100);
        }
        finally
        {
            foreach (var path in temporaryFiles) TryDelete(path);
            TryDelete(stagedOutput);
        }
    }

    private static async Task<(int Width, int Height)> GetDimensionsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.GetVideoPropertiesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (properties.Width > 0 && properties.Height > 0) return ((int)properties.Width, (int)properties.Height);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        var probe = await FfmpegService.ProbeAsync(path, cancellationToken);
        return (probe.Width, probe.Height);
    }

    private static async Task ValidateOutputSizeAsync(VideoClipItem clip, double upscaleFactor, CancellationToken cancellationToken,
        int? targetWidth = null, int? targetHeight = null)
    {
        var sourceDimensions = await GetDimensionsAsync(clip.Path, cancellationToken);
        if (sourceDimensions.Width <= 0 || sourceDimensions.Height <= 0)
            throw new InvalidDataException($"The dimensions of {clip.Name} could not be determined safely.");
        if (sourceDimensions.Width > 8192 || sourceDimensions.Height > 8192 ||
            (long)sourceDimensions.Width * sourceDimensions.Height > 50_000_000)
            throw new InvalidOperationException($"{clip.Name} exceeds the 8192-per-side or 50-megapixel source safety limit.");

        long width;
        long height;
        if (targetWidth is > 0 && targetHeight is > 0)
        {
            width = targetWidth.Value;
            height = targetHeight.Value;
        }
        else
        {
            width = (long)Math.Ceiling(sourceDimensions.Width * upscaleFactor);
            height = (long)Math.Ceiling(sourceDimensions.Height * upscaleFactor);
        }
        if (width > 8192 || height > 8192 || width * height > 50_000_000)
            throw new InvalidOperationException($"The requested output would be {width} × {height}. Choose a lower scale (maximum 8192 per side and 50 megapixels per frame).");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
