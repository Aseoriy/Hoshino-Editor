using HoshinoEditor.Models;
using System.Text.Json;

namespace HoshinoEditor.Services;

public static class VideoProjectService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string path, string name, IEnumerable<VideoClipItem> clips)
    {
        var project = new HoshinoProject
        {
            Name = name,
            SavedAtUtc = DateTime.UtcNow,
            Clips = clips.Select(c => new VideoClipData { Path = c.Path, SourceDuration = c.SourceDuration, InPoint = c.InPoint, OutPoint = c.OutPoint, Speed = c.Speed }).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(project, Options));
    }

    public static HoshinoProject Load(string path) => JsonSerializer.Deserialize<HoshinoProject>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("The project file is empty or invalid.");
}
