using HoshinoEditor.Controls;
using HoshinoEditor.Services;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HoshinoEditor;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private IEditorWorkspace? _workspace;
    private object? _contentBeforeSettings;
    private bool _settingsOpen;

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
        else ShowStart();
    }

    public void ShowStart()
    {
        _workspace?.Close();
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
        if (!File.Exists(path)) { ShowToast("That file no longer exists.", true); return; }
        switch (MediaTypeService.GetKind(path))
        {
            case EditorKind.Photo: OpenPhoto(path); break;
            case EditorKind.Video: OpenVideo(path); break;
            default: ShowToast("Hoshino doesn't support that file type yet.", true); return;
        }
        await Task.CompletedTask;
    }

    private void OpenPhoto(string? path)
    {
        _workspace?.Close();
        var workspace = new PhotoWorkspace(path);
        HookWorkspace(workspace);
        WorkspaceHost.Content = workspace;
    }

    private void OpenVideo(string? path)
    {
        _workspace?.Close();
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
        if (_settingsOpen)
        {
            if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; }
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.O or Key.I)
        {
            _workspace?.Open();
            if (_workspace is null) ShowStart();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { _workspace?.Save(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { _workspace?.Undo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { _workspace?.Redo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add) { if (_workspace is PhotoWorkspace photo) photo.ZoomIn(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemMinus or Key.Subtract) { if (_workspace is PhotoWorkspace photo) photo.ZoomOut(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D0) { if (_workspace is PhotoWorkspace photo) photo.ResetZoom(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D9) { if (_workspace is PhotoWorkspace photo) photo.FitComposition(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma) { OpenSettings(); e.Handled = true; }
        else if (e.Key == Key.Delete) { if (_workspace is PhotoWorkspace photo) photo.DeleteSelectedLayer(); e.Handled = true; }
        else if (e.Key == Key.Space) { _workspace?.TogglePlayback(); e.Handled = _workspace is VideoWorkspace; }
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
        _settingsOpen = true;
        _contentBeforeSettings = WorkspaceHost.Content;
        var settings = new SettingsWorkspace();
        settings.DoneRequested += (_, _) => CloseSettings();
        WorkspaceHost.Content = settings;
        DocumentTitle.Text = "  /  Settings";
        StatusText.Text = "Hoshino Editor v0.9.0-beta-1  ·  Sail Solutions";
    }
    private void CloseSettings()
    {
        if (!_settingsOpen) return;
        SettingsService.Save();
        StartupService.SetStartWithWindows(SettingsService.Current.StartWithWindows);
        _settingsOpen = false;
        WorkspaceHost.Content = _contentBeforeSettings;
        _contentBeforeSettings = null;
        DocumentTitle.Text = $"  /  {_workspace?.Title ?? "New project"}";
        StatusText.Text = _workspace?.Status ?? "Ready";
        ApplyWindowSettings();
    }
    private void ApplyWindowSettings()
    {
        StatusBar.Visibility = SettingsService.Current.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        StatusRow.Height = SettingsService.Current.ShowStatusBar ? new GridLength(28) : new GridLength(0);
    }
    protected override void OnClosed(EventArgs e) { _workspace?.Close(); base.OnClosed(e); }
}
