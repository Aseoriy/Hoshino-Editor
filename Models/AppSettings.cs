namespace HoshinoEditor.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Hoshino";
    public string CustomAccent { get; set; } = "#A855F7";
    public bool ShowTooltips { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool CheckerboardCanvas { get; set; } = true;
    public bool ShowCanvasGrid { get; set; }
    public bool SnapLayersToGrid { get; set; }
    public bool CenterNewLayers { get; set; } = true;
    public bool ConfirmBeforeReset { get; set; } = true;
    public bool HighQualityPreview { get; set; } = true;
    public bool PreferGpuAcceleration { get; set; } = true;
    public bool RememberExportFolder { get; set; } = true;
    public bool AutoSaveVideoProjects { get; set; } = true;
    public bool RestoreLastSession { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public bool IncludeBetaUpdates { get; set; }
    public string CloseButtonAction { get; set; } = "Exit";
    public string DefaultExportFormat { get; set; } = "PNG";
    public int JpegQuality { get; set; } = 92;
    public int UndoLimit { get; set; } = 40;
    public int AutoSaveMinutes { get; set; } = 5;
    public string? LastExportFolder { get; set; }
    public Dictionary<string, string> KeyBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Open"] = "Ctrl+O",
        ["Save"] = "Ctrl+S",
        ["Undo"] = "Ctrl+Z",
        ["Redo"] = "Ctrl+Y",
        ["ZoomIn"] = "Ctrl+OemPlus",
        ["ZoomOut"] = "Ctrl+OemMinus",
        ["ResetZoom"] = "Ctrl+D0",
        ["FitComposition"] = "Ctrl+D9",
        ["Settings"] = "Ctrl+OemComma",
        ["DeleteLayer"] = "Delete",
        ["PlayPause"] = "Space",
        ["MoveTool"] = "V",
        ["PanTool"] = "H"
    };
}
