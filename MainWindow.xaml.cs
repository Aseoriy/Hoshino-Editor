using HoshinoEditor.Controls;
using HoshinoEditor.Services;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Documents;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Controls.Primitives;

namespace HoshinoEditor;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private IEditorWorkspace? _workspace;
    private object? _contentBeforeSettings;
    private bool _settingsOpen;
    private UpdateRelease? _availableUpdate;
    private CancellationTokenSource? _updateCts;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _installingUpdate;
    private bool _checkingForUpdates;
    private IInputElement? _focusBeforeOverlay;

    public MainWindow()
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { ShellIntegrationService.EnsureRegistered(); }
        catch { /* Editing must still work if registry policy blocks registration. */ }

        ApplyWindowSettings();
        if (!string.IsNullOrWhiteSpace(App.StartupFile)) await OpenFileAsync(App.StartupFile);
        else if (SettingsService.Current.RestoreLastSession && File.Exists(VideoProjectService.RecoveryPath)) OpenVideo(VideoProjectService.RecoveryPath);
        else ShowStart();
        if (SettingsService.Current.CheckForUpdates) _ = CheckForUpdatesAsync();
    }

    public void ShowStart()
    {
        if (!TryCloseWorkspace()) return;
        _workspace = null;
        DocumentTitle.Text = "  /  New project";
        StatusText.Text = "Ready";
        var start = new StartWorkspace();
        start.OpenRequested += async (_, path) => await OpenFileAsync(path);
        start.NewPhotoRequested += (_, _) => OpenPhoto(null);
        start.NewVideoRequested += (_, _) => OpenVideo(null);
        WorkspaceHost.Content = start;
    }

    public async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            ShowToast("That file no longer exists.", true);
            if (WorkspaceHost.Content is null) ShowStart();
            return;
        }
        switch (MediaTypeService.GetKind(path))
        {
            case EditorKind.Photo: OpenPhoto(path); break;
            case EditorKind.Video: OpenVideo(path); break;
            default:
                ShowToast("Hoshino doesn't support that file type yet.", true);
                if (WorkspaceHost.Content is null) ShowStart();
                return;
        }
        await Task.CompletedTask;
    }

    private void OpenPhoto(string? path)
    {
        if (!TryCloseWorkspace()) return;
        var workspace = new PhotoWorkspace(path);
        HookWorkspace(workspace);
        WorkspaceHost.Content = workspace;
    }

    private void OpenVideo(string? path)
    {
        if (!TryCloseWorkspace()) return;
        var workspace = new VideoWorkspace(path);
        HookWorkspace(workspace);
        WorkspaceHost.Content = workspace;
    }

    private void HookWorkspace(IEditorWorkspace workspace)
    {
        _workspace = workspace;
        workspace.TitleChanged += (_, title) => DocumentTitle.Text = $"  /  {title}";
        workspace.StatusChanged += (_, status) => StatusText.Text = status;
        workspace.HomeRequested += (_, _) => ShowStart();
        workspace.ToastRequested += (_, toast) => ShowToast(toast.Message, toast.IsError);
        DocumentTitle.Text = $"  /  {workspace.Title}";
        StatusText.Text = workspace.Status;
    }

    public void ShowToast(string message, bool isError = false)
    {
        ToastText.Text = message;
        Toast.BorderBrush = (Brush)FindResource(isError ? "Danger" : "AccentLine");
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (UpdateOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape && _updateCts is null) CloseUpdateOverlay();
            e.Handled = true;
            return;
        }
        if (_settingsOpen)
        {
            if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; }
            return;
        }
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            if (e.Key == Key.Escape) _workspace?.CancelActiveTool();
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && FocusIsInsideInteractiveControl())
        {
            if (e.Key == Key.Escape) _workspace?.CancelActiveTool();
            return;
        }
        if (ShortcutService.Matches("Open", e))
        {
            _workspace?.Open();
            if (_workspace is null && WorkspaceHost.Content is StartWorkspace start) start.Open();
            e.Handled = true;
        }
        else if (ShortcutService.Matches("Save", e)) { _workspace?.Save(); e.Handled = true; }
        else if (ShortcutService.Matches("Undo", e)) { _workspace?.Undo(); e.Handled = true; }
        else if (ShortcutService.Matches("Redo", e)) { _workspace?.Redo(); e.Handled = true; }
        else if (ShortcutService.Matches("ZoomIn", e)) { if (_workspace is PhotoWorkspace photo) photo.ZoomIn(); e.Handled = _workspace is PhotoWorkspace; }
        else if (ShortcutService.Matches("ZoomOut", e)) { if (_workspace is PhotoWorkspace photo) photo.ZoomOut(); e.Handled = _workspace is PhotoWorkspace; }
        else if (ShortcutService.Matches("ResetZoom", e)) { if (_workspace is PhotoWorkspace photo) photo.ResetZoom(); e.Handled = _workspace is PhotoWorkspace; }
        else if (ShortcutService.Matches("FitComposition", e)) { if (_workspace is PhotoWorkspace photo) photo.FitComposition(); e.Handled = _workspace is PhotoWorkspace; }
        else if (ShortcutService.Matches("Settings", e)) { OpenSettings(); e.Handled = true; }
        else if (ShortcutService.Matches("DeleteLayer", e)) { if (_workspace is PhotoWorkspace photo) photo.DeleteSelectedLayer(); e.Handled = _workspace is PhotoWorkspace; }
        else if (ShortcutService.Matches("MoveTool", e)) { if (_workspace is PhotoWorkspace photo) photo.ActivateMoveTool(); e.Handled = _workspace is PhotoWorkspace; }
        else if (ShortcutService.Matches("PanTool", e)) { if (_workspace is PhotoWorkspace photo) photo.ActivatePanTool(); e.Handled = _workspace is PhotoWorkspace; }
        else if (_workspace is PhotoWorkspace canvas && e.Key is Key.Left or Key.Right or Key.Up or Key.Down &&
                 Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var amount = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            canvas.NudgeSelected(e.Key == Key.Left ? -amount : e.Key == Key.Right ? amount : 0,
                e.Key == Key.Up ? -amount : e.Key == Key.Down ? amount : 0);
            e.Handled = true;
        }
        else if (ShortcutService.Matches("PlayPause", e)) { _workspace?.TogglePlayback(); e.Handled = _workspace is VideoWorkspace; }
        else if (e.Key == Key.Escape) _workspace?.CancelActiveTool();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Maximize_Click(sender, e); else DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Current.CloseButtonAction.Equals("Minimize", StringComparison.OrdinalIgnoreCase)) WindowState = WindowState.Minimized;
        else Close();
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void OpenSettings()
    {
        if (_settingsOpen) return;
        if (_workspace?.IsBusy == true) { ShowToast("Finish or cancel the current operation before opening Settings.", true); return; }
        _workspace?.CancelActiveTool();
        _settingsOpen = true;
        _contentBeforeSettings = WorkspaceHost.Content;
        var settings = new SettingsWorkspace();
        settings.DoneRequested += (_, _) => CloseSettings();
        settings.CheckUpdatesRequested += async (_, _) => await CheckForUpdatesAsync(true);
        WorkspaceHost.Content = settings;
        DocumentTitle.Text = "  /  Settings";
        StatusText.Text = $"Hoshino Editor v{UpdateService.CurrentVersion}  ·  Sail Solutions";
    }
    private void CloseSettings()
    {
        if (!_settingsOpen) return;
        PersistSettings(showErrors: true);
        _settingsOpen = false;
        WorkspaceHost.Content = _contentBeforeSettings;
        _contentBeforeSettings = null;
        DocumentTitle.Text = $"  /  {_workspace?.Title ?? "New project"}";
        StatusText.Text = _workspace?.Status ?? "Ready";
        ApplyWindowSettings();
        _workspace?.RefreshSettings();
    }

    private void PersistSettings(bool showErrors)
    {
        try { SettingsService.Save(); }
        catch (Exception ex) { if (showErrors) ShowToast($"Settings could not be saved: {ex.Message}", true); }
        try { StartupService.SetStartWithWindows(SettingsService.Current.StartWithWindows); }
        catch (Exception ex) { if (showErrors) ShowToast($"The Windows startup setting could not be changed: {ex.Message}", true); }
    }
    private void ApplyWindowSettings()
    {
        System.Windows.Controls.ToolTipService.SetIsEnabled(this, SettingsService.Current.ShowTooltips);
        StatusBar.Visibility = SettingsService.Current.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        StatusRow.Height = SettingsService.Current.ShowStatusBar ? new GridLength(28) : new GridLength(0);
    }

    private async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (_checkingForUpdates)
        {
            if (manual) ShowToast("An update check is already running.");
            return;
        }
        _checkingForUpdates = true;
        try
        {
            var includePrereleases = SettingsService.Current.IncludeBetaUpdates || UpdateService.CurrentVersion.Contains('-', StringComparison.Ordinal);
            _availableUpdate = await UpdateService.CheckAsync(includePrereleases, _lifetimeCts.Token);
            if (_availableUpdate is null)
            {
                if (manual) ShowToast($"Hoshino Editor {UpdateService.CurrentVersion} is up to date.");
                return;
            }
            if (_workspace?.IsBusy == true)
            {
                if (manual) ShowToast($"Hoshino Editor {_availableUpdate.Version} is available. Finish the current operation, then check again.");
                return;
            }
            UpdateTitle.Text = _availableUpdate.Name;
            UpdateVersionText.Text = $"Version {_availableUpdate.Version} is ready · currently {UpdateService.CurrentVersion}";
            UpdateNotes.Document = BuildReleaseDocument(_availableUpdate.Markdown);
            UpdateProgress.Value = 0;
            UpdateProgress.Visibility = UpdateProgressText.Visibility = Visibility.Collapsed;
            UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = true;
            _focusBeforeOverlay = Keyboard.FocusedElement;
            WorkspaceHost.IsEnabled = false;
            UpdateOverlay.Visibility = Visibility.Visible;
            UpdateNowButton.Focus();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (manual) ShowToast($"Update check failed: {ex.Message}", true);
        }
        finally { _checkingForUpdates = false; }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _updateCts is not null) return;
        _updateCts = new CancellationTokenSource();
        UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = false;
        UpdateProgress.Visibility = UpdateProgressText.Visibility = Visibility.Visible;
        UpdateProgressText.Text = "Downloading installer…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateProgress.Value = value;
                UpdateProgressText.Text = $"Downloading installer… {value:0}%";
            });
            var installer = await UpdateService.DownloadInstallerAsync(_availableUpdate, progress, _updateCts.Token);
            if (!TryCloseWorkspace())
            {
                UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = true;
                UpdateProgressText.Text = "Update ready. Save or discard your work to continue.";
                return;
            }
            _workspace = null;
            UpdateProgressText.Text = "Starting installer…";
            UpdateService.LaunchInstaller(installer, _availableUpdate.Sha256);
            _installingUpdate = true;
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowToast($"Update failed: {ex.Message}", true);
            UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = true;
            UpdateProgressText.Text = "The update could not continue. You can try again.";
            if (_workspace is null && !_installingUpdate)
            {
                CloseUpdateOverlay();
                ShowStart();
            }
        }
        finally
        {
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => CloseUpdateOverlay();

    private void CloseUpdateOverlay()
    {
        UpdateOverlay.Visibility = Visibility.Collapsed;
        WorkspaceHost.IsEnabled = true;
        _focusBeforeOverlay?.Focus();
        _focusBeforeOverlay = null;
    }

    private static FlowDocument BuildReleaseDocument(string markdown)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 12, LineHeight = 19 };
        System.Windows.Documents.List? activeList = null;
        TextMarkerStyle? activeMarker = null;
        var code = new System.Text.StringBuilder();
        var inCode = false;
        void FlushCode()
        {
            if (code.Length == 0) return;
            document.Blocks.Add(new Paragraph(new Run(code.ToString().TrimEnd()))
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 11, Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Padding = new Thickness(10), Margin = new Thickness(0, 5, 0, 9)
            });
            code.Clear();
        }
        foreach (var raw in markdown.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```")) { if (inCode) FlushCode(); inCode = !inCode; activeList = null; activeMarker = null; continue; }
            if (inCode) { code.AppendLine(line); continue; }
            if (string.IsNullOrWhiteSpace(line)) { activeList = null; activeMarker = null; continue; }
            if (line.StartsWith('#'))
            {
                activeList = null; activeMarker = null;
                var level = line.TakeWhile(ch => ch == '#').Count();
                var paragraph = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = Math.Max(14, 23 - level * 2), Margin = new Thickness(0, 10, 0, 5) };
                AddMarkdownInlines(paragraph.Inlines, line[level..].Trim());
                document.Blocks.Add(paragraph);
            }
            else if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                if (activeList is null || activeMarker != TextMarkerStyle.Disc) { activeMarker = TextMarkerStyle.Disc; activeList = new System.Windows.Documents.List { MarkerStyle = activeMarker.Value, Margin = new Thickness(18, 3, 0, 7) }; document.Blocks.Add(activeList); }
                var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddMarkdownInlines(paragraph.Inlines, line.TrimStart()[2..]);
                activeList.ListItems.Add(new ListItem(paragraph));
            }
            else if (System.Text.RegularExpressions.Regex.Match(line.TrimStart(), @"^\d+\.\s+") is { Success: true } numbered)
            {
                if (activeList is null || activeMarker != TextMarkerStyle.Decimal) { activeMarker = TextMarkerStyle.Decimal; activeList = new System.Windows.Documents.List { MarkerStyle = activeMarker.Value, Margin = new Thickness(18, 3, 0, 7) }; document.Blocks.Add(activeList); }
                var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddMarkdownInlines(paragraph.Inlines, line.TrimStart()[numbered.Length..]);
                activeList.ListItems.Add(new ListItem(paragraph));
            }
            else if (line.TrimStart().StartsWith('>'))
            {
                activeList = null; activeMarker = null;
                var paragraph = new Paragraph { Margin = new Thickness(12, 5, 0, 9), Padding = new Thickness(10, 2, 0, 2),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(100, 168, 85, 247)), BorderThickness = new Thickness(3, 0, 0, 0) };
                AddMarkdownInlines(paragraph.Inlines, line.TrimStart()[1..].TrimStart());
                document.Blocks.Add(paragraph);
            }
            else if (line.Trim() is "---" or "***")
            {
                activeList = null; activeMarker = null;
                document.Blocks.Add(new Paragraph { BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 8, 0, 8) });
            }
            else
            {
                activeList = null; activeMarker = null;
                var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 7) };
                AddMarkdownInlines(paragraph.Inlines, line);
                document.Blocks.Add(paragraph);
            }
        }
        FlushCode();
        return document;
    }

    private static void AddMarkdownInlines(InlineCollection inlines, string text)
    {
        var index = 0;
        var pattern = new System.Text.RegularExpressions.Regex(@"(\*\*.+?\*\*|~~.+?~~|`.+?`|(?<!_)_[^_]+_(?!_)|\[[^\]]+\]\(https?://[^)]+\))");
        foreach (System.Text.RegularExpressions.Match match in pattern.Matches(text))
        {
            if (match.Index > index) inlines.Add(new Run(text[index..match.Index]));
            var token = match.Value;
            if (token.StartsWith("**")) inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith("~~")) inlines.Add(new Run(token[2..^2]) { TextDecorations = TextDecorations.Strikethrough });
            else if (token.StartsWith('`')) inlines.Add(new Run(token[1..^1]) { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)) });
            else if (token.StartsWith('_')) inlines.Add(new Italic(new Run(token[1..^1])));
            else
            {
                var split = token.IndexOf("](", StringComparison.Ordinal);
                var link = new Hyperlink(new Run(token[1..split])) { NavigateUri = new Uri(token[(split + 2)..^1]) };
                link.RequestNavigate += OpenReleaseLink;
                inlines.Add(link);
            }
            index = match.Index + match.Length;
        }
        if (index < text.Length) inlines.Add(new Run(text[index..]));
    }

    private static void OpenReleaseLink(object sender, RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"That link could not be opened.\n\n{ex.Message}", "Hoshino Editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
        e.Handled = true;
    }

    private static bool FocusIsInsideInteractiveControl()
    {
        var current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Primitives.RangeBase or
                System.Windows.Controls.Primitives.Selector or
                System.Windows.Controls.Primitives.Thumb)
                return true;
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private bool TryCloseWorkspace()
    {
        if (_workspace is null) return true;
        if (!_workspace.CanClose()) return false;
        _workspace.Close();
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_settingsOpen) PersistSettings(showErrors: false);
        if (!_installingUpdate && !TryCloseWorkspace()) { e.Cancel = true; return; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _updateCts?.Cancel();
        _toastTimer.Stop();
        base.OnClosed(e);
    }
}
