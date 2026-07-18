namespace HoshinoEditor.Models;

public sealed class HoshinoProject
{
    public int Version { get; set; } = 1;
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
}
