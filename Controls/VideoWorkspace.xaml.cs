using HoshinoEditor.Models;
using HoshinoEditor.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HoshinoEditor.Controls;

public partial class VideoWorkspace : UserControl, IEditorWorkspace
{
    private sealed record ProjectSnapshot(List<VideoClipItem> Clips, int SelectedIndex);
    private readonly Stack<ProjectSnapshot> _undo = new();
    private readonly Stack<ProjectSnapshot> _redo = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private VideoClipItem? _selectedClip;
    private string? _projectPath;
    private bool _syncingSelection;
    private bool _syncingInspector;
    private bool _scrubbing;
    private bool _isPlaying;
    private bool _playWhenReady;

    public ObservableCollection<VideoClipItem> Clips { get; } = [];
    public string Title => _projectPath is null ? (Clips.FirstOrDefault()?.Name ?? "Untitled video") : Path.GetFileNameWithoutExtension(_projectPath);
    public string Status { get; private set; } = "Video workspace";
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? HomeRequested;
    public event EventHandler<ToastMessage>? ToastRequested;

    public VideoWorkspace(string? initialPath)
    {
        InitializeComponent();
        VideoUpscale_ValueChanged(this, new RoutedPropertyChangedEventArgs<double>(0, VideoUpscaleSlider.Value));
        Player.Volume = VolumeSlider.Value;
        _playbackTimer.Tick += PlaybackTimer_Tick;
        if (initialPath is not null) _ = AddFilesAsync([initialPath]);
    }

