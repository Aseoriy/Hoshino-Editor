using System.Windows.Input;

namespace HoshinoEditor.Services;

public sealed record ShortcutDefinition(string Id, string Name, string Description);
public sealed record ShortcutBinding(Key Key, ModifierKeys Modifiers);

public static class ShortcutService
{
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } =
    [
        new("Open", "Open / import", "Open media or import another image."),
        new("Save", "Export / save", "Save a project or export the current composition."),
        new("Undo", "Undo", "Undo the previous edit."),
        new("Redo", "Redo", "Redo the previous edit."),
        new("ZoomIn", "Zoom in", "Increase photo canvas magnification."),
        new("ZoomOut", "Zoom out", "Decrease photo canvas magnification."),
        new("ResetZoom", "Reset zoom", "Return the photo canvas to 100%."),
        new("FitComposition", "Fit composition", "Fit all image layers into the viewport."),
        new("Settings", "Open settings", "Open the settings workspace."),
        new("DeleteLayer", "Delete selected layer", "Remove the selected photo layer."),
        new("PlayPause", "Play / pause video", "Toggle video preview playback."),
        new("MoveTool", "Move tool", "Activate layer selection and drag-to-move."),
        new("PanTool", "Hand / pan tool", "Activate click-and-drag canvas panning.")
    ];

    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Open"] = "Ctrl+O", ["Save"] = "Ctrl+S", ["Undo"] = "Ctrl+Z", ["Redo"] = "Ctrl+Y",
        ["ZoomIn"] = "Ctrl+OemPlus", ["ZoomOut"] = "Ctrl+OemMinus", ["ResetZoom"] = "Ctrl+D0",
        ["FitComposition"] = "Ctrl+D9", ["Settings"] = "Ctrl+OemComma", ["DeleteLayer"] = "Delete",
        ["PlayPause"] = "Space", ["MoveTool"] = "V", ["PanTool"] = "H"
    };

    public static string GetRaw(string action)
    {
        SettingsService.Current.KeyBindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!SettingsService.Current.KeyBindings.TryGetValue(action, out var configured)) return Defaults[action];
        if (string.IsNullOrWhiteSpace(configured)) return string.Empty;
        return TryParse(configured, out _) ? configured : Defaults[action];
    }

    public static bool Matches(string action, KeyEventArgs e)
    {
        if (!TryParse(GetRaw(action), out var binding)) return false;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows);
        return key == binding.Key && modifiers == binding.Modifiers;
    }

    public static bool TryParse(string? value, out ShortcutBinding binding)
    {
        binding = new ShortcutBinding(Key.None, ModifierKeys.None);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        var hasKey = false;
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Control;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Alt;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Windows;
            else
            {
                if (hasKey || !Enum.TryParse(part, true, out key) || !Enum.IsDefined(typeof(Key), key) || IsModifierKey(key)) return false;
                hasKey = true;
            }
        }
        if (!hasKey || key == Key.None) return false;
        binding = new ShortcutBinding(key, modifiers);
        return true;
    }

    public static string Serialize(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join('+', parts);
    }

    public static string Display(string? raw)
    {
        if (!TryParse(raw, out var binding)) return "Unassigned";
        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (binding.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(binding.Key switch
        {
            Key.OemPlus or Key.Add => "+", Key.OemMinus or Key.Subtract => "−", Key.OemComma => ",",
            >= Key.D0 and <= Key.D9 => ((int)binding.Key - (int)Key.D0).ToString(),
            _ => binding.Key.ToString()
        });
        return string.Join('+', parts);
    }

    public static string? FindConflict(string action, string raw)
    {
        if (!TryParse(raw, out var requested)) return null;
        return Definitions.FirstOrDefault(definition => !definition.Id.Equals(action, StringComparison.OrdinalIgnoreCase)
            && TryParse(GetRaw(definition.Id), out var existing) && existing == requested)?.Id;
    }

    public static void ResetDefaults()
    {
        SettingsService.Current.KeyBindings = new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
