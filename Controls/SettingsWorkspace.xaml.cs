using HoshinoEditor.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HoshinoEditor.Controls;

public partial class SettingsWorkspace : UserControl
{
    private sealed record ShortcutItem(string Id, string Name, string Description, string Display);
    private bool _ready;
    public event EventHandler? DoneRequested;
    public event EventHandler? CheckUpdatesRequested;

    public SettingsWorkspace()
    {
        InitializeComponent();
        DataContext = SettingsService.Current;
        CustomAccentBox.Text = SettingsService.Current.CustomAccent;
        AboutVersionText.Text = $"v{UpdateService.CurrentVersion}";
        ThemeCombo.ItemsSource = ThemeService.ThemeNames;
        ThemeCombo.SelectedItem = SettingsService.Current.Theme;
        RefreshShortcuts();
        _ready = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (AppearancePanel is null || sender is not RadioButton button) return;
        foreach (var panel in new[] { AppearancePanel, EditorPanel, PerformancePanel, FilesPanel, ShortcutsPanel, AboutPanel })
            panel.Visibility = panel.Name.StartsWith(button.Tag?.ToString() ?? string.Empty, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeCombo.SelectedItem is not string theme) return;
        AccentStatus.Text = string.Empty;
        SettingsService.Current.Theme = theme;
        ThemeService.ApplyCurrent();
    }

    private void ApplyAccent_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeAccent(CustomAccentBox.Text, out var accent, out var error))
        {
            AccentStatus.Text = error;
            AccentStatus.Foreground = (Brush)FindResource("Danger");
            CustomAccentBox.Focus();
            CustomAccentBox.SelectAll();
            return;
        }

        SettingsService.Current.Theme = "Custom";
        SettingsService.Current.CustomAccent = accent;
        CustomAccentBox.Text = accent;
        if (!Equals(ThemeCombo.SelectedItem, "Custom")) ThemeCombo.SelectedItem = "Custom";
        else ThemeService.ApplyCurrent();
        AccentStatus.Text = "Custom accent applied";
        AccentStatus.Foreground = (Brush)FindResource("AccentBright");
    }

    private static bool TryNormalizeAccent(string input, out string accent, out string error)
    {
        accent = string.Empty;
        error = string.Empty;
        try
        {
            if (ColorConverter.ConvertFromString(input.Trim()) is not Color color)
                throw new FormatException();

            color.A = byte.MaxValue;
            var bright = Blend(color, Colors.White, .18);
            var background = Color.FromRgb(0x0A, 0x0A, 0x0F);
            if (ContrastRatio(bright, background) < 4.5)
            {
                error = "Choose a brighter accent so text and controls stay readable.";
                return false;
            }

            accent = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            return true;
        }
        catch
        {
            error = "Enter a color such as #A855F7.";
            return false;
        }
    }

    private static Color Blend(Color first, Color second, double amount) => Color.FromRgb(
        (byte)(first.R + (second.R - first.R) * amount),
        (byte)(first.G + (second.G - first.G) * amount),
        (byte)(first.B + (second.B - first.B) * amount));

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + .05) /
               (Math.Min(firstLuminance, secondLuminance) + .05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4);
        }

        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        DoneRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshShortcuts()
    {
        ShortcutList.ItemsSource = ShortcutService.Definitions.Select(definition => new ShortcutItem(
            definition.Id, definition.Name, definition.Description, ShortcutService.Display(ShortcutService.GetRaw(definition.Id)))).ToArray();
    }

    private void ShortcutBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
        ShortcutStatus.Text = "Press a shortcut now · Backspace clears";
        ShortcutStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
    }

    private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string action } box) return;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (ShortcutService.IsModifierKey(key)) { e.Handled = true; return; }
        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows);
        if (key == Key.Back && modifiers == ModifierKeys.None)
        {
            SettingsService.Current.KeyBindings[action] = string.Empty;
            box.Text = "Unassigned";
            ShortcutStatus.Text = "Shortcut cleared";
            e.Handled = true;
            return;
        }
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
        if (key is Key.None or Key.Escape) { e.Handled = true; return; }
        var raw = ShortcutService.Serialize(key, modifiers);
        var conflict = ShortcutService.FindConflict(action, raw);
        if (conflict is not null)
        {
            var name = ShortcutService.Definitions.First(definition => definition.Id == conflict).Name;
            ShortcutStatus.Text = $"Already assigned to {name}. Choose another shortcut.";
            ShortcutStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            e.Handled = true;
            return;
        }
        SettingsService.Current.KeyBindings[action] = raw;
        box.Text = ShortcutService.Display(raw);
        ShortcutStatus.Text = $"{ShortcutService.Definitions.First(definition => definition.Id == action).Name} updated";
        ShortcutStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentBright");
        e.Handled = true;
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        ShortcutService.ResetDefaults();
        RefreshShortcuts();
        ShortcutStatus.Text = "Default shortcuts restored";
        ShortcutStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentBright");
    }
}
