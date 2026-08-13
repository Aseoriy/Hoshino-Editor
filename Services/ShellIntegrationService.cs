using Microsoft.Win32;
using System.Diagnostics;

namespace HoshinoEditor.Services;

public static class ShellIntegrationService
{
    private const string MenuLabel = "Open with Hoshino Editor";

    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
        if (!Path.GetFileName(exe).Equals("HoshinoEditor.exe", StringComparison.OrdinalIgnoreCase)) return;

        var extensions = MediaTypeService.ImageExtensions.Concat(MediaTypeService.VideoExtensions).Append(".hoshino");
        foreach (var extension in extensions)
        {
            using var command = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{extension}\shell\HoshinoEditor\command");
            command?.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
            using var shell = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{extension}\shell\HoshinoEditor");
            shell?.SetValue(string.Empty, MenuLabel);
            shell?.SetValue("Icon", $"{exe},0");
        }

        // Older beta builds advertised incomplete Default Apps capabilities without
        // creating the matching ProgIDs. Remove only those stale app-owned entries;
        // the working per-file context-menu verbs above remain registered.
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\HoshinoEditor\Capabilities", false);
        using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue("Hoshino Editor", false);
    }
}
