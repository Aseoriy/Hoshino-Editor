using HoshinoEditor.Services;
using System.Windows;
using System.Windows.Controls;

namespace HoshinoEditor.Controls;

public partial class SettingsWorkspace : UserControl
{
    public event EventHandler? DoneRequested;

    public SettingsWorkspace()
    {
        InitializeComponent();
        DataContext = SettingsService.Current;
        ThemeCombo.ItemsSource = ThemeService.ThemeNames;
        ThemeCombo.SelectedItem = SettingsService.Current.Theme;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (AppearancePanel is null || sender is not RadioButton button) return;
        foreach (var panel in new[] { AppearancePanel, EditorPanel, PerformancePanel, FilesPanel, ShortcutsPanel, AboutPanel })
            panel.Visibility = panel.Name.StartsWith(button.Tag?.ToString() ?? string.Empty, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not string theme) return;
        SettingsService.Current.Theme = theme;
        SettingsService.Save();
        ThemeService.ApplyCurrent();
    }

    private void ApplyAccent_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.Theme = "Custom";
        SettingsService.Current.CustomAccent = CustomAccentBox.Text.Trim();
        ThemeCombo.SelectedItem = "Custom";
        SettingsService.Save();
        ThemeService.ApplyCurrent();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Save();
        StartupService.SetStartWithWindows(SettingsService.Current.StartWithWindows);
        DoneRequested?.Invoke(this, EventArgs.Empty);
    }
}
