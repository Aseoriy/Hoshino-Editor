using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HoshinoEditor.Models;

public sealed class VideoClipItem : INotifyPropertyChanged
{
    private double _sourceDuration;
    private double _inPoint;
    private double _outPoint;
    private double _speed = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Path { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
    public double SourceDuration { get => _sourceDuration; set { _sourceDuration = value; OnChanged(); OnChanged(nameof(DurationText)); OnChanged(nameof(TimelineWidth)); } }
    public double InPoint { get => _inPoint; set { _inPoint = Math.Clamp(value, 0, Math.Max(0, OutPoint - .05)); OnChanged(); NotifyTiming(); } }
    public double OutPoint { get => _outPoint; set { _outPoint = Math.Clamp(value, InPoint + .05, Math.Max(InPoint + .05, SourceDuration)); OnChanged(); NotifyTiming(); } }
    public double Speed { get => _speed; set { _speed = Math.Clamp(value, .25, 4); OnChanged(); NotifyTiming(); } }
    public double EditedDuration => Math.Max(0, OutPoint - InPoint) / Speed;
    public double TimelineWidth => Math.Clamp(EditedDuration * 12, 90, 360);
    public string DurationText => FormatTime(EditedDuration);
    public string SpeedText => Speed == 1 ? "" : $"{Speed:0.##}×";

    public VideoClipItem Clone() => new()
    {
        Path = Path, SourceDuration = SourceDuration, InPoint = InPoint, OutPoint = OutPoint, Speed = Speed
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyTiming() { OnChanged(nameof(EditedDuration)); OnChanged(nameof(DurationText)); OnChanged(nameof(TimelineWidth)); OnChanged(nameof(SpeedText)); }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalHours >= 1) return span.ToString(@"h\:mm\:ss");
        return seconds < 10 ? span.ToString(@"m\:ss\.f") : span.ToString(@"m\:ss");
    }
}
