using HoshinoEditor.Models;
using HoshinoEditor.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HoshinoEditor.Controls;

public partial class VideoWorkspace : UserControl, IEditorWorkspace
{
    private sealed record ProjectSnapshot(List<VideoClipItem> Clips, int SelectedIndex);
    private readonly Stack<ProjectSnapshot> _undo = new();
    private readonly Stack<ProjectSnapshot> _redo = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _scrubSeekTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _autoSaveTimer = new();
    private VideoClipItem? _selectedClip;
    private string? _projectPath;
    private bool _syncingSelection;
    private bool _syncingInspector;
    private bool _scrubbing;
    private bool _isPlaying;
    private bool _playWhenReady;
    private bool _resumeAfterScrub;
    private bool _masterMuted;
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _importCts;
    private Point _timelineDragStart;
    private bool _projectDirty;
    private bool _ownsRecovery;
    private bool _discardChanges;
    private string? _recoveredTitle;
    private int _workspaceVersion;
    private bool _closed;
    private IInputElement? _focusBeforeExport;

    public ObservableCollection<VideoClipItem> Clips { get; } = [];
    public string Title => _projectPath is null
        ? (_recoveredTitle is null ? Clips.FirstOrDefault()?.Name ?? "Untitled video" : $"{_recoveredTitle} (recovered)")
        : Path.GetFileNameWithoutExtension(_projectPath);
    public string Status { get; private set; } = "Video workspace";
    public bool IsBusy => _exportCts is not null || _importCts is not null;
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
        _scrubSeekTimer.Tick += ScrubSeekTimer_Tick;
        _autoSaveTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(SettingsService.Current.AutoSaveMinutes, 1, 30));
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        foreach (var control in new Control[] { InSlider, OutSlider, ClipVolumeSlider, FadeInSlider, FadeOutSlider, VideoFadeInSlider, VideoFadeOutSlider, ClipMuteCheck })
            control.PreviewKeyDown += InspectorEdit_PreviewKeyDown;
        Scrubber.LostMouseCapture += Scrubber_LostMouseCapture;
        SyncInspector();
        if (SettingsService.Current.AutoSaveVideoProjects) _autoSaveTimer.Start();
        if (initialPath is not null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Path.GetExtension(initialPath).Equals(".hoshino", StringComparison.OrdinalIgnoreCase)) LoadProject(initialPath);
                else _ = AddFilesAsync([initialPath]);
            }, DispatcherPriority.Loaded);
        }
    }

    public void Open() { if (!IsBusy) AddMedia_Click(this, new RoutedEventArgs()); }
    public void Save() { if (!IsBusy) SaveProject_Click(this, new RoutedEventArgs()); }
    public void Undo() { if (!IsBusy) Undo_Click(this, new RoutedEventArgs()); }
    public void Redo() { if (!IsBusy) Redo_Click(this, new RoutedEventArgs()); }
    public void TogglePlayback() { if (!IsBusy) Play_Click(this, new RoutedEventArgs()); }
    public void CancelActiveTool()
    {
        if (_exportCts is not null) { CancelExport(); return; }
        if (_importCts is not null) { _importCts.Cancel(); SetStatus("Canceling importâ€¦"); return; }
        if (_isPlaying) Pause();
    }
    public void RefreshSettings()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(SettingsService.Current.AutoSaveMinutes, 1, 30));
        if (SettingsService.Current.AutoSaveVideoProjects) _autoSaveTimer.Start();
    }
    public bool CanClose()
    {
        if (_exportCts is not null && MessageBox.Show("Cancel the video export and close this project?", "Hoshino Editor",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        if (!_projectDirty) return true;
        var choice = MessageBox.Show("Save this video project before closing it?", "Hoshino Editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.No) { _discardChanges = true; return true; }
        SaveProject_Click(this, new RoutedEventArgs());
        return !_projectDirty;
    }
    public void Close()
    {
        _closed = true;
        Interlocked.Increment(ref _workspaceVersion);
        _importCts?.Cancel();
        _exportCts?.Cancel();
        _playbackTimer.Stop();
        _scrubSeekTimer.Stop();
        _autoSaveTimer.Stop();
        if (_discardChanges && _ownsRecovery) VideoProjectService.DeleteRecovery();
        Player.Stop();
        Player.Source = null;
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var valid = paths.Where(File.Exists).Where(p => MediaTypeService.VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (valid.Length == 0) return;
        _importCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _importCts = cancellation;
        UpdateAvailability();
        var version = _workspaceVersion;
        var imported = new List<VideoClipItem>();
        var failures = new List<string>();
        SetStatus("Reading clip information…");
        try
        {
            foreach (var path in valid)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var duration = await VideoExportService.GetDurationAsync(path, cancellation.Token);
                    var seconds = Math.Max(.1, duration.TotalSeconds);
                    imported.Add(new VideoClipItem { Path = path, SourceDuration = seconds, InPoint = 0, OutPoint = seconds });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed || version != _workspaceVersion || !ReferenceEquals(_importCts, cancellation)) return;
            if (imported.Count == 0)
            {
                ToastRequested?.Invoke(this, new ToastMessage(failures.Count == 0
                    ? "No supported video clips were selected."
                    : $"The selected video could not be read. {failures[0]}", true));
                SetStatus("No clips imported");
                return;
            }
            PushUndo();
            foreach (var clip in imported) AddClip(clip);
            if (_selectedClip is null) SelectClip(Clips[0]);
            EmptyState.Visibility = Visibility.Collapsed;
            PlayerFrame.Visibility = Visibility.Visible;
            UpdateProjectSummary();
            TitleChanged?.Invoke(this, Title);
            SetStatus($"{Clips.Count} clip{(Clips.Count == 1 ? "" : "s")}  ·  {VideoClipItem.FormatTime(Clips.Sum(c => c.EditedDuration))}");
            if (failures.Count > 0)
                ToastRequested?.Invoke(this, new ToastMessage($"Imported {imported.Count} clip{(imported.Count == 1 ? "" : "s")}; {failures.Count} could not be read.", true));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Media import failed: {ex.Message}", true)); }
        finally
        {
            if (ReferenceEquals(_importCts, cancellation)) _importCts = null;
            cancellation.Dispose();
            UpdateAvailability();
        }
    }

    private void AddClip(VideoClipItem clip, int? index = null)
    {
        clip.TimelineScale = TimelineZoomSlider?.Value / 100 ?? 1;
        clip.PropertyChanged += Clip_PropertyChanged;
        if (index is null) Clips.Add(clip); else Clips.Insert(index.Value, clip);
    }

    private void Clip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VideoClipItem.TimelineScale)) _projectDirty = true;
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
        ReleasePreview();
        if (!File.Exists(clip.Path))
        {
            _playWhenReady = false;
            Scrubber.Minimum = clip.InPoint;
            Scrubber.Maximum = Math.Max(clip.InPoint + .05, clip.OutPoint);
            Scrubber.Value = clip.InPoint;
            Scrubber.IsEnabled = false;
            PreviewLabel.Text = "SOURCE MISSING";
            UpdateTimeText(clip.InPoint);
            return;
        }
        _playWhenReady = autoplay;
        Player.Source = new Uri(clip.Path, UriKind.Absolute);
        Player.SpeedRatio = clip.Speed;
        Player.Volume = VolumeSlider.Value * clip.Volume;
        Player.IsMuted = _masterMuted || clip.IsMuted;
        Player.Position = TimeSpan.FromSeconds(clip.InPoint);
        Scrubber.Minimum = clip.InPoint; Scrubber.Maximum = Math.Max(clip.InPoint + .05, clip.OutPoint); Scrubber.Value = clip.InPoint; Scrubber.IsEnabled = true;
        PreviewLabel.Text = clip.Speed == 1 ? "PREVIEW" : $"PREVIEW  ·  {clip.Speed:0.##}×";
        UpdateTimeText(clip.InPoint);
    }

    private void ReleasePreview()
    {
        Pause();
        Player.Stop();
        Player.Source = null;
    }

    private void SyncInspector()
    {
        _syncingInspector = true;
        VideoInspectorControls.IsEnabled = _selectedClip is not null;
        if (_selectedClip is null)
        {
            InspectorName.Text = "Select a clip"; InspectorMeta.Text = "Trim and speed controls will appear here.";
            InSlider.IsEnabled = OutSlider.IsEnabled = false;
        }
        else
        {
            var c = _selectedClip;
            InspectorName.Text = c.Name;
            InspectorMeta.Text = File.Exists(c.Path)
                ? $"Source {VideoClipItem.FormatTime(c.SourceDuration)}  ·  Edited {VideoClipItem.FormatTime(c.EditedDuration)}  ·  {c.Speed:0.##}×"
                : $"Source missing  ·  Edited {VideoClipItem.FormatTime(c.EditedDuration)}  ·  {c.Speed:0.##}×";
            InSlider.Minimum = 0; InSlider.Maximum = Math.Max(.1, c.SourceDuration); InSlider.Value = c.InPoint; InSlider.IsEnabled = true;
            OutSlider.Minimum = 0; OutSlider.Maximum = Math.Max(.1, c.SourceDuration); OutSlider.Value = c.OutPoint; OutSlider.IsEnabled = true;
            InValue.Text = FormatPrecise(c.InPoint); OutValue.Text = FormatPrecise(c.OutPoint);
            ClipVolumeSlider.Value = c.Volume * 100; ClipMuteCheck.IsChecked = c.IsMuted;
            var maximumFade = Math.Clamp(c.EditedDuration, .1, 10);
            FadeInSlider.Maximum = FadeOutSlider.Maximum = maximumFade;
            FadeInSlider.Value = Math.Min(c.FadeIn, maximumFade); FadeOutSlider.Value = Math.Min(c.FadeOut, maximumFade);
            VideoFadeInSlider.Maximum = VideoFadeOutSlider.Maximum = maximumFade;
            VideoFadeInSlider.Value = Math.Min(c.VideoFadeIn, maximumFade); VideoFadeOutSlider.Value = Math.Min(c.VideoFadeOut, maximumFade);
            UpdateClipAudioLabels();
            UpdateClipVisualLabels();
        }
        _syncingInspector = false;
        UpdateAvailability();
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
    private void InspectorEdit_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_selectedClip is null || _syncingInspector || e.IsRepeat) return;
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space)
            PushUndo();
    }
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

    private void TimelineZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TimelineZoomSlider is null) return;
        var scale = TimelineZoomSlider.Value / 100;
        foreach (var clip in Clips) clip.TimelineScale = scale;
    }

    private void ClipAudio_ValueChanged(object sender, RoutedEventArgs e)
    {
        // ValueChanged can fire while InitializeComponent is still materializing
        // the inspector. Do not touch controls that appear later in the XAML tree.
        if (!IsInitialized) return;
        UpdateClipAudioLabels();
        if (_syncingInspector || _selectedClip is null) return;
        _selectedClip.Volume = ClipVolumeSlider.Value / 100;
        _selectedClip.IsMuted = ClipMuteCheck.IsChecked == true;
        _selectedClip.FadeIn = FadeInSlider.Value;
        _selectedClip.FadeOut = FadeOutSlider.Value;
        Player.Volume = VolumeSlider.Value * _selectedClip.Volume;
        Player.IsMuted = _selectedClip.IsMuted || _masterMuted;
    }

    private void UpdateClipAudioLabels()
    {
        if (ClipVolumeValue is null || ClipVolumeSlider is null ||
            FadeInValue is null || FadeInSlider is null ||
            FadeOutValue is null || FadeOutSlider is null) return;
        ClipVolumeValue.Text = $"{ClipVolumeSlider.Value:0}%";
        FadeInValue.Text = $"{FadeInSlider.Value:0.0}s";
        FadeOutValue.Text = $"{FadeOutSlider.Value:0.0}s";
    }

    private void ClipVisual_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        UpdateClipVisualLabels();
        if (_syncingInspector || _selectedClip is null) return;
        _selectedClip.VideoFadeIn = VideoFadeInSlider.Value;
        _selectedClip.VideoFadeOut = VideoFadeOutSlider.Value;
    }

    private void UpdateClipVisualLabels()
    {
        if (VideoFadeInValue is null || VideoFadeInSlider is null ||
            VideoFadeOutValue is null || VideoFadeOutSlider is null) return;
        VideoFadeInValue.Text = $"{VideoFadeInSlider.Value:0.0}s";
        VideoFadeOutValue.Text = $"{VideoFadeOutSlider.Value:0.0}s";
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
        if (Clips.Count == 0) { _selectedClip = null; PlayerFrame.Visibility = Visibility.Collapsed; EmptyState.Visibility = Visibility.Visible; ReleasePreview(); SyncInspector(); }
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
        UpdateTimeText(Scrubber.Value);
        _scrubSeekTimer.Stop();
        _scrubSeekTimer.Start();
    }
    private void Scrubber_DragStarted(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = true;
        _resumeAfterScrub = _isPlaying;
        if (_isPlaying) Pause();
    }
    private void Scrubber_DragEnded(object sender, MouseButtonEventArgs e)
    {
        _scrubSeekTimer.Stop();
        if (_selectedClip is not null) { Player.Position = TimeSpan.FromSeconds(Scrubber.Value); UpdateTimeText(Scrubber.Value); }
        _scrubbing = false;
        if (_resumeAfterScrub) Play();
        _resumeAfterScrub = false;
    }
    private void Scrubber_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubSeekTimer.Stop();
        _scrubbing = false;
        _resumeAfterScrub = false;
    }
    private void ScrubSeekTimer_Tick(object? sender, EventArgs e)
    {
        _scrubSeekTimer.Stop();
        if (_scrubbing && _selectedClip is not null) Player.Position = TimeSpan.FromSeconds(Scrubber.Value);
    }

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
        if (Player.Source is null || !Path.GetFullPath(Player.Source.LocalPath).Equals(Path.GetFullPath(_selectedClip.Path), StringComparison.OrdinalIgnoreCase)) return;
        if (Player.NaturalDuration.HasTimeSpan && (_selectedClip.SourceDuration <= 1 || Math.Abs(Player.NaturalDuration.TimeSpan.TotalSeconds - _selectedClip.SourceDuration) > .5))
        {
            _selectedClip.SourceDuration = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (_selectedClip.OutPoint <= 1) _selectedClip.OutPoint = _selectedClip.SourceDuration;
            SyncInspector();
        }
        Player.Position = TimeSpan.FromSeconds(_selectedClip.InPoint);
        if (_playWhenReady) { _playWhenReady = false; Play(); }
    }
    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _playWhenReady = false;
        Pause();
        ToastRequested?.Invoke(this, new ToastMessage($"Preview couldn't decode this clip: {e.ErrorException?.Message}", true));
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Player is not null) { Player.Volume = VolumeSlider.Value * (_selectedClip?.Volume ?? 1); Player.IsMuted = _masterMuted || _selectedClip?.IsMuted == true; } }
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _masterMuted = !_masterMuted;
        Player.IsMuted = _masterMuted || _selectedClip?.IsMuted == true;
        MuteButton.Content = _masterMuted ? "×" : "♪";
        MuteButton.ToolTip = _masterMuted ? "Unmute" : "Mute";
        AutomationProperties.SetName(MuteButton, _masterMuted ? "Unmute preview" : "Mute preview");
    }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncingSelection && MediaList.SelectedItem is VideoClipItem clip) SelectClip(clip); }
    private void TimelineList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncingSelection && TimelineList.SelectedItem is VideoClipItem clip) SelectClip(clip); }

    private void Timeline_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _timelineDragStart = e.GetPosition(TimelineList);
    private void Timeline_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(TimelineList);
        if (Math.Abs(point.X - _timelineDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _timelineDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not VideoClipItem clip) return;
        DragDrop.DoDragDrop(container, new DataObject("Hoshino.VideoClip", clip.Id), DragDropEffects.Move);
    }
    private void Timeline_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("Hoshino.VideoClip") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private void Timeline_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("Hoshino.VideoClip") || e.Data.GetData("Hoshino.VideoClip") is not string id) return;
        var clip = Clips.FirstOrDefault(item => item.Id == id); if (clip is null) return;
        var targetContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var oldIndex = Clips.IndexOf(clip);
        var newIndex = Clips.Count - 1;
        if (targetContainer?.DataContext is VideoClipItem target)
        {
            var insertionIndex = Clips.IndexOf(target) + (e.GetPosition(targetContainer).X > targetContainer.ActualWidth / 2 ? 1 : 0);
            if (oldIndex < insertionIndex) insertionIndex--;
            newIndex = Math.Clamp(insertionIndex, 0, Clips.Count - 1);
        }
        if (oldIndex == newIndex) return;
        PushUndo(); Clips.Move(oldIndex, newIndex); SelectClip(clip); TimelineList.ScrollIntoView(clip); e.Handled = true;
    }
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null && current is not T) current = VisualTreeHelper.GetParent(current);
        return current as T;
    }

    private void PushUndo()
    {
        _undo.Push(CaptureSnapshot()); TrimStack(_undo, Math.Clamp(SettingsService.Current.UndoLimit, 1, 100));
        _redo.Clear(); UpdateHistoryButtons();
        _projectDirty = true;
    }
    private static void TrimStack(Stack<ProjectSnapshot> stack, int limit)
    {
        if (stack.Count <= limit) return;
        var newestFirst = stack.ToArray();
        stack.Clear();
        for (var index = Math.Min(limit, newestFirst.Length) - 1; index >= 0; index--) stack.Push(newestFirst[index]);
    }
    private ProjectSnapshot CaptureSnapshot() => new(Clips.Select(c => c.Clone()).ToList(), _selectedClip is null ? -1 : Clips.IndexOf(_selectedClip));
    private void ApplySnapshot(ProjectSnapshot snapshot)
    {
        Pause(); foreach (var c in Clips) c.PropertyChanged -= Clip_PropertyChanged; Clips.Clear(); foreach (var c in snapshot.Clips) AddClip(c.Clone());
        if (Clips.Count > 0) { EmptyState.Visibility = Visibility.Collapsed; PlayerFrame.Visibility = Visibility.Visible; SelectClip(Clips[Math.Clamp(snapshot.SelectedIndex, 0, Clips.Count - 1)]); }
        else { _selectedClip = null; EmptyState.Visibility = Visibility.Visible; PlayerFrame.Visibility = Visibility.Collapsed; ReleasePreview(); SyncInspector(); }
        UpdateProjectSummary();
    }
    private void Undo_Click(object sender, RoutedEventArgs e) { if (_undo.Count == 0) return; _redo.Push(CaptureSnapshot()); ApplySnapshot(_undo.Pop()); _projectDirty = true; UpdateHistoryButtons(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (_redo.Count == 0) return; _undo.Push(CaptureSnapshot()); ApplySnapshot(_redo.Pop()); _projectDirty = true; UpdateHistoryButtons(); }
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
        var targetPath = _projectPath;
        if (targetPath is null)
        {
            var dialog = new SaveFileDialog { Title = "Save Hoshino project", Filter = "Hoshino project|*.hoshino", DefaultExt = ".hoshino", FileName = "Untitled video.hoshino", AddExtension = true };
            if (dialog.ShowDialog() != true) return;
            targetPath = dialog.FileName;
        }
        try
        {
            var projectName = _projectPath is null ? Path.GetFileNameWithoutExtension(targetPath) : Title;
            VideoProjectService.Save(targetPath, projectName, Clips);
            _projectPath = targetPath; _projectDirty = false; _recoveredTitle = null;
            if (_ownsRecovery) { VideoProjectService.DeleteRecovery(); _ownsRecovery = false; }
            TitleChanged?.Invoke(this, Title); SetStatus($"Project saved  ·  {Path.GetFileName(_projectPath)}"); ToastRequested?.Invoke(this, new ToastMessage("Project saved."));
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Couldn't save the project: {ex.Message}", true)); }
    }

    private void LoadProject(string path)
    {
        try
        {
            var project = VideoProjectService.Load(path);
            var networkSourceCount = project.Clips.Count(item => VideoProjectService.IsNetworkOrDevicePath(item.Path));
            if (networkSourceCount > 0 && MessageBox.Show(
                    $"This project references {networkSourceCount} source file{(networkSourceCount == 1 ? "" : "s")} on a network or device path. Connect to those locations?",
                    "Open network sources?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            var replacement = project.Clips.Select(item => new VideoClipItem
            {
                Path = item.Path, SourceDuration = item.SourceDuration, InPoint = item.InPoint, OutPoint = item.OutPoint, Speed = item.Speed,
                Volume = item.Volume, IsMuted = item.IsMuted, FadeIn = item.FadeIn, FadeOut = item.FadeOut,
                VideoFadeIn = item.VideoFadeIn, VideoFadeOut = item.VideoFadeOut
            }).ToList();
            var missingCount = project.Clips.Count(item => !File.Exists(item.Path));
            if (Clips.Count > 0 && _projectDirty && !CanClose()) return;
            if (_discardChanges && _ownsRecovery) VideoProjectService.DeleteRecovery();
            _discardChanges = false; _ownsRecovery = false; _recoveredTitle = null;
            _importCts?.Cancel();
            Interlocked.Increment(ref _workspaceVersion);
            Pause(); _undo.Clear(); _redo.Clear(); UpdateHistoryButtons(); foreach (var c in Clips) c.PropertyChanged -= Clip_PropertyChanged; Clips.Clear();
            foreach (var item in replacement) AddClip(item);
            var isRecovery = VideoProjectService.IsRecoveryPath(path);
            _projectPath = isRecovery ? null : path; _ownsRecovery = isRecovery; _recoveredTitle = isRecovery ? project.Name : null;
            if (Clips.Count > 0) SelectClip(Clips[0]);
            else { _selectedClip = null; ReleasePreview(); SyncInspector(); }
            EmptyState.Visibility = Clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PlayerFrame.Visibility = Clips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _projectDirty = isRecovery; TitleChanged?.Invoke(this, Title); UpdateProjectSummary(); SetStatus(isRecovery ? $"Recovered autosave  ·  {Clips.Count} clips" : $"Project loaded  ·  {Clips.Count} clips");
            if (missingCount > 0) ToastRequested?.Invoke(this, new ToastMessage($"Loaded the project, but {missingCount} source clip{(missingCount == 1 ? " is" : "s are")} missing.", true));
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Couldn't open the project: {ex.Message}", true)); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        if (Clips.Count == 0) { ToastRequested?.Invoke(this, new ToastMessage("Add at least one clip before exporting.", true)); return; }
        var dialog = new SaveFileDialog { Title = "Export video", Filter = "MP4 video|*.mp4", DefaultExt = ".mp4", FileName = $"{Path.GetFileNameWithoutExtension(Title)}-export.mp4", AddExtension = true };
        if (SettingsService.Current.RememberExportFolder && Directory.Exists(SettingsService.Current.LastExportFolder)) dialog.InitialDirectory = SettingsService.Current.LastExportFolder;
        if (dialog.ShowDialog() != true) return;
        if (Clips.Any(clip => Path.GetFullPath(clip.Path).Equals(Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)))
        {
            ToastRequested?.Invoke(this, new ToastMessage("Choose an export filename that does not replace a source clip.", true));
            return;
        }
        var exportClips = Clips.Select(clip => clip.Clone()).ToList();
        Pause();
        _focusBeforeExport = Keyboard.FocusedElement;
        ExportOverlay.Visibility = Visibility.Visible; ExportButton.IsEnabled = false; CancelExportButton.IsEnabled = true; ExportProgress.Value = 0;
        CancelExportButton.Focus();
        _exportCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(value => { ExportProgress.Value = value; ExportStatusText.Text = $"Rendering… {value:0}%"; });
            await VideoExportService.ExportAsync(exportClips, dialog.FileName, VideoUpscaleSlider.Value / 100,
                SettingsService.Current.PreferGpuAcceleration, progress, _exportCts.Token);
            SetStatus($"Exported  ·  {Path.GetFileName(dialog.FileName)}"); ToastRequested?.Invoke(this, new ToastMessage("Video exported."));
            SettingsService.Current.LastExportFolder = Path.GetDirectoryName(dialog.FileName);
            try { SettingsService.Save(); }
            catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"The video exported, but the export folder preference could not be saved: {ex.Message}", true)); }
        }
        catch (OperationCanceledException) { SetStatus("Export canceled"); ToastRequested?.Invoke(this, new ToastMessage("Export canceled.")); }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Export failed: {ex.Message}", true)); }
        finally
        {
            _exportCts.Dispose(); _exportCts = null;
            ExportOverlay.Visibility = Visibility.Collapsed;
            UpdateAvailability();
            if (!_closed) _focusBeforeExport?.Focus();
            _focusBeforeExport = null;
        }
    }
    private void CancelExport_Click(object sender, RoutedEventArgs e)
        => CancelExport();
    private void CancelExport()
    {
        CancelExportButton.IsEnabled = false;
        ExportStatusText.Text = "Canceling...";
        _exportCts?.Cancel();
    }

    private void UpdateProjectSummary()
    {
        if (ProjectDurationText is null) return; var duration = Clips.Sum(c => c.EditedDuration); ProjectDurationText.Text = $"{Clips.Count} CLIPS  ·  {VideoClipItem.FormatTime(duration)}"; SetStatus($"{Clips.Count} clip{(Clips.Count == 1 ? "" : "s")}  ·  {VideoClipItem.FormatTime(duration)}");
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        if (SaveProjectButton is null) return;
        var hasClips = Clips.Count > 0;
        var hasSelection = _selectedClip is not null;
        var editable = !IsBusy;
        SaveProjectButton.IsEnabled = hasClips && editable;
        ExportButton.IsEnabled = hasClips && editable;
        VideoInspectorControls.IsEnabled = hasSelection && editable;
        TimelineActionControls.IsEnabled = hasSelection && editable;
        PreviewAudioControls.IsEnabled = hasSelection && editable;
        PreviousButton.IsEnabled = PlayButton.IsEnabled = NextButton.IsEnabled = hasSelection && editable;
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
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths is { Length: 1 } && Path.GetExtension(paths[0]).Equals(".hoshino", StringComparison.OrdinalIgnoreCase)) LoadProject(paths[0]);
        else if (paths is not null) _ = AddFilesAsync(paths);
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        if (!SettingsService.Current.AutoSaveVideoProjects || !_projectDirty || Clips.Count == 0) return;
        try
        {
            if (_projectPath is null)
            {
                VideoProjectService.SaveRecovery(_recoveredTitle ?? Title, Clips); _ownsRecovery = true;
                SetStatus($"Recovery autosaved  ·  {DateTime.Now:t}");
            }
            else
            {
                VideoProjectService.Save(_projectPath, Title, Clips); _projectDirty = false;
                SetStatus($"Autosaved  ·  {DateTime.Now:t}");
            }
        }
        catch (Exception ex) { SetStatus($"Autosave failed  ·  {ex.Message}"); }
    }
}
