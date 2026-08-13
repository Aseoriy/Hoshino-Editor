using HoshinoEditor.Models;
using System.Text.Json;

namespace HoshinoEditor.Services;

public static class VideoProjectService
{
    private const long MaxProjectBytes = 16L * 1024 * 1024;
    private const int MaxClips = 10_000;
    private const double MaxDurationSeconds = 30 * 24 * 60 * 60;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 32 };
    private static readonly string RecoveryFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sail Solutions", "Hoshino Editor", "Recovery");
    public static string RecoveryPath { get; } = Path.Combine(RecoveryFolder, "last-video.autosave.hoshino");

    public static void Save(string path, string name, IEnumerable<VideoClipItem> clips)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var project = ValidateAndNormalize(new HoshinoProject
        {
            Name = name,
            SavedAtUtc = DateTime.UtcNow,
            Clips = clips.Select(c => new VideoClipData
            {
                Path = c.Path, SourceDuration = c.SourceDuration, InPoint = c.InPoint, OutPoint = c.OutPoint, Speed = c.Speed,
                Volume = c.Volume, IsMuted = c.IsMuted, FadeIn = c.FadeIn, FadeOut = c.FadeOut,
                VideoFadeIn = c.VideoFadeIn, VideoFadeOut = c.VideoFadeOut
            }).ToList()
        });
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(project, Options));
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static HoshinoProject Load(string path)
    {
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The project file could not be found.", path);
        if (info.Length is <= 0 or > MaxProjectBytes)
            throw new InvalidDataException($"Project files must be between 1 byte and {MaxProjectBytes / 1024 / 1024} MB.");
        try
        {
            return ValidateAndNormalize(JsonSerializer.Deserialize<HoshinoProject>(File.ReadAllText(path), Options));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The project file contains invalid JSON or is missing required data.", ex);
        }
    }

    public static void SaveRecovery(string name, IEnumerable<VideoClipItem> clips)
    {
        Directory.CreateDirectory(RecoveryFolder);
        Save(RecoveryPath, name, clips);
    }

    public static bool IsRecoveryPath(string path) => Path.GetFullPath(path).Equals(Path.GetFullPath(RecoveryPath), StringComparison.OrdinalIgnoreCase);

    public static void DeleteRecovery()
    {
        try { if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath); } catch { }
    }

    private static HoshinoProject ValidateAndNormalize(HoshinoProject? project)
    {
        if (project is null) throw new InvalidDataException("The project file is empty or invalid.");
        if (project.Version is < 1 or > HoshinoProject.CurrentVersion)
            throw new InvalidDataException($"Project version {project.Version} is not supported by this version of Hoshino Editor.");
        if (project.Clips is null) throw new InvalidDataException("The project does not contain a valid clip list.");
        if (project.Clips.Count > MaxClips) throw new InvalidDataException($"A project cannot contain more than {MaxClips:N0} clips.");

        var clips = new List<VideoClipData>(project.Clips.Count);
        for (var index = 0; index < project.Clips.Count; index++)
        {
            var clip = project.Clips[index] ?? throw new InvalidDataException($"Clip {index + 1} is empty.");
            if (string.IsNullOrWhiteSpace(clip.Path) || clip.Path.Length > 32_767)
                throw new InvalidDataException($"Clip {index + 1} has an invalid source path.");
            try { _ = Path.GetFullPath(clip.Path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException($"Clip {index + 1} has an invalid source path.", ex);
            }
            RequireRange(clip.SourceDuration, .05, MaxDurationSeconds, index, "source duration");
            RequireRange(clip.InPoint, 0, clip.SourceDuration - .05, index, "in point");
            RequireRange(clip.OutPoint, clip.InPoint + .05, clip.SourceDuration, index, "out point");
            RequireRange(clip.Speed, .25, 4, index, "speed");
            RequireRange(clip.Volume, 0, 2, index, "volume");
            RequireRange(clip.FadeIn, 0, 10, index, "audio fade-in");
            RequireRange(clip.FadeOut, 0, 10, index, "audio fade-out");
            RequireRange(clip.VideoFadeIn, 0, 10, index, "video fade-in");
            RequireRange(clip.VideoFadeOut, 0, 10, index, "video fade-out");
            clips.Add(new VideoClipData
            {
                Path = clip.Path,
                SourceDuration = clip.SourceDuration,
                InPoint = clip.InPoint,
                OutPoint = clip.OutPoint,
                Speed = clip.Speed,
                Volume = clip.Volume,
                IsMuted = clip.IsMuted,
                FadeIn = clip.FadeIn,
                FadeOut = clip.FadeOut,
                VideoFadeIn = clip.VideoFadeIn,
                VideoFadeOut = clip.VideoFadeOut
            });
        }

        var name = string.IsNullOrWhiteSpace(project.Name) ? "Untitled video" : project.Name.Trim();
        if (name.Length > 200) name = name[..200];
        return new HoshinoProject
        {
            Version = HoshinoProject.CurrentVersion,
            Name = name,
            SavedAtUtc = project.SavedAtUtc == default ? DateTime.UtcNow : project.SavedAtUtc,
            Clips = clips
        };
    }

    private static void RequireRange(double value, double minimum, double maximum, int clipIndex, string field)
    {
        if (!double.IsFinite(value) || value < minimum - .000_001 || value > maximum + .000_001)
            throw new InvalidDataException($"Clip {clipIndex + 1} has an invalid {field}.");
    }

    public static bool IsNetworkOrDevicePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return new Uri(Path.GetFullPath(path)).IsUnc; }
        catch { return false; }
    }
}
