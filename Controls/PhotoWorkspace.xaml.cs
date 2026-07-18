using HoshinoEditor.Models;
using HoshinoEditor.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace HoshinoEditor.Controls;

public partial class PhotoWorkspace : UserControl, IEditorWorkspace
{
    private sealed record LayerState(Guid Id, string Path, string Name, BitmapSource Original, BitmapSource Base, BitmapSource Preview,
        ImageAdjustments Adjustments, double X, double Y, double Scale, double Opacity);
    private sealed record WorkspaceSnapshot(List<LayerState> Layers, Guid? SelectedId);

    public ObservableCollection<ImageLayerItem> Layers { get; } = [];
    private readonly Stack<WorkspaceSnapshot> _undo = new();
    private readonly Stack<WorkspaceSnapshot> _redo = new();
    private readonly Dictionary<Guid, Border> _layerVisuals = [];
    private ImageLayerItem? _selectedLayer;
    private ImageLayerItem? _movingLayer;
    private Point _moveStart;
    private Point _layerStart;
    private bool _isCropping;
    private bool _isDraggingCrop;
    private Point _cropStart;
    private Rect _cropRect;
    private bool _suppressControls;
    private int _renderVersion;
    private string? _path;
    private double _zoom = 1;

    public string Title => Layers.Count switch { 0 => "Untitled composition", 1 => Layers[0].Name, _ => $"{Layers.Count} image composition" };
    public string Status { get; private set; } = "Photo compositor";
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? HomeRequested;
    public event EventHandler<ToastMessage>? ToastRequested;

    public PhotoWorkspace(string? path)
    {
        InitializeComponent();
        DataContext = this;
        ApplyCanvasAppearance();
        Upscale_ValueChanged(this, new RoutedPropertyChangedEventArgs<double>(0, UpscaleSlider.Value));
        Background_ValueChanged(this, new RoutedPropertyChangedEventArgs<double>(0, BackgroundSlider.Value));
        if (path is not null) LoadImages([path]);
    }

    public void Open() => Open_Click(this, new RoutedEventArgs());
    public void Save() => Export_Click(this, new RoutedEventArgs());
    public void Undo() => Undo_Click(this, new RoutedEventArgs());
    public void Redo() => Redo_Click(this, new RoutedEventArgs());
    public void TogglePlayback() { }
    public void CancelActiveTool() => CancelCrop();
    public void Close() => Interlocked.Increment(ref _renderVersion);
    public void ZoomIn() => SetZoom(_zoom * 1.2);
    public void ZoomOut() => SetZoom(_zoom / 1.2);
    public void ResetZoom() => SetZoom(1);
    public void FitComposition()
    {
        if (Layers.Count == 0) { ResetZoom(); return; }
        var bounds = CompositionBounds();
        var viewportWidth = Math.Max(100, CanvasScroll.ViewportWidth - 36);
        var viewportHeight = Math.Max(100, CanvasScroll.ViewportHeight - 36);
        SetZoom(Math.Min(viewportWidth / Math.Max(1, bounds.Right + 50), viewportHeight / Math.Max(1, bounds.Bottom + 50)));
        CanvasScroll.ScrollToHorizontalOffset(0); CanvasScroll.ScrollToVerticalOffset(0);
    }
    public void DeleteSelectedLayer() => RemoveSelectedLayer();

