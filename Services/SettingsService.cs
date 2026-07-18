using HoshinoEditor.Models;
using System.Text.Json;

namespace HoshinoEditor.Services;

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
