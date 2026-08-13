using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HoshinoEditor.Models;

public sealed class VideoClipItem : INotifyPropertyChanged
{
    private double _sourceDuration = .05;
    private double _inPoint;
    private double _outPoint = .05;
    private double _speed = 1;
    private double _volume = 1;
    private bool _isMuted;
    private double _fadeIn;
    private double _fadeOut;
    private double _timelineScale = 1;
    private double _videoFadeIn;
    private double _videoFadeOut;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Path { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
    public double SourceDuration
    {
        get => _sourceDuration;
        set
        {
            _sourceDuration = double.IsFinite(value) ? Math.Max(.05, value) : .05;
            var inPoint = Math.Clamp(_inPoint, 0, Math.Max(0, _sourceDuration - .05));
            var outPoint = Math.Clamp(_outPoint, inPoint + .05, _sourceDuration);
            var inChanged = Math.Abs(inPoint - _inPoint) > .000_001;
            var outChanged = Math.Abs(outPoint - _outPoint) > .000_001;
            _inPoint = inPoint;
            _outPoint = outPoint;
            OnChanged();
            if (inChanged) OnChanged(nameof(InPoint));
            if (outChanged) OnChanged(nameof(OutPoint));
            NotifyTiming();
        }
    }
    public double InPoint
    {
        get => _inPoint;
        set
        {
            var maximum = OutPoint > .05
                ? Math.Min(Math.Max(0, SourceDuration - .05), Math.Max(0, OutPoint - .05))
                : Math.Max(0, SourceDuration - .05);
            _inPoint = Math.Clamp(double.IsFinite(value) ? value : 0, 0, maximum);
            OnChanged(); NotifyTiming();
        }
    }
    public double OutPoint { get => _outPoint; set { _outPoint = Math.Clamp(double.IsFinite(value) ? value : InPoint + .05, InPoint + .05, SourceDuration); OnChanged(); NotifyTiming(); } }
    public double Speed { get => _speed; set { _speed = Math.Clamp(double.IsFinite(value) ? value : 1, .25, 4); OnChanged(); NotifyTiming(); } }
    public double Volume { get => _volume; set { _volume = Math.Clamp(double.IsFinite(value) ? value : 1, 0, 2); OnChanged(); OnChanged(nameof(AudioText)); } }
    public bool IsMuted { get => _isMuted; set { _isMuted = value; OnChanged(); OnChanged(nameof(AudioText)); } }
    public double FadeIn { get => _fadeIn; set { _fadeIn = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 10); OnChanged(); } }
    public double FadeOut { get => _fadeOut; set { _fadeOut = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 10); OnChanged(); } }
    public double TimelineScale { get => _timelineScale; set { _timelineScale = Math.Clamp(double.IsFinite(value) ? value : 1, .4, 4); OnChanged(); OnChanged(nameof(TimelineWidth)); } }
    public double VideoFadeIn { get => _videoFadeIn; set { _videoFadeIn = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 10); OnChanged(); } }
    public double VideoFadeOut { get => _videoFadeOut; set { _videoFadeOut = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 10); OnChanged(); } }
    public double EditedDuration => Math.Max(0, OutPoint - InPoint) / Speed;
    public double TimelineWidth => Math.Clamp(EditedDuration * 12 * TimelineScale, 70, 720);
    public string DurationText => FormatTime(EditedDuration);
    public string SpeedText => Speed == 1 ? "" : $"{Speed:0.##}×";
    public string AudioText => IsMuted ? "MUTED" : Volume == 1 ? "" : $"VOL {Volume * 100:0}%";

    public VideoClipItem Clone() => new()
    {
        Path = Path, SourceDuration = SourceDuration, OutPoint = OutPoint, InPoint = InPoint, Speed = Speed,
        Volume = Volume, IsMuted = IsMuted, FadeIn = FadeIn, FadeOut = FadeOut, TimelineScale = TimelineScale,
        VideoFadeIn = VideoFadeIn, VideoFadeOut = VideoFadeOut
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
