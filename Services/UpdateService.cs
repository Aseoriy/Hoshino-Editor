using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace HoshinoEditor.Services;

public sealed record UpdateRelease(string Version, string Name, string Markdown, Uri DownloadUrl, string FileName, Uri PageUrl, string Sha256);

public static class UpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/Aseoriy/Hoshino-Editor/releases";
    private const long MaxInstallerBytes = 512L * 1024 * 1024;
    private static readonly string UpdatesRoot = Path.Combine(Path.GetTempPath(), "Hoshino Editor", "Updates");
    private static readonly HttpClient Client = CreateClient();
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);
    private sealed record ReleaseAsset(string Name, string Url, string Sha256);
    private readonly record struct ReleaseVersion(int Major, int Minor, int Patch, int PreRank, int PreNumber);

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.0.0";

    public static async Task<UpdateRelease?> CheckAsync(bool includePrereleases, CancellationToken cancellationToken = default)
    {
        CleanupUpdateCache();
        using var response = await Client.GetAsync(ReleasesApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub returned an invalid releases response.");

        if (!TryParseVersion(CurrentVersion, out var current)) current = default;
        UpdateRelease? best = null;
        ReleaseVersion? bestVersion = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("draft", out var draft) || draft.GetBoolean()) continue;
            if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean() && !includePrereleases) continue;
            var tag = release.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() ?? string.Empty : string.Empty;
            if (!TryParseVersion(tag, out var version) || Compare(version, current) <= 0) continue;
            if (bestVersion is not null && Compare(version, bestVersion.Value) <= 0) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            var asset = assets.EnumerateArray().Select(TryReadInstallerAsset).Where(value => value is not null)
                .OrderByDescending(value => value!.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(value => value!.Name.Contains("installer", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (asset is null || !Uri.TryCreate(asset.Url, UriKind.Absolute, out var downloadUrl) || downloadUrl.Scheme != Uri.UriSchemeHttps) continue;

            var page = release.TryGetProperty("html_url", out var pageValue) ? pageValue.GetString() : null;
            best = new UpdateRelease(
                tag.TrimStart('v', 'V'),
                release.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? tag : tag,
                release.TryGetProperty("body", out var bodyValue) ? bodyValue.GetString() ?? "No release notes were provided." : "No release notes were provided.",
                downloadUrl,
                asset.Name,
                Uri.TryCreate(page, UriKind.Absolute, out var pageUrl) ? pageUrl : new Uri("https://github.com/Aseoriy/Hoshino-Editor/releases"),
                asset.Sha256);
            bestVersion = version;
        }
        return best;
    }

    public static async Task<string> DownloadInstallerAsync(UpdateRelease release, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.DownloadUrl.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Update downloads must use HTTPS.");
        if (!TryNormalizeSha256(release.Sha256, out var expectedSha256))
            throw new InvalidDataException("The release asset does not include a valid SHA-256 digest.");
        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var folder = Path.Combine(UpdatesRoot, SafePathSegment(release.Version, "update"));
            Directory.CreateDirectory(folder);
            CleanupStaleDownloads(folder);
            var safeName = SafePathSegment(release.FileName, "HoshinoEditorSetup.exe");
            if (!Path.GetExtension(safeName).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected update asset is not a Windows installer.");
            var destination = Path.Combine(folder, safeName);
            temporary = Path.Combine(folder, $".{safeName}.{Guid.NewGuid():N}.download");

            using var response = await Client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total is > MaxInstallerBytes) throw new InvalidDataException("The update installer exceeds the 512 MB download limit.");
            byte[] actualSha256;
            long received = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
                {
                    var buffer = new byte[1024 * 128];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        received = checked(received + read);
                        if (received > MaxInstallerBytes) throw new InvalidDataException("The update installer exceeds the 512 MB download limit.");
                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        if (total is > 0) progress?.Report(received * 100d / total.Value);
                    }
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                actualSha256 = hash.GetHashAndReset();
            }
            if (total is > 0 && received != total.Value) throw new InvalidDataException("The update download ended before all bytes were received.");
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedSha256), actualSha256))
                throw new InvalidDataException("The update installer failed SHA-256 verification.");
            ValidateInstaller(temporary);
            File.Move(temporary, destination, true);
            temporary = null;
            progress?.Report(100);
            return destination;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); } catch { }
            DownloadGate.Release();
        }
    }

    public static void LaunchInstaller(string installerPath, string expectedDigest)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("The downloaded installer could not be found.", installerPath);
        if (!TryNormalizeSha256(expectedDigest, out var expectedSha256))
            throw new InvalidDataException("The release asset does not include a valid SHA-256 digest.");
        ValidateInstaller(installerPath);
        using (var stream = File.OpenRead(installerPath))
        {
            var actualSha256 = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedSha256), actualSha256))
                throw new InvalidDataException("The update installer changed after it was downloaded.");
        }
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
    }

    private static void ValidateInstaller(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is < 100_000 or > MaxInstallerBytes) throw new InvalidDataException("The downloaded installer has an unexpected size.");
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            throw new InvalidDataException("The downloaded update is not a valid Windows installer.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HoshinoEditor", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static ReleaseAsset? TryReadInstallerAsset(JsonElement value)
    {
        var name = value.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? string.Empty : string.Empty;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !name.Contains("Hoshino", StringComparison.OrdinalIgnoreCase)
            || (!name.Contains("setup", StringComparison.OrdinalIgnoreCase) && !name.Contains("installer", StringComparison.OrdinalIgnoreCase))) return null;
        var url = value.TryGetProperty("browser_download_url", out var urlValue) ? urlValue.GetString() ?? string.Empty : string.Empty;
        var digest = value.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() : null;
        return TryNormalizeSha256(digest, out var sha256) ? new ReleaseAsset(name, url, $"sha256:{sha256}") : null;
    }

    private static bool TryParseVersion(string value, out ReleaseVersion version)
    {
        version = default;
        value = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
        var pieces = value.Split('-', 2, StringSplitOptions.TrimEntries);
        var numbers = pieces[0].Split('.');
        if (numbers.Length is < 1 or > 3) return false;
        var parsed = new int[3];
        for (var index = 0; index < numbers.Length; index++)
            if (!int.TryParse(numbers[index], out parsed[index]) || parsed[index] < 0) return false;
        if (pieces.Length == 1)
        {
            version = new ReleaseVersion(parsed[0], parsed[1], parsed[2], int.MaxValue, int.MaxValue);
            return true;
        }
        var prerelease = pieces[1].ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(prerelease)) return false;
        var rank = prerelease.StartsWith("alpha") ? 0 : prerelease.StartsWith("beta") ? 1 : prerelease.StartsWith("rc") ? 2 : 0;
        var suffix = prerelease.Split('-', '.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        version = new ReleaseVersion(parsed[0], parsed[1], parsed[2], rank, int.TryParse(suffix, out var preNumber) ? preNumber : 0);
        return true;
    }

    private static int Compare(ReleaseVersion left, ReleaseVersion right)
    {
        var values = new[]
        {
            left.Major.CompareTo(right.Major), left.Minor.CompareTo(right.Minor), left.Patch.CompareTo(right.Patch),
            left.PreRank.CompareTo(right.PreRank), left.PreNumber.CompareTo(right.PreNumber)
        };
        return values.FirstOrDefault(value => value != 0);
    }

    private static bool TryNormalizeSha256(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
        var value = digest["sha256:".Length..];
        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch))) return false;
        sha256 = value.ToLowerInvariant();
        return true;
    }

    private static string SafePathSegment(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(safe)) safe = fallback;
        return safe.Length <= 120 ? safe : safe[..120];
    }

    private static void CleanupStaleDownloads(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*.download", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.Delete(path);
            }
            catch { }
        }
    }

    private static void CleanupUpdateCache()
    {
        if (!Directory.Exists(UpdatesRoot)) return;
        foreach (var folder in Directory.EnumerateDirectories(UpdatesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var newestWrite = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder))
                    .Max();
                if (newestWrite < DateTime.UtcNow.AddDays(-7)) Directory.Delete(folder, true);
                else CleanupStaleDownloads(folder);
            }
            catch { }
        }
    }
}
