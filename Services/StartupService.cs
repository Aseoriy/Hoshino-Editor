using Microsoft.Win32;

namespace HoshinoEditor.Services;

public static class StartupService
{
    private const string ValueName = "Hoshino Editor";

    public static void SetStartWithWindows(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
            key?.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key?.DeleteValue(ValueName, false);
    }
}
