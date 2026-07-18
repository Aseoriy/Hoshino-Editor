using HoshinoEditor.Models;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace HoshinoEditor.Services;

public static class VideoExportService
{
    public static async Task<TimeSpan> GetDurationAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        return properties.Duration;
    }

    public static async Task ExportAsync(IReadOnlyList<VideoClipItem> clips, string outputPath, double upscaleFactor = 1, bool preferGpu = true, IProgress<double>? progress = null)
    {
        if (clips.Count == 0) throw new InvalidOperationException("Add at least one video clip before exporting.");
        var composition = new MediaComposition();
        var temporaryFiles = new List<string>();
        try
        {
            for (var index = 0; index < clips.Count; index++)
            {
                var item = clips[index];
                if (!File.Exists(item.Path)) throw new FileNotFoundException($"Missing clip: {item.Name}", item.Path);
                string sourcePath;
                var wasPrepared = Math.Abs(item.Speed - 1) > .001 || Math.Abs(upscaleFactor - 1) > .001;
                if (wasPrepared)
                {
                    sourcePath = Path.Combine(Path.GetTempPath(), $"hoshino-{Guid.NewGuid():N}.mp4");
                    temporaryFiles.Add(sourcePath);
                    await FfmpegService.RenderPreparedClipAsync(item, sourcePath, upscaleFactor, preferGpu);
                    progress?.Report((index + .65) / clips.Count * 20);
                }
                else sourcePath = item.Path;

                var source = await StorageFile.GetFileFromPathAsync(sourcePath);
                var clip = await MediaClip.CreateFromFileAsync(source);
                if (!wasPrepared)
                {
                    clip.TrimTimeFromStart = TimeSpan.FromSeconds(item.InPoint);
                    clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, item.SourceDuration - item.OutPoint));
                }
                composition.Clips.Add(clip);
            }

            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!);
            var output = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            var operation = composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile);
            operation.Progress = (_, value) => progress?.Report(20 + value * .8);
            var result = await operation;
            if (result != TranscodeFailureReason.None) throw new InvalidOperationException($"Windows Media Transcoding returned {result}.");
        }
        finally
        {
            foreach (var path in temporaryFiles)
                try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