    public void Open() => AddMedia_Click(this, new RoutedEventArgs());
    public void Save() => SaveProject_Click(this, new RoutedEventArgs());
    public void Undo() => Undo_Click(this, new RoutedEventArgs());
    public void Redo() => Redo_Click(this, new RoutedEventArgs());
    public void TogglePlayback() => Play_Click(this, new RoutedEventArgs());
    public void CancelActiveTool() { if (_isPlaying) Pause(); }
    public void Close() { _playbackTimer.Stop(); Player.Stop(); Player.Source = null; }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var valid = paths.Where(File.Exists).Where(p => MediaTypeService.GetKind(p) == EditorKind.Video).ToArray();
        if (valid.Length == 0) return;
        PushUndo();
        SetStatus("Reading clip information…");
        foreach (var path in valid)
        {
            try
            {
                var duration = await VideoExportService.GetDurationAsync(path);
                var seconds = Math.Max(.1, duration.TotalSeconds);
                AddClip(new VideoClipItem { Path = path, SourceDuration = seconds, InPoint = 0, OutPoint = seconds });
            }
            catch
            {
                // Some installed codecs can play a file even when properties cannot inspect it.
                AddClip(new VideoClipItem { Path = path, SourceDuration = 1, InPoint = 0, OutPoint = 1 });
            }
        }
        if (_selectedClip is null && Clips.Count > 0) SelectClip(Clips[0]);
        EmptyState.Visibility = Visibility.Collapsed;
        PlayerFrame.Visibility = Visibility.Visible;
        UpdateProjectSummary();
        TitleChanged?.Invoke(this, Title);
        SetStatus($"{Clips.Count} clip{(Clips.Count == 1 ? "" : "s")}  ·  {VideoClipItem.FormatTime(Clips.Sum(c => c.EditedDuration))}");
    }

    private void AddClip(VideoClipItem clip, int? index = null)
    {
        clip.PropertyChanged += Clip_PropertyChanged;
        if (index is null) Clips.Add(clip); else Clips.Insert(index.Value, clip);
    }

    private void Clip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VideoClipItem.EditedDuration) or nameof(VideoClipItem.Speed)) UpdateProjectSummary();
    }

    private void SelectClip(VideoClipItem clip, bool autoplay = false)
    {
        _selectedClip = clip;
        _syncingSelection = true;
        MediaList.SelectedItem = clip; TimelineList.SelectedItem = clip;
        _syncingSelection = false;
        SyncInspector();
        LoadPreview(clip, autoplay);
    }

    private void LoadPreview(VideoClipItem clip, bool autoplay)
    {
        Pause();
        _playWhenReady = autoplay;
        Player.Source = new Uri(clip.Path, UriKind.Absolute);
        Player.SpeedRatio = clip.Speed;
        Player.Position = TimeSpan.FromSeconds(clip.InPoint);
        Scrubber.Minimum = clip.InPoint; Scrubber.Maximum = Math.Max(clip.InPoint + .05, clip.OutPoint); Scrubber.Value = clip.InPoint; Scrubber.IsEnabled = true;
        PreviewLabel.Text = clip.Speed == 1 ? "PREVIEW" : $"PREVIEW  ·  {clip.Speed:0.##}×";
        UpdateTimeText(clip.InPoint);
    }

    private void SyncInspector()
    {
        _syncingInspector = true;
        if (_selectedClip is null)
        {
            InspectorName.Text = "Select a clip"; InspectorMeta.Text = "Trim and speed controls will appear here.";
            InSlider.IsEnabled = OutSlider.IsEnabled = false;
        }
        else
        {
            var c = _selectedClip;
            InspectorName.Text = c.Name;
            InspectorMeta.Text = $"Source {VideoClipItem.FormatTime(c.SourceDuration)}  ·  Edited {VideoClipItem.FormatTime(c.EditedDuration)}  ·  {c.Speed:0.##}×";
            InSlider.Minimum = 0; InSlider.Maximum = Math.Max(.1, c.SourceDuration); InSlider.Value = c.InPoint; InSlider.IsEnabled = true;
            OutSlider.Minimum = 0; OutSlider.Maximum = Math.Max(.1, c.SourceDuration); OutSlider.Value = c.OutPoint; OutSlider.IsEnabled = true;
            InValue.Text = FormatPrecise(c.InPoint); OutValue.Text = FormatPrecise(c.OutPoint);
        }
        _syncingInspector = false;
    }

    private void InSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingInspector || _selectedClip is null || InValue is null) return;
        _selectedClip.InPoint = Math.Min(InSlider.Value, _selectedClip.OutPoint - .05);
        InValue.Text = FormatPrecise(_selectedClip.InPoint);
        Scrubber.Minimum = _selectedClip.InPoint; Scrubber.Value = _selectedClip.InPoint;
        Player.Position = TimeSpan.FromSeconds(_selectedClip.InPoint);
        SyncInspectorMetaOnly();
    }

    private void OutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingInspector || _selectedClip is null || OutValue is null) return;
        _selectedClip.OutPoint = Math.Max(OutSlider.Value, _selectedClip.InPoint + .05);
        OutValue.Text = FormatPrecise(_selectedClip.OutPoint);
        Scrubber.Maximum = _selectedClip.OutPoint;
        if (Player.Position.TotalSeconds > _selectedClip.OutPoint) Player.Position = TimeSpan.FromSeconds(_selectedClip.OutPoint);
        SyncInspectorMetaOnly();
    }

    private void InspectorEdit_Begin(object sender, MouseButtonEventArgs e) { if (_selectedClip is not null) PushUndo(); }
    private void SyncInspectorMetaOnly()
    {
        if (_selectedClip is not null) InspectorMeta.Text = $"Source {VideoClipItem.FormatTime(_selectedClip.SourceDuration)}  ·  Edited {VideoClipItem.FormatTime(_selectedClip.EditedDuration)}  ·  {_selectedClip.Speed:0.##}×";
        UpdateProjectSummary();
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null || sender is not Button { Tag: string value } || !double.TryParse(value, out var speed)) return;
        PushUndo(); _selectedClip.Speed = speed; Player.SpeedRatio = speed; PreviewLabel.Text = speed == 1 ? "PREVIEW" : $"PREVIEW  ·  {speed:0.##}×"; SyncInspector(); UpdateProjectSummary();
    }

    private void VideoUpscale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VideoUpscaleValue is not null) VideoUpscaleValue.Text = $"{VideoUpscaleSlider.Value:0}%";
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null) return;
        var position = Player.Position.TotalSeconds;
        if (position <= _selectedClip.InPoint + .05 || position >= _selectedClip.OutPoint - .05)
        {
            ToastRequested?.Invoke(this, new ToastMessage("Move the playhead inside the clip before splitting.", true)); return;
        }
        PushUndo();
        var index = Clips.IndexOf(_selectedClip);
        var right = _selectedClip.Clone();
        right.InPoint = position;
        _selectedClip.OutPoint = position;
        AddClip(right, index + 1);
        SelectClip(right);
        UpdateProjectSummary();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null) return;
        PushUndo(); var index = Clips.IndexOf(_selectedClip); var copy = _selectedClip.Clone(); AddClip(copy, index + 1); SelectClip(copy); UpdateProjectSummary();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null) return;
        PushUndo(); var index = Clips.IndexOf(_selectedClip); _selectedClip.PropertyChanged -= Clip_PropertyChanged; Clips.Remove(_selectedClip);
        if (Clips.Count == 0) { _selectedClip = null; PlayerFrame.Visibility = Visibility.Collapsed; EmptyState.Visibility = Visibility.Visible; Player.Source = null; SyncInspector(); }
        else SelectClip(Clips[Math.Min(index, Clips.Count - 1)]);
        UpdateProjectSummary();
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveRight_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int delta)
    {
        if (_selectedClip is null) return;
        var old = Clips.IndexOf(_selectedClip); var next = old + delta;
        if (next < 0 || next >= Clips.Count) return;
        PushUndo(); Clips.Move(old, next); TimelineList.ScrollIntoView(_selectedClip);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null) return;
        if (_isPlaying) Pause(); else Play();
    }

    private void Play()
    {
        if (_selectedClip is null) return;
        if (Player.Position.TotalSeconds >= _selectedClip.OutPoint - .03) Player.Position = TimeSpan.FromSeconds(_selectedClip.InPoint);
        Player.SpeedRatio = _selectedClip.Speed; Player.Play(); _isPlaying = true; PlayButton.Content = new TextBlock { Text = "Ⅱ", FontSize = 14 }; _playbackTimer.Start(); SetStatus("Playing preview");
    }

    private void Pause()
    {
        Player.Pause(); _isPlaying = false; PlayButton.Content = new TextBlock { Text = "▶", FontSize = 14 }; _playbackTimer.Stop();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_selectedClip is null || _scrubbing) return;
        var position = Player.Position.TotalSeconds;
        if (position >= _selectedClip.OutPoint - .025)
        {
            var index = Clips.IndexOf(_selectedClip);
            if (index >= 0 && index < Clips.Count - 1) SelectClip(Clips[index + 1], true);
            else { Player.Position = TimeSpan.FromSeconds(_selectedClip.OutPoint); Pause(); SetStatus("Preview finished"); }
            return;
        }
        Scrubber.Value = Math.Clamp(position, Scrubber.Minimum, Scrubber.Maximum);
        UpdateTimeText(position);
    }

    private void Scrubber_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedClip is null || !_scrubbing) return;
        Player.Position = TimeSpan.FromSeconds(Scrubber.Value); UpdateTimeText(Scrubber.Value);
    }
    private void Scrubber_DragStarted(object sender, MouseButtonEventArgs e) => _scrubbing = true;
    private void Scrubber_DragEnded(object sender, MouseButtonEventArgs e) { if (_selectedClip is not null) { Player.Position = TimeSpan.FromSeconds(Scrubber.Value); UpdateTimeText(Scrubber.Value); } _scrubbing = false; }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null) return; var index = Clips.IndexOf(_selectedClip); if (index > 0) SelectClip(Clips[index - 1]); else Player.Position = TimeSpan.FromSeconds(_selectedClip.InPoint);
    }
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null) return; var index = Clips.IndexOf(_selectedClip); if (index < Clips.Count - 1) SelectClip(Clips[index + 1]); else Player.Position = TimeSpan.FromSeconds(_selectedClip.OutPoint);
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null) return;
        if (Player.NaturalDuration.HasTimeSpan && (_selectedClip.SourceDuration <= 1 || Math.Abs(Player.NaturalDuration.TimeSpan.TotalSeconds - _selectedClip.SourceDuration) > .5))
        {
            _selectedClip.SourceDuration = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (_selectedClip.OutPoint <= 1) _selectedClip.OutPoint = _selectedClip.SourceDuration;
            SyncInspector();
        }
        Player.Position = TimeSpan.FromSeconds(_selectedClip.InPoint);
        if (_playWhenReady) { _playWhenReady = false; Play(); }
    }
    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e) => ToastRequested?.Invoke(this, new ToastMessage($"Preview couldn't decode this clip: {e.ErrorException?.Message}", true));

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Player is not null) { Player.Volume = VolumeSlider.Value; Player.IsMuted = false; } }
    private void Mute_Click(object sender, RoutedEventArgs e) { Player.IsMuted = !Player.IsMuted; MuteButton.Content = Player.IsMuted ? "×" : "♪"; }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncingSelection && MediaList.SelectedItem is VideoClipItem clip) SelectClip(clip); }
    private void TimelineList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncingSelection && TimelineList.SelectedItem is VideoClipItem clip) SelectClip(clip); }

    private void PushUndo()
    {
        _undo.Push(CaptureSnapshot()); if (_undo.Count > 30) { var arr = _undo.Reverse().Skip(1).Reverse().ToArray(); _undo.Clear(); foreach (var s in arr) _undo.Push(s); }
        _redo.Clear(); UpdateHistoryButtons();
    }
    private ProjectSnapshot CaptureSnapshot() => new(Clips.Select(c => c.Clone()).ToList(), _selectedClip is null ? -1 : Clips.IndexOf(_selectedClip));
    private void ApplySnapshot(ProjectSnapshot snapshot)
    {
        Pause(); foreach (var c in Clips) c.PropertyChanged -= Clip_PropertyChanged; Clips.Clear(); foreach (var c in snapshot.Clips) AddClip(c.Clone());
        if (Clips.Count > 0) { EmptyState.Visibility = Visibility.Collapsed; PlayerFrame.Visibility = Visibility.Visible; SelectClip(Clips[Math.Clamp(snapshot.SelectedIndex, 0, Clips.Count - 1)]); }
        else { _selectedClip = null; EmptyState.Visibility = Visibility.Visible; PlayerFrame.Visibility = Visibility.Collapsed; SyncInspector(); }
        UpdateProjectSummary();
    }
    private void Undo_Click(object sender, RoutedEventArgs e) { if (_undo.Count == 0) return; _redo.Push(CaptureSnapshot()); ApplySnapshot(_undo.Pop()); UpdateHistoryButtons(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (_redo.Count == 0) return; _undo.Push(CaptureSnapshot()); ApplySnapshot(_redo.Pop()); UpdateHistoryButtons(); }
    private void UpdateHistoryButtons() { UndoButton.IsEnabled = _undo.Count > 0; RedoButton.IsEnabled = _redo.Count > 0; }

    private void AddMedia_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import video clips", Multiselect = true, Filter = "Videos|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.m4v|Hoshino project|*.hoshino|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        if (dialog.FileNames.Length == 1 && Path.GetExtension(dialog.FileName).Equals(".hoshino", StringComparison.OrdinalIgnoreCase)) LoadProject(dialog.FileName);
        else _ = AddFilesAsync(dialog.FileNames);
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (Clips.Count == 0) { ToastRequested?.Invoke(this, new ToastMessage("Add a clip before saving a project.", true)); return; }
        if (_projectPath is null)
        {
            var dialog = new SaveFileDialog { Title = "Save Hoshino project", Filter = "Hoshino project|*.hoshino", DefaultExt = ".hoshino", FileName = "Untitled video.hoshino", AddExtension = true };
            if (dialog.ShowDialog() != true) return; _projectPath = dialog.FileName;
        }
        try { VideoProjectService.Save(_projectPath, Title, Clips); TitleChanged?.Invoke(this, Title); SetStatus($"Project saved  ·  {Path.GetFileName(_projectPath)}"); ToastRequested?.Invoke(this, new ToastMessage("Project saved.")); }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Couldn't save the project: {ex.Message}", true)); }
    }

    private void LoadProject(string path)
    {
        try
        {
            var project = VideoProjectService.Load(path); PushUndo(); foreach (var c in Clips) c.PropertyChanged -= Clip_PropertyChanged; Clips.Clear();
            foreach (var item in project.Clips.Where(c => File.Exists(c.Path))) AddClip(new VideoClipItem { Path = item.Path, SourceDuration = item.SourceDuration, InPoint = item.InPoint, OutPoint = item.OutPoint, Speed = item.Speed });
            _projectPath = path; if (Clips.Count > 0) SelectClip(Clips[0]); EmptyState.Visibility = Clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed; PlayerFrame.Visibility = Clips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            TitleChanged?.Invoke(this, Title); UpdateProjectSummary(); SetStatus($"Project loaded  ·  {Clips.Count} clips");
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Couldn't open the project: {ex.Message}", true)); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Clips.Count == 0) { ToastRequested?.Invoke(this, new ToastMessage("Add at least one clip before exporting.", true)); return; }
        var dialog = new SaveFileDialog { Title = "Export video", Filter = "MP4 video|*.mp4", DefaultExt = ".mp4", FileName = $"{Path.GetFileNameWithoutExtension(Title)}-export.mp4", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        Pause(); ExportOverlay.Visibility = Visibility.Visible; ExportButton.IsEnabled = false; ExportProgress.Value = 0;
        try
        {
            var progress = new Progress<double>(value => { ExportProgress.Value = value; ExportStatusText.Text = $"Rendering… {value:0}%"; });
            await VideoExportService.ExportAsync(Clips.ToList(), dialog.FileName, VideoUpscaleSlider.Value / 100, SettingsService.Current.PreferGpuAcceleration, progress);
            SetStatus($"Exported  ·  {Path.GetFileName(dialog.FileName)}"); ToastRequested?.Invoke(this, new ToastMessage("Video exported."));
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Export failed: {ex.Message}", true)); }
        finally { ExportOverlay.Visibility = Visibility.Collapsed; ExportButton.IsEnabled = true; }
    }

    private void UpdateProjectSummary()
    {
        if (ProjectDurationText is null) return; var duration = Clips.Sum(c => c.EditedDuration); ProjectDurationText.Text = $"{Clips.Count} CLIPS  ·  {VideoClipItem.FormatTime(duration)}"; SetStatus($"{Clips.Count} clip{(Clips.Count == 1 ? "" : "s")}  ·  {VideoClipItem.FormatTime(duration)}");
    }
    private void UpdateTimeText(double absoluteSeconds)
    {
        if (_selectedClip is null) { TimeText.Text = "0:00 / 0:00"; return; }
        TimeText.Text = $"{VideoClipItem.FormatTime(Math.Max(0, absoluteSeconds - _selectedClip.InPoint) / _selectedClip.Speed)} / {_selectedClip.DurationText}";
    }
    private static string FormatPrecise(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss\.f");
    private void SetStatus(string status) { Status = status; StatusChanged?.Invoke(this, status); }
    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; e.Effects = paths?.Any(p => MediaTypeService.GetKind(p) == EditorKind.Video) == true ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    private void Root_Drop(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths is not null) _ = AddFilesAsync(paths);
    }
}
