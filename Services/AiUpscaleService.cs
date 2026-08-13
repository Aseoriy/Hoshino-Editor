using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace HoshinoEditor.Services;

public static class AiUpscaleService
{
    private const string EngineArchiveUrl = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";
    private const string EngineArchiveSha256 = "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d";
    private const long MaxOutputPixels = 50_000_000;
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExtractedBytes = 1024L * 1024 * 1024;
    private const int MaxArchiveEntries = 4096;
    private static readonly string EngineFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sail Solutions", "Hoshino Editor", "AI", "RealESRGAN");
    private static readonly HttpClient Client = CreateClient();
    private static readonly SemaphoreSlim EngineInstallGate = new(1, 1);
    private sealed record EngineAsset(Uri Url, string Sha256);
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public static async Task<BitmapSource> UpscaleAsync(BitmapSource source, double factor, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The editor can hand us a mutable WPF bitmap owned by the UI thread.
        // Freeze a caller-thread clone before any encoder/resize work is queued.
        source = ImageEditService.FreezeForBackgroundAccess(source);
        if (!double.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        factor = Math.Clamp(factor, 1.01, 10);
        var requestedWidth = Math.Round(source.PixelWidth * factor);
        var requestedHeight = Math.Round(source.PixelHeight * factor);
        if (requestedWidth is < 1 or > 32_768 || requestedHeight is < 1 or > 32_768)
            throw new InvalidOperationException("That AI upscale would exceed the 32,768 pixel dimension limit.");
        var width = (int)requestedWidth;
        var height = (int)requestedHeight;
        if ((long)width * height > MaxOutputPixels)
            throw new InvalidOperationException("That AI upscale would exceed the 50 megapixel safety limit.");
        var engineScale = factor <= 2 ? 2 : factor <= 3 ? 3 : 4;
        var engineWidth = checked((long)source.PixelWidth * engineScale);
        var engineHeight = checked((long)source.PixelHeight * engineScale);
        if (engineWidth > 32_768 || engineHeight > 32_768 || checked(engineWidth * engineHeight) > MaxOutputPixels)
            throw new InvalidOperationException("The AI engine's intermediate image would exceed the processing safety limit.");

        var executable = await EnsureEngineAsync(new InlineProgress(value => progress?.Report(value * .35)), cancellationToken).ConfigureAwait(false);
        var workFolder = Path.Combine(Path.GetTempPath(), "Hoshino Editor", "AI Upscale", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workFolder);
        try
        {
            var input = Path.Combine(workFolder, "input.png");
            await Task.Run(() => ImageEditService.Save(source, input), cancellationToken).ConfigureAwait(false);
            var current = Path.Combine(workFolder, $"ai-{engineScale}x.png");
            progress?.Report(40);
            await RunEngineAsync(executable, input, current, engineScale, cancellationToken).ConfigureAwait(false);
            progress?.Report(92);
            var result = await Task.Run(() => ImageEditService.Load(current), cancellationToken).ConfigureAwait(false);
            if (result.PixelWidth != width || result.PixelHeight != height)
                result = await Task.Run(() => ImageEditService.Resize(result, width, height, true), cancellationToken).ConfigureAwait(false);
            result = ImageEditService.FreezeForBackgroundAccess(result);
            progress?.Report(100);
            return result;
        }
        finally
        {
            try { if (Directory.Exists(workFolder)) Directory.Delete(workFolder, true); } catch { }
        }
    }

    private static async Task<string> EnsureEngineAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var existing = FindEngine();
        if (existing is not null) { progress?.Report(100); return existing; }
        await EngineInstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingFolder = null;
        try
        {
            existing = FindEngine();
            if (existing is not null) { progress?.Report(100); return existing; }

            var parent = Path.GetDirectoryName(EngineFolder) ?? throw new InvalidOperationException("The AI engine folder is invalid.");
            Directory.CreateDirectory(parent);
            CleanupStaleInstallFolders(parent);
            stagingFolder = Path.Combine(parent, $".RealESRGAN-install-{Guid.NewGuid():N}");
            var payloadFolder = Path.Combine(stagingFolder, "payload");
            var archive = Path.Combine(stagingFolder, "engine.zip.download");
            Directory.CreateDirectory(payloadFolder);

            progress?.Report(2);
            var asset = new EngineAsset(new Uri(EngineArchiveUrl), EngineArchiveSha256);
            await DownloadEngineArchiveAsync(asset, archive, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(92);
            await Task.Run(() => ExtractArchiveSafely(archive, payloadFolder, cancellationToken), cancellationToken).ConfigureAwait(false);
            var stagedExecutable = FindEngine(payloadFolder)
                ?? throw new InvalidDataException("The Real-ESRGAN archive did not contain a complete Windows engine and model package.");
            _ = stagedExecutable;
            PromoteEngine(payloadFolder);
            var installed = FindEngine() ?? throw new InvalidDataException("Real-ESRGAN was staged, but the completed installation could not be validated.");
            progress?.Report(100);
            return installed;
        }
        finally
        {
            try { if (stagingFolder is not null && Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); } catch { }
            EngineInstallGate.Release();
        }
    }

    private static async Task DownloadEngineArchiveAsync(EngineAsset asset, string archive, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var download = await Client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        download.EnsureSuccessStatusCode();
        var total = download.Content.Headers.ContentLength;
        if (total is > MaxArchiveBytes) throw new InvalidDataException("The AI engine archive exceeds the 512 MB download limit.");
        byte[] actualSha256;
        long received = 0;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var source = await download.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var destination = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
            {
                var buffer = new byte[1024 * 128];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received = checked(received + read);
                    if (received > MaxArchiveBytes) throw new InvalidDataException("The AI engine archive exceeds the 512 MB download limit.");
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (total is > 0) progress?.Report(5 + received * 85d / total.Value);
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            actualSha256 = hash.GetHashAndReset();
        }
        if (total is > 0 && received != total.Value) throw new InvalidDataException("The AI engine download ended before all bytes were received.");
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(asset.Sha256), actualSha256))
            throw new InvalidDataException("The AI engine archive failed SHA-256 verification.");
    }

    private static void ExtractArchiveSafely(string archive, string destination, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("The AI engine archive contains too many files.");
        long totalSize = 0;
        foreach (var entry in zip.Entries)
        {
            totalSize = checked(totalSize + entry.Length);
            if (totalSize > MaxExtractedBytes) throw new InvalidDataException("The AI engine archive exceeds the 1 GB extraction limit.");
        }
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The AI engine archive contained an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 128];
            long written = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written = checked(written + read);
                if (written > entry.Length) throw new InvalidDataException("The AI engine archive expanded beyond its declared size.");
                target.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new InvalidDataException("The AI engine archive contains a truncated file.");
        }
    }

    private static string? FindEngine() => FindEngine(EngineFolder);

    private static string? FindEngine(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        try
        {
            foreach (var executable in Directory.EnumerateFiles(folder, "realesrgan-ncnn-vulkan.exe", SearchOption.AllDirectories))
            {
                var executableFolder = Path.GetDirectoryName(executable)!;
                var modelFolder = Path.Combine(executableFolder, "models");
                var parameterFile = Path.Combine(modelFolder, "realesrgan-x4plus.param");
                var modelFile = Path.Combine(modelFolder, "realesrgan-x4plus.bin");
                if (new FileInfo(executable).Length > 500_000 && HasPortableExecutableHeader(executable)
                    && File.Exists(parameterFile) && new FileInfo(parameterFile).Length > 10_000
                    && File.Exists(modelFile) && new FileInfo(modelFile).Length > 1_000_000)
                    return executable;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static void PromoteEngine(string payloadFolder)
    {
        var backup = EngineFolder + $".old-{Guid.NewGuid():N}";
        var hadExisting = Directory.Exists(EngineFolder);
        var promoted = false;
        if (hadExisting) Directory.Move(EngineFolder, backup);
        try
        {
            Directory.Move(payloadFolder, EngineFolder);
            promoted = true;
        }
        catch
        {
            try { if (Directory.Exists(EngineFolder)) Directory.Delete(EngineFolder, true); } catch { }
            if (hadExisting && Directory.Exists(backup)) Directory.Move(backup, EngineFolder);
            throw;
        }
        finally
        {
            try { if (promoted && Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
        }
    }

    private static void CleanupStaleInstallFolders(string parent)
    {
        foreach (var folder in Directory.EnumerateDirectories(parent, ".RealESRGAN-install-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(folder, true);
            }
            catch { }
        }
    }

    private static bool HasPortableExecutableHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    private static async Task RunEngineAsync(string executable, string input, string output, int scale, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in new[] { "-i", input, "-o", output, "-n", "realesrgan-x4plus", "-s", scale.ToString(), "-f", "png", "-j", "2:2:2" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The Real-ESRGAN engine could not be started.");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var messages = string.Join(Environment.NewLine, await outputTask, await errorTask).Trim();
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(messages) ? "Real-ESRGAN could not process this image. A Vulkan-capable GPU and current drivers are required." : messages);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HoshinoEditor", UpdateService.CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
