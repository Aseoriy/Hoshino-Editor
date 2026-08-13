namespace HoshinoEditor.Models;

public sealed class HoshinoProject
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Untitled video";
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public List<VideoClipData> Clips { get; set; } = [];
}

public sealed class VideoClipData
{
    public required string Path { get; set; }
    public double SourceDuration { get; set; }
    public double InPoint { get; set; }
    public double OutPoint { get; set; }
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool IsMuted { get; set; }
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public double VideoFadeIn { get; set; }
    public double VideoFadeOut { get; set; }
}
