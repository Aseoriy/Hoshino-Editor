using HoshinoEditor.Services;
using System.Windows;

namespace HoshinoEditor;

public partial class App : Application
{
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupFile = e.Args.FirstOrDefault(File.Exists);
        ThemeService.ApplyCurrent();
        base.OnStartup(e);
    }
}
