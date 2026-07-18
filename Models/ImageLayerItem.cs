using HoshinoEditor.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace HoshinoEditor.Models;

public sealed class ImageLayerItem : INotifyPropertyChanged
{
    private BitmapSource _previewImage;
    private double _x;
    private double _y;
    private double _scale = 1;
    private double _opacity = 1;
    private bool _isSelected;

    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Path { get; init; }
    public required string Name { get; set; }
    public required BitmapSource OriginalImage { get; init; }
    public required BitmapSource BaseImage { get; set; }
    public BitmapSource PreviewImage { get => _previewImage; set { _previewImage = value; Notify(); Notify(nameof(Dimensions)); } }
    public ImageAdjustments Adjustments { get; set; } = new();
    public double X { get => _x; set { _x = value; Notify(); } }
    public double Y { get => _y; set { _y = value; Notify(); } }
    public double Scale { get => _scale; set { _scale = value; Notify(); Notify(nameof(Dimensions)); } }
    public double Opacity { get => _opacity; set { _opacity = value; Notify(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(); } }
    public string Dimensions => $"{BaseImage.PixelWidth} × {BaseImage.PixelHeight}";

    public ImageLayerItem() => _previewImage = null!;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
