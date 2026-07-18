using HoshinoEditor.Services;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace HoshinoEditor.Controls;

public partial class StartWorkspace : UserControl
{
    public event EventHandler<string>? OpenRequested;
    public event EventHandler? NewPhotoRequested;
    public event EventHandler? NewVideoRequested;

    public StartWorkspace() => InitializeComponent();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open media in Hoshino",
            Filter = "Supported media|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.m4v|Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|Videos|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.m4v|All files|*.*"
        };
        if (dialog.ShowDialog() == true) OpenRequested?.Invoke(this, dialog.FileName);
    }

    private void Photo_Click(object sender, RoutedEventArgs e) => NewPhotoRequested?.Invoke(this, EventArgs.Empty);
    private void Video_Click(object sender, RoutedEventArgs e) => NewVideoRequested?.Invoke(this, EventArgs.Empty);

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var path = (e.Data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault();
        e.Effects = path is not null && MediaTypeService.GetKind(path) != EditorKind.Unknown ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Root_Drop(object sender, DragEventArgs e)
    {
        var path = (e.Data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault();
        if (path is not null && MediaTypeService.GetKind(path) != EditorKind.Unknown) OpenRequested?.Invoke(this, path);
    }
}
