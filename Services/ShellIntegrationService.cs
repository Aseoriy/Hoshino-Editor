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

        foreach (var extension in MediaTypeService.ImageExtensions.Concat(MediaTypeService.VideoExtensions))
        {
            using var command = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{extension}\shell\HoshinoEditor\command");
            command?.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
            using var shell = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{extension}\shell\HoshinoEditor");
            shell?.SetValue(string.Empty, MenuLabel);
            shell?.SetValue("Icon", $"{exe},0");
        }

        using var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\HoshinoEditor\Capabilities");
        capabilities?.SetValue("ApplicationName", "Hoshino Editor");
        capabilities?.SetValue("ApplicationDescription", "A fast photo and video editor from Sail Solutions.");
        using var associations = capabilities?.CreateSubKey("FileAssociations");
        foreach (var extension in MediaTypeService.ImageExtensions.Concat(MediaTypeService.VideoExtensions))
            associations?.SetValue(extension, $"HoshinoEditor{extension}");
        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered?.SetValue("Hoshino Editor", @"Software\HoshinoEditor\Capabilities");
    }
}