    private void ApplyCanvasAppearance()
    {
        if (!SettingsService.Current.CheckerboardCanvas) { CompositionCanvas.Background = new SolidColorBrush(Color.FromRgb(14, 14, 20)); return; }
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(23, 23, 31)), null, new Rect(0, 0, 24, 24));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 40)), null, new Rect(0, 0, 12, 12));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 40)), null, new Rect(12, 12, 12, 12));
            if (SettingsService.Current.ShowCanvasGrid)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), .5);
                dc.DrawLine(pen, new Point(0, 0), new Point(24, 0)); dc.DrawLine(pen, new Point(0, 0), new Point(0, 24));
            }
        }
        CompositionCanvas.Background = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 24, 24), ViewportUnits = BrushMappingMode.Absolute };
    }

    private void LoadImages(IEnumerable<string> paths)
    {
        var validPaths = paths.Where(p => File.Exists(p) && MediaTypeService.GetKind(p) == EditorKind.Photo).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (validPaths.Length == 0) return;
        if (Layers.Count > 0) PushUndo();
        ImageLayerItem? last = null;
        foreach (var path in validPaths)
        {
            try
            {
                var image = ImageEditService.Load(path);
                var offset = 48 + Layers.Count * 34;
                last = new ImageLayerItem
                {
                    Path = path, Name = IOPath.GetFileName(path), OriginalImage = image, BaseImage = image, PreviewImage = image,
                    X = SettingsService.Current.CenterNewLayers ? offset : 20, Y = SettingsService.Current.CenterNewLayers ? offset : 20
                };
                Layers.Add(last);
                _path ??= path;
            }
            catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Couldn't import {IOPath.GetFileName(path)}: {ex.Message}", true)); }
        }
        if (last is null) return;
        RebuildLayerVisuals();
        SelectLayer(last);
        EmptyState.Visibility = Visibility.Collapsed;
        UpdateWorkspaceSummary();
        Dispatcher.BeginInvoke(FitComposition);
    }

    private void RebuildLayerVisuals()
    {
        foreach (var visual in _layerVisuals.Values) CompositionCanvas.Children.Remove(visual);
        _layerVisuals.Clear();
        for (var index = 0; index < Layers.Count; index++)
        {
            var layer = Layers[index];
            var image = new Image { Source = layer.PreviewImage, Stretch = Stretch.Fill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, SettingsService.Current.HighQualityPreview ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
            var border = new Border { Child = image, Tag = layer, BorderThickness = new Thickness(2), Cursor = Cursors.SizeAll };
            border.MouseLeftButtonDown += Layer_MouseLeftButtonDown;
            border.MouseMove += Layer_MouseMove;
            border.MouseLeftButtonUp += Layer_MouseLeftButtonUp;
            _layerVisuals[layer.Id] = border;
            CompositionCanvas.Children.Add(border);
            Panel.SetZIndex(border, index + 1);
            UpdateLayerVisual(layer);
        }
        EmptyState.Visibility = Layers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionVisuals();
        ResizeCanvasToContent();
    }

    private void UpdateLayerVisual(ImageLayerItem layer)
    {
        if (!_layerVisuals.TryGetValue(layer.Id, out var border)) return;
        border.Width = layer.BaseImage.PixelWidth * layer.Scale;
        border.Height = layer.BaseImage.PixelHeight * layer.Scale;
        border.Opacity = layer.Opacity;
        Canvas.SetLeft(border, layer.X); Canvas.SetTop(border, layer.Y);
        if (border.Child is Image image) image.Source = layer.PreviewImage;
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var layer in Layers)
        {
            layer.IsSelected = layer == _selectedLayer;
            if (_layerVisuals.TryGetValue(layer.Id, out var border))
            {
                border.BorderBrush = layer == _selectedLayer ? (Brush)FindResource("AccentBright") : Brushes.Transparent;
                border.Cursor = _isCropping ? Cursors.Cross : Cursors.SizeAll;
            }
        }
    }

    private void SelectLayer(ImageLayerItem? layer)
    {
        _selectedLayer = layer;
        LayerList.SelectedItem = layer;
        UpdateSelectionVisuals();
        SyncControlsFromSelection();
        UpdateWorkspaceSummary();
    }

    private void SyncControlsFromSelection()
    {
        _suppressControls = true;
        var a = _selectedLayer?.Adjustments ?? new ImageAdjustments();
        ExposureSlider.Value = a.Exposure; ContrastSlider.Value = a.Contrast; SaturationSlider.Value = a.Saturation; TemperatureSlider.Value = a.Temperature;
        OpacitySlider.Value = (_selectedLayer?.Opacity ?? 1) * 100;
        if (_selectedLayer is not null) { WidthBox.Text = _selectedLayer.BaseImage.PixelWidth.ToString(); HeightBox.Text = _selectedLayer.BaseImage.PixelHeight.ToString(); }
        _suppressControls = false;
        UpdateAdjustmentLabels();
    }

    private IEnumerable<ImageLayerItem> EditTargets() => EditTargetCombo.SelectedIndex == 1 ? Layers : _selectedLayer is null ? [] : [_selectedLayer];
    private ImageAdjustments CurrentAdjustments() => new(ExposureSlider.Value, ContrastSlider.Value, SaturationSlider.Value, TemperatureSlider.Value);

    private void Adjustment_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ExposureValue is null) return;
        UpdateAdjustmentLabels();
        if (_suppressControls || _selectedLayer is null) return;
        var value = CurrentAdjustments();
        var targets = EditTargets().ToArray();
        foreach (var layer in targets) layer.Adjustments = value;
        QueueRender(targets);
    }

    private void UpdateAdjustmentLabels()
    {
        if (ExposureValue is null) return;
        ExposureValue.Text = Math.Round(ExposureSlider.Value).ToString("+0;-0;0");
        ContrastValue.Text = Math.Round(ContrastSlider.Value).ToString("+0;-0;0");
        SaturationValue.Text = Math.Round(SaturationSlider.Value).ToString("+0;-0;0");
        TemperatureValue.Text = Math.Round(TemperatureSlider.Value).ToString("+0;-0;0");
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
    }

    private void Adjustment_Begin(object sender, MouseButtonEventArgs e) { if (_selectedLayer is not null) PushUndo(); }
    private void Opacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue is null) return;
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        if (_suppressControls) return;
        foreach (var layer in EditTargets()) { layer.Opacity = OpacitySlider.Value / 100; UpdateLayerVisual(layer); }
    }

    private async void QueueRender(IEnumerable<ImageLayerItem> requested)
    {
        var targets = requested.Distinct().ToArray();
        if (targets.Length == 0) return;
        var version = Interlocked.Increment(ref _renderVersion);
        RenderingBadge.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(65);
            foreach (var layer in targets)
            {
                if (version != _renderVersion) return;
                var buffer = ImageEditService.CapturePixels(layer.BaseImage);
                var adjustments = layer.Adjustments;
                var adjusted = await Task.Run(() => ImageEditService.AdjustPixels(buffer, adjustments));
                if (version != _renderVersion) return;
                layer.PreviewImage = ImageEditService.CreateBitmap(buffer, adjusted);
                UpdateLayerVisual(layer);
            }
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Preview failed: {ex.Message}", true)); }
        finally { if (version == _renderVersion) RenderingBadge.Visibility = Visibility.Collapsed; }
    }

    private void TransformTargets(Func<BitmapSource, BitmapSource> operation)
    {
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return;
        PushUndo();
        foreach (var layer in targets) { layer.BaseImage = operation(layer.BaseImage); layer.PreviewImage = layer.BaseImage; }
        RebuildLayerVisuals(); QueueRender(targets); SyncControlsFromSelection(); UpdateWorkspaceSummary();
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => TransformTargets(i => ImageEditService.Rotate(i, -90));
    private void RotateRight_Click(object sender, RoutedEventArgs e) => TransformTargets(i => ImageEditService.Rotate(i, 90));
    private void FlipH_Click(object sender, RoutedEventArgs e) => TransformTargets(i => ImageEditService.Flip(i, true));
    private void FlipV_Click(object sender, RoutedEventArgs e) => TransformTargets(i => ImageEditService.Flip(i, false));

    private void Layer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ImageLayerItem layer) return;
        SelectLayer(layer);
        if (_isCropping) return;
        PushUndo(); _movingLayer = layer; _moveStart = e.GetPosition(CompositionCanvas); _layerStart = new Point(layer.X, layer.Y);
        border.CaptureMouse(); e.Handled = true;
    }
    private void Layer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_movingLayer is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(CompositionCanvas); var x = Math.Max(0, _layerStart.X + point.X - _moveStart.X); var y = Math.Max(0, _layerStart.Y + point.Y - _moveStart.Y);
        if (SettingsService.Current.SnapLayersToGrid) { x = Math.Round(x / 10) * 10; y = Math.Round(y / 10) * 10; }
        _movingLayer.X = x; _movingLayer.Y = y; UpdateLayerVisual(_movingLayer); ResizeCanvasToContent(); UpdateWorkspaceSummary();
    }
    private void Layer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (sender is Border border) border.ReleaseMouseCapture(); _movingLayer = null; e.Handled = true; }

    private void CropTool_Click(object sender, RoutedEventArgs e)
    {
        _isCropping = CropTool.IsChecked == true && _selectedLayer is not null;
        ApplyCropButton.Visibility = CancelCropButton.Visibility = _isCropping ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionVisuals();
        if (!_isCropping) ClearCropVisuals(); else SetStatus("Crop · drag over the selected layer, then apply");
    }
    private Rect SelectedLayerRect() => _selectedLayer is null ? Rect.Empty : new Rect(_selectedLayer.X, _selectedLayer.Y, _selectedLayer.BaseImage.PixelWidth * _selectedLayer.Scale, _selectedLayer.BaseImage.PixelHeight * _selectedLayer.Scale);
    private Point ClampToSelected(Point point) { var rect = SelectedLayerRect(); return new Point(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom)); }
    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCropping || _selectedLayer is null) return;
        var point = e.GetPosition(CompositionCanvas); if (!SelectedLayerRect().Contains(point)) return;
        _isDraggingCrop = true; _cropStart = point; _cropRect = new Rect(point, point); CompositionCanvas.CaptureMouse(); DrawCrop(); e.Handled = true;
    }
    private void Canvas_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingCrop) return; _cropRect = new Rect(_cropStart, ClampToSelected(e.GetPosition(CompositionCanvas))); DrawCrop(); }
    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (!_isDraggingCrop) return; _isDraggingCrop = false; CompositionCanvas.ReleaseMouseCapture(); ApplyCropButton.IsEnabled = _cropRect.Width >= 4 && _cropRect.Height >= 4; }
    private void DrawCrop()
    {
        var image = SelectedLayerRect(); var crop = Rect.Intersect(image, _cropRect); CropRectangle.Visibility = Visibility.Visible;
        SetRect(CropRectangle, crop.Left, crop.Top, crop.Width, crop.Height);
        SetRect(CropShadeTop, image.Left, image.Top, image.Width, Math.Max(0, crop.Top - image.Top)); SetRect(CropShadeBottom, image.Left, crop.Bottom, image.Width, Math.Max(0, image.Bottom - crop.Bottom));
        SetRect(CropShadeLeft, image.Left, crop.Top, Math.Max(0, crop.Left - image.Left), crop.Height); SetRect(CropShadeRight, crop.Right, crop.Top, Math.Max(0, image.Right - crop.Right), crop.Height);
    }
    private static void SetRect(Rectangle rectangle, double left, double top, double width, double height) { Canvas.SetLeft(rectangle, left); Canvas.SetTop(rectangle, top); rectangle.Width = width; rectangle.Height = height; }
    private void ApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is null || _cropRect.Width < 4) return;
        var layer = _selectedLayer; var shown = SelectedLayerRect();
        var x = (int)Math.Round((_cropRect.Left - shown.Left) / shown.Width * layer.BaseImage.PixelWidth); var y = (int)Math.Round((_cropRect.Top - shown.Top) / shown.Height * layer.BaseImage.PixelHeight);
        var width = (int)Math.Round(_cropRect.Width / shown.Width * layer.BaseImage.PixelWidth); var height = (int)Math.Round(_cropRect.Height / shown.Height * layer.BaseImage.PixelHeight);
        PushUndo(); layer.BaseImage = ImageEditService.Crop(layer.BaseImage, new Int32Rect(x, y, width, height)); layer.X = _cropRect.Left; layer.Y = _cropRect.Top; layer.PreviewImage = layer.BaseImage;
        CancelCrop(); RebuildLayerVisuals(); QueueRender([layer]); SyncControlsFromSelection(); UpdateWorkspaceSummary();
    }
    private void CancelCrop_Click(object sender, RoutedEventArgs e) => CancelCrop();
    private void CancelCrop() { _isDraggingCrop = false; _isCropping = false; CropTool.IsChecked = false; CompositionCanvas.ReleaseMouseCapture(); ApplyCropButton.Visibility = CancelCropButton.Visibility = Visibility.Collapsed; ClearCropVisuals(); UpdateSelectionVisuals(); }
    private void ClearCropVisuals() { CropRectangle.Visibility = Visibility.Collapsed; ApplyCropButton.IsEnabled = false; _cropRect = Rect.Empty; foreach (var shade in new[] { CropShadeTop, CropShadeBottom, CropShadeLeft, CropShadeRight }) { shade.Width = 0; shade.Height = 0; } }

    private async void Resize_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out var width) || width < 1 || !int.TryParse(HeightBox.Text, out var height) || height < 1) { ToastRequested?.Invoke(this, new ToastMessage("Enter valid width and height values.", true)); return; }
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return; PushUndo(); RenderingBadge.Visibility = Visibility.Visible;
        try
        {
            foreach (var layer in targets)
            {
                var targetHeight = LockAspectCheck.IsChecked == true ? Math.Max(1, (int)Math.Round(width * (double)layer.BaseImage.PixelHeight / layer.BaseImage.PixelWidth)) : height;
                layer.BaseImage = await Task.Run(() => ImageEditService.Resize(layer.BaseImage, width, targetHeight, SettingsService.Current.HighQualityPreview)); layer.PreviewImage = layer.BaseImage;
            }
            RebuildLayerVisuals(); QueueRender(targets); SyncControlsFromSelection(); UpdateWorkspaceSummary();
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Resize failed: {ex.Message}", true)); }
        finally { RenderingBadge.Visibility = Visibility.Collapsed; }
    }
    private void Upscale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (UpscaleValue is not null) UpscaleValue.Text = $"{UpscaleSlider.Value:0}%"; }
    private async void Upscale_Click(object sender, RoutedEventArgs e)
    {
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return; var factor = UpscaleSlider.Value / 100; PushUndo(); RenderingBadge.Visibility = Visibility.Visible;
        try
        {
            foreach (var layer in targets)
            {
                var source = layer.BaseImage; var width = (int)Math.Round(source.PixelWidth * factor); var height = (int)Math.Round(source.PixelHeight * factor);
                layer.BaseImage = await Task.Run(() => ImageEditService.Resize(source, width, height, true)); layer.PreviewImage = layer.BaseImage;
            }
            RebuildLayerVisuals(); QueueRender(targets); SyncControlsFromSelection(); UpdateWorkspaceSummary(); SetStatus($"Upscaled {targets.Length} layer{(targets.Length == 1 ? "" : "s")} to {UpscaleSlider.Value:0}%");
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Upscale failed: {ex.Message}", true)); }
        finally { RenderingBadge.Visibility = Visibility.Collapsed; }
    }
    private void Background_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (BackgroundValue is not null) BackgroundValue.Text = $"{BackgroundSlider.Value:0}"; }
    private async void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return; PushUndo(); RenderingBadge.Visibility = Visibility.Visible;
        try
        {
            var tolerance = 12 + BackgroundSlider.Value * 1.1; var feather = 28 + BackgroundSlider.Value * .7;
            foreach (var layer in targets) { var source = layer.BaseImage; layer.BaseImage = await Task.Run(() => ImageEditService.RemoveBackground(source, tolerance, feather)); layer.PreviewImage = layer.BaseImage; }
            RebuildLayerVisuals(); QueueRender(targets); SetStatus($"Background removed locally from {targets.Length} layer{(targets.Length == 1 ? "" : "s")}");
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Background removal failed: {ex.Message}", true)); }
        finally { RenderingBadge.Visibility = Visibility.Collapsed; }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return;
        if (SettingsService.Current.ConfirmBeforeReset && MessageBox.Show("Reset edits for the current edit target?", "Hoshino Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PushUndo(); foreach (var layer in targets) { layer.BaseImage = layer.OriginalImage; layer.PreviewImage = layer.OriginalImage; layer.Adjustments = new(); layer.Scale = 1; layer.Opacity = 1; }
        RebuildLayerVisuals(); SyncControlsFromSelection(); UpdateWorkspaceSummary();
    }

    private WorkspaceSnapshot CaptureSnapshot() => new(Layers.Select(l => new LayerState(l.Id, l.Path, l.Name, l.OriginalImage, l.BaseImage, l.PreviewImage, l.Adjustments, l.X, l.Y, l.Scale, l.Opacity)).ToList(), _selectedLayer?.Id);
    private void PushUndo()
    {
        _undo.Push(CaptureSnapshot()); var limit = Math.Clamp(SettingsService.Current.UndoLimit, 10, 100);
        if (_undo.Count > limit) { var kept = _undo.Reverse().Skip(1).Reverse().ToArray(); _undo.Clear(); foreach (var item in kept) _undo.Push(item); }
        _redo.Clear(); UpdateHistoryButtons();
    }
    private void Restore(WorkspaceSnapshot snapshot)
    {
        Interlocked.Increment(ref _renderVersion); Layers.Clear();
        foreach (var state in snapshot.Layers) Layers.Add(new ImageLayerItem { Id = state.Id, Path = state.Path, Name = state.Name, OriginalImage = state.Original, BaseImage = state.Base, PreviewImage = state.Preview, Adjustments = state.Adjustments, X = state.X, Y = state.Y, Scale = state.Scale, Opacity = state.Opacity });
        RebuildLayerVisuals(); SelectLayer(Layers.FirstOrDefault(l => l.Id == snapshot.SelectedId) ?? Layers.LastOrDefault()); UpdateWorkspaceSummary();
    }
    private void Undo_Click(object sender, RoutedEventArgs e) { if (_undo.Count == 0) return; _redo.Push(CaptureSnapshot()); Restore(_undo.Pop()); UpdateHistoryButtons(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (_redo.Count == 0) return; _undo.Push(CaptureSnapshot()); Restore(_redo.Pop()); UpdateHistoryButtons(); }
    private void UpdateHistoryButtons() { UndoButton.IsEnabled = _undo.Count > 0; RedoButton.IsEnabled = _redo.Count > 0; }

    private void BringForward_Click(object sender, RoutedEventArgs e) { if (_selectedLayer is null) return; var i = Layers.IndexOf(_selectedLayer); if (i >= Layers.Count - 1) return; PushUndo(); Layers.Move(i, i + 1); RebuildLayerVisuals(); }
    private void SendBackward_Click(object sender, RoutedEventArgs e) { if (_selectedLayer is null) return; var i = Layers.IndexOf(_selectedLayer); if (i <= 0) return; PushUndo(); Layers.Move(i, i - 1); RebuildLayerVisuals(); }
    private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (LayerList.SelectedItem is ImageLayerItem layer && layer != _selectedLayer) SelectLayer(layer); }
    private void RemoveLayer_Click(object sender, RoutedEventArgs e) => RemoveSelectedLayer();
    private void RemoveSelectedLayer()
    {
        if (_selectedLayer is null) return; PushUndo(); var index = Layers.IndexOf(_selectedLayer); Layers.Remove(_selectedLayer); RebuildLayerVisuals(); SelectLayer(Layers.Count == 0 ? null : Layers[Math.Min(index, Layers.Count - 1)]); UpdateWorkspaceSummary();
    }

    private Rect CompositionBounds()
    {
        if (Layers.Count == 0) return Rect.Empty;
        var left = Layers.Min(l => l.X); var top = Layers.Min(l => l.Y); var right = Layers.Max(l => l.X + l.BaseImage.PixelWidth * l.Scale); var bottom = Layers.Max(l => l.Y + l.BaseImage.PixelHeight * l.Scale);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }
    private void ResizeCanvasToContent()
    {
        var bounds = CompositionBounds(); CompositionCanvas.Width = Math.Max(1000, bounds.Right + 120); CompositionCanvas.Height = Math.Max(700, bounds.Bottom + 120);
    }
    private void UpdateWorkspaceSummary()
    {
        if (DimensionsText is null) return; var bounds = CompositionBounds();
        DimensionsText.Text = Layers.Count == 0 ? string.Empty : $"{Math.Ceiling(bounds.Width):0} × {Math.Ceiling(bounds.Height):0}  ·  {Layers.Count} layer{(Layers.Count == 1 ? "" : "s")}";
        TitleChanged?.Invoke(this, Title); SetStatus(Layers.Count == 0 ? "Photo compositor" : $"{Layers.Count} image layer{(Layers.Count == 1 ? "" : "s")} · selected: {_selectedLayer?.Name}");
    }

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, .05, 8); CanvasZoomTransform.ScaleX = CanvasZoomTransform.ScaleY = _zoom; ZoomText.Text = $"{_zoom * 100:0}%";
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOut();
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ResetZoom();
    private void Fit_Click(object sender, RoutedEventArgs e) => FitComposition();
    private void CanvasScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetZoom(_zoom * (e.Delta > 0 ? 1.12 : 1 / 1.12)); e.Handled = true; } }

    private BitmapSource RenderComposition()
    {
        var bounds = CompositionBounds(); var width = (int)Math.Ceiling(bounds.Width); var height = (int)Math.Ceiling(bounds.Height);
        if ((long)width * height > 120_000_000) throw new InvalidOperationException("The merged canvas exceeds the 120 megapixel export safety limit.");
        var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            foreach (var layer in Layers)
            {
                dc.PushOpacity(layer.Opacity);
                dc.DrawImage(layer.PreviewImage, new Rect(layer.X - bounds.Left, layer.Y - bounds.Top, layer.BaseImage.PixelWidth * layer.Scale, layer.BaseImage.PixelHeight * layer.Scale));
                dc.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import images", Multiselect = true, Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        if (dialog.ShowDialog() == true) LoadImages(dialog.FileNames);
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Layers.Count == 0) { ToastRequested?.Invoke(this, new ToastMessage("Import an image before exporting.", true)); return; }
        var settings = SettingsService.Current; var extension = settings.DefaultExportFormat.ToUpperInvariant() switch { "JPEG" => ".jpg", "TIFF" => ".tiff", "BMP" => ".bmp", _ => ".png" };
        var initial = Layers.Count == 1 ? $"{IOPath.GetFileNameWithoutExtension(Layers[0].Name)}-edited{extension}" : $"hoshino-composite{extension}";
        var dialog = new SaveFileDialog { Title = "Merge and export image", FileName = initial, Filter = "PNG image|*.png|JPEG image|*.jpg|TIFF image|*.tiff|Bitmap image|*.bmp", DefaultExt = extension, AddExtension = true };
        if (settings.RememberExportFolder && Directory.Exists(settings.LastExportFolder)) dialog.InitialDirectory = settings.LastExportFolder;
        if (dialog.ShowDialog() != true) return;
        try { var result = RenderComposition(); ImageEditService.Save(result, dialog.FileName, settings.JpegQuality); settings.LastExportFolder = IOPath.GetDirectoryName(dialog.FileName); SettingsService.Save(); SetStatus($"Exported · {IOPath.GetFileName(dialog.FileName)}"); ToastRequested?.Invoke(this, new ToastMessage("Composition merged and exported.")); }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Export failed: {ex.Message}", true)); }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
    private void SetStatus(string status) { Status = status; StatusChanged?.Invoke(this, status); }
    private void Root_DragOver(object sender, DragEventArgs e) { var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; e.Effects = paths?.Any(p => MediaTypeService.GetKind(p) == EditorKind.Photo) == true ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Root_Drop(object sender, DragEventArgs e) { var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths is not null) LoadImages(paths); }
}
