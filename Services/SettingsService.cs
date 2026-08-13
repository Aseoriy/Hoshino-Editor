using HoshinoEditor.Models;
using System.Text.Json;

namespace HoshinoEditor.Services;

public static class SettingsService
{
    private const long MaxSettingsBytes = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 32 };
    private static readonly string[] Themes = ["Hoshino", "Midnight", "Sakura", "Aurora", "Ember", "Custom"];
    private static readonly string[] ExportFormats = ["PNG", "JPEG", "TIFF", "BMP"];
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sail Solutions", "Hoshino Editor");
    private static readonly string SettingsPath = Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; private set; } = Load();
    public static event EventHandler? Changed;

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            if (new FileInfo(SettingsPath).Length > MaxSettingsBytes) return new AppSettings();
            return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions));
        }
        catch { return new AppSettings(); }
    }

    public static void Save()
    {
        Current = Normalize(Current);
        Directory.CreateDirectory(Folder);
        var temporary = SettingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temporary, SettingsPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        settings.Theme = Themes.FirstOrDefault(theme => theme.Equals(settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? "Hoshino";
        if (!IsHexColor(settings.CustomAccent)) settings.CustomAccent = "#A855F7";
        settings.CloseButtonAction = settings.CloseButtonAction?.Equals("Minimize", StringComparison.OrdinalIgnoreCase) == true
            ? "Minimize" : "Exit";
        settings.DefaultExportFormat = ExportFormats.FirstOrDefault(format => format.Equals(settings.DefaultExportFormat, StringComparison.OrdinalIgnoreCase)) ?? "PNG";
        settings.JpegQuality = Math.Clamp(settings.JpegQuality, 50, 100);
        settings.UndoLimit = Math.Clamp(settings.UndoLimit, 10, 100);
        settings.AutoSaveMinutes = Math.Clamp(settings.AutoSaveMinutes, 1, 30);
        if (string.IsNullOrWhiteSpace(settings.LastExportFolder) || settings.LastExportFolder.Length > 32_767)
            settings.LastExportFolder = null;

        var normalizedBindings = new Dictionary<string, string>(new AppSettings().KeyBindings, StringComparer.OrdinalIgnoreCase);
        if (settings.KeyBindings is not null)
        {
            foreach (var pair in settings.KeyBindings)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 64) continue;
                var value = pair.Value ?? string.Empty;
                if (value.Length <= 128) normalizedBindings[pair.Key] = value;
            }
        }
        settings.KeyBindings = normalizedBindings;
        return settings;
    }

    private static bool IsHexColor(string? value)
    {
        if (value is null || value.Length is not (7 or 9) || value[0] != '#') return false;
        return value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    }
}
