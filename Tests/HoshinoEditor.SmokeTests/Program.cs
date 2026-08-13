using HoshinoEditor.Controls;
using HoshinoEditor.Models;
using HoshinoEditor.Services;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HoshinoEditor.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { return MainAsync(args).GetAwaiter().GetResult(); }
        finally { System.Windows.Application.Current?.Shutdown(); }
    }

    private static async Task<int> MainAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = timeout.Token;
        var application = new App();
        application.InitializeComponent();
        var videoWorkspace = new VideoWorkspace(null);
        Require(videoWorkspace.Clips.Count == 0, "video workspace opens without media");
        videoWorkspace.Close();
        var photoWorkspace = new PhotoWorkspace(null);
        Require(photoWorkspace.Title == "Untitled composition", "photo workspace tools load");
        photoWorkspace.ActivatePanTool();
        photoWorkspace.ActivateMoveTool();
        photoWorkspace.Close();
        var settingsWorkspace = new SettingsWorkspace();
        Require(settingsWorkspace.DataContext is AppSettings, "editable shortcut settings load");

        var ffmpeg = FfmpegService.FindExecutable();
        if (ffmpeg is null)
        {
            Console.Error.WriteLine("FAIL: ffmpeg.exe was not found. Run scripts/Get-Ffmpeg.ps1 first.");
            return 1;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"HoshinoEditor-Smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var clipA = Path.Combine(folder, "clip-a.mp4");
            var clipB = Path.Combine(folder, "clip-b.mp4");
            var imagePath = Path.Combine(folder, "input.png");
            await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=size=320x240:rate=30:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", clipA], cancellationToken);
            await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "color=c=royalblue:size=640x360:rate=24:duration=1.5", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", clipB], cancellationToken);
            await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=size=96x64:rate=1:duration=1", "-frames:v", "1", imagePath], cancellationToken);

            var probeA = await FfmpegService.ProbeAsync(clipA, cancellationToken);
            var probeB = await FfmpegService.ProbeAsync(clipB, cancellationToken);
            Require(Math.Abs(probeA.DurationSeconds - 2) < .1 && probeA.Width == 320 && probeA.Height == 240, "clip A probe");
            Require(Math.Abs(probeB.DurationSeconds - 1.5) < .1 && probeB.Width == 640 && probeB.Height == 360, "clip B probe");

            var clips = new List<VideoClipItem>
            {
                new() { Path = clipA, SourceDuration = 2, InPoint = .2, OutPoint = 1.8, Speed = 1.25,
                    Volume = .65, FadeIn = .2, FadeOut = .25, VideoFadeIn = .15, VideoFadeOut = .2 },
                new() { Path = clipB, SourceDuration = 1.5, InPoint = .1, OutPoint = 1.4, Speed = .75, IsMuted = true }
            };
            var merged = Path.Combine(folder, "merged.mp4");
            var lastProgress = 0d;
            await VideoExportService.ExportAsync(clips, merged, 1, false, new InlineProgress(value => lastProgress = Math.Max(lastProgress, value)), cancellationToken);
            var mergedProbe = await FfmpegService.ProbeAsync(merged, cancellationToken);
            Require(File.Exists(merged) && new FileInfo(merged).Length > 10_000, "combined export file");
            Require(mergedProbe.DurationSeconds is > 2.9 and < 3.2 && mergedProbe.Width == 320 && mergedProbe.Height == 240, "combined export media");
            Require(lastProgress == 100, "export progress reaches 100%");

            var projectPath = Path.Combine(folder, "roundtrip.hoshino");
            VideoProjectService.Save(projectPath, "Smoke test", clips);
            var project = VideoProjectService.Load(projectPath);
            Require(project.Version == 2 && project.Clips.Count == 2 && Math.Abs(project.Clips[0].Volume - .65) < .001
                && Math.Abs(project.Clips[0].VideoFadeOut - .2) < .001 && project.Clips[1].IsMuted, "project round-trip");

            var malformedProject = Path.Combine(folder, "malformed.hoshino");
            await File.WriteAllTextAsync(malformedProject, "{\"Version\":2,\"Name\":\"Broken\",\"Clips\":null}", cancellationToken);
            RequireThrows<InvalidDataException>(() => VideoProjectService.Load(malformedProject), "malformed project rejection");
            Require(VideoProjectService.IsNetworkOrDevicePath(@"\\server\share\clip.mp4") &&
                    VideoProjectService.IsNetworkOrDevicePath(@"\\?\C:\clip.mp4") &&
                    !VideoProjectService.IsNetworkOrDevicePath(clipA), "network project sources are detected before probing");

            var orderedTiming = new VideoClipItem { Path = clipA, SourceDuration = 2, InPoint = .2, OutPoint = 1.8 };
            Require(Math.Abs(orderedTiming.InPoint - .2) < .001 && Math.Abs(orderedTiming.OutPoint - 1.8) < .001,
                "clip timing survives initializer order");
            orderedTiming.SourceDuration = .5;
            Require(orderedTiming.InPoint >= 0 && orderedTiming.OutPoint <= .5 && orderedTiming.OutPoint - orderedTiming.InPoint >= .049,
                "clip timing re-clamps after duration shrink");

            var image = ImageEditService.Load(imagePath);
            var mutableImage = new WriteableBitmap(image);
            Require(!mutableImage.IsFrozen && ImageEditService.FreezeForBackgroundAccess(mutableImage).IsFrozen,
                "mutable WPF bitmap is frozen for worker access");
            var buffer = ImageEditService.CapturePixels(image);
            var adjusted = ImageEditService.AdjustPixels(buffer, new ImageAdjustments(12, 18, 15, 8, -20, 25, 30));
            Require(!adjusted.SequenceEqual(buffer.Pixels), "image adjustments");
            RequireThrows<InvalidOperationException>(() => ImageEditService.Resize(image, 10_000, 6_000, true),
                "oversized image operation rejection");
            foreach (var variant in new[] { ImageEditService.AutoTone(image), ImageEditService.Grayscale(image),
                         ImageEditService.Sepia(image), ImageEditService.Sharpen(image) })
                Require(variant.PixelWidth == image.PixelWidth && variant.PixelHeight == image.PixelHeight, "filter dimensions");
            var text = ImageEditService.CreateTextBitmap("Hoshino\nEditor", "Segoe UI", 48, true, Colors.MediumPurple);
            Require(text.PixelWidth > 20 && text.PixelHeight > 20, "text-layer rendering");

            var backgroundPixels = Enumerable.Repeat((byte)255, 9 * 9 * 4).ToArray();
            for (var y = 2; y <= 6; y++)
            for (var x = 2; x <= 6; x++)
            {
                if (x is not (2 or 6) && y is not (2 or 6)) continue;
                var pixel = (y * 9 + x) * 4;
                backgroundPixels[pixel] = 242; backgroundPixels[pixel + 1] = 101; backgroundPixels[pixel + 2] = 88;
            }
            var enclosedBackground = ImageEditService.CreateBitmap(new ImagePixelBuffer(9, 9, 96, 96, 36, backgroundPixels), backgroundPixels);
            var outerOnly = ImageEditService.CapturePixels(ImageEditService.RemoveBackground(enclosedBackground, 4, 1, BackgroundRemovalMode.OuterOnly));
            var innerOnly = ImageEditService.CapturePixels(ImageEditService.RemoveBackground(enclosedBackground, 4, 1, BackgroundRemovalMode.InnerOnly));
            Require(AlphaAt(outerOnly, 0, 0) == 0 && AlphaAt(outerOnly, 4, 4) == 255,
                "outer background removal preserves enclosed matching color");
            Require(AlphaAt(innerOnly, 0, 0) == 255 && AlphaAt(innerOnly, 4, 4) == 0,
                "inner background removal preserves edge-connected color");

            ShortcutService.ResetDefaults();
            Require(ShortcutService.TryParse(ShortcutService.GetRaw("PanTool"), out var panBinding) && panBinding.Key == System.Windows.Input.Key.H,
                "configurable shortcut parsing");
            Require(!ShortcutService.TryParse("Ctrl+A+B", out _) && !ShortcutService.TryParse("Ctrl+9999", out _),
                "malformed shortcuts are rejected");

            var canceledOutput = Path.Combine(folder, "canceled.mp4");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await RequireCanceledAsync(() => FfmpegService.RenderPreparedClipAsync(clips[0], canceledOutput, 1, false, cancellationToken: canceled.Token),
                    "FFmpeg cancellation waits for process cleanup");
            }
            Require(!File.Exists(canceledOutput), "canceled FFmpeg output cleanup");

            if (args.Contains("--ai", StringComparer.OrdinalIgnoreCase))
            {
                var aiProgress = 0d;
                var upscaled = await AiUpscaleService.UpscaleAsync(mutableImage, 2,
                    new InlineProgress(value => aiProgress = Math.Max(aiProgress, value)), cancellationToken);
                Require(upscaled.PixelWidth == 192 && upscaled.PixelHeight == 128, "Real-ESRGAN 2x dimensions");
                Require(aiProgress == 100, "Real-ESRGAN progress reaches 100%");
            }

            Console.WriteLine("PASS: Hoshino media, project, image, and text smoke tests completed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex}");
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
        }
    }

    private static async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not start.");
        try
        {
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var message = await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException(message);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Smoke assertion failed: {name}");
        Console.WriteLine($"PASS: {name}");
    }

    private static void RequireThrows<TException>(Action action, string name) where TException : Exception
    {
        try { action(); }
        catch (TException) { Console.WriteLine($"PASS: {name}"); return; }
        throw new InvalidOperationException($"Smoke assertion failed: {name}");
    }

    private static async Task RequireCanceledAsync(Func<Task> action, string name)
    {
        try { await action(); }
        catch (OperationCanceledException) { Console.WriteLine($"PASS: {name}"); return; }
        throw new InvalidOperationException($"Smoke assertion failed: {name}");
    }

    private static byte AlphaAt(ImagePixelBuffer buffer, int x, int y) => buffer.Pixels[y * buffer.Stride + x * 4 + 3];

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
