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
using System.Windows.Threading;
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
    private WorkspaceSnapshot? _moveSnapshot;
    private bool _moveUndoCommitted;
    private bool _isPanning;
    private Point _panStart;
    private double _panHorizontalStart;
    private double _panVerticalStart;
    private bool _isCropping;
    private bool _isDraggingCrop;
    private Point _cropStart;
    private Rect _cropRect;
    private bool _suppressControls;
    private int _renderVersion;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _upscaleCts;
    private CancellationTokenSource? _exportCts;
    private bool _isBusy;
    private string? _path;
    private double _zoom = 1;
    private bool _isDirty;
    private bool _closed;
    private IInputElement? _focusBeforeTextOverlay;

    public string Title => Layers.Count switch { 0 => "Untitled composition", 1 => Layers[0].Name, _ => $"{Layers.Count} image composition" };
    public string Status { get; private set; } = "Photo compositor";
    public bool IsBusy => _isBusy;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? HomeRequested;
    public event EventHandler<ToastMessage>? ToastRequested;

    public PhotoWorkspace(string? path)
    {
        InitializeComponent();
        DataContext = this;
        ApplyCanvasAppearance();
        foreach (var slider in new[] { ExposureSlider, ContrastSlider, SaturationSlider, TemperatureSlider, HighlightsSlider, ShadowsSlider, VibranceSlider, OpacitySlider })
            slider.PreviewKeyDown += Adjustment_PreviewKeyDown;
        Upscale_ValueChanged(this, new RoutedPropertyChangedEventArgs<double>(0, UpscaleSlider.Value));
        Background_ValueChanged(this, new RoutedPropertyChangedEventArgs<double>(0, BackgroundSlider.Value));
        UpdateSelectionControls();
        if (path is not null) Dispatcher.BeginInvoke(() => _ = LoadImagesAsync([path]), DispatcherPriority.Loaded);
    }

    public void Open() { if (!_isBusy) Open_Click(this, new RoutedEventArgs()); }
    public void Save() => Export_Click(this, new RoutedEventArgs());
    public void Undo() { if (!_isBusy) Undo_Click(this, new RoutedEventArgs()); }
    public void Redo() { if (!_isBusy) Redo_Click(this, new RoutedEventArgs()); }
    public void TogglePlayback() { }
    public void CancelActiveTool()
    {
        if (_exportCts is not null) _exportCts.Cancel();
        else if (_upscaleCts is not null) _upscaleCts.Cancel();
        else if (TextOverlay.Visibility == Visibility.Visible) CloseTextDialog();
        else if (_isCropping) CancelCrop();
        else ActivateMoveTool();
    }
    public void RefreshSettings() { ApplyCanvasAppearance(); RebuildLayerVisuals(); }
    public bool CanClose()
    {
        if (_exportCts is not null && MessageBox.Show("Cancel the image export and close this composition?", "Hoshino Editor",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        return !_isDirty || MessageBox.Show("Discard the unsaved changes in this photo composition?", "Hoshino Editor",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
    public void Close()
    {
        _closed = true;
        Interlocked.Increment(ref _renderVersion);
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
        _upscaleCts?.Cancel();
        _exportCts?.Cancel();
    }
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
    public void DeleteSelectedLayer() { if (!_isBusy) RemoveSelectedLayer(); }
    public void ActivateMoveTool()
    {
        if (_isBusy) return;
        CancelPan();
        if (_isCropping) CancelCrop();
        MoveTool.IsChecked = true; PanTool.IsChecked = false; CropTool.IsChecked = false;
        CanvasScroll.Cursor = Cursors.Arrow;
        UpdateSelectionVisuals(); SetStatus("Move tool · drag a layer or use the arrow keys");
    }
    public void ActivatePanTool()
    {
        if (_isBusy) return;
        if (_isCropping) CancelCrop();
        MoveTool.IsChecked = false; PanTool.IsChecked = true; CropTool.IsChecked = false;
        CanvasScroll.Cursor = Cursors.Hand;
        UpdateSelectionVisuals(); SetStatus("Hand tool · drag the canvas; middle mouse always pans");
    }
    public void NudgeSelected(double x, double y)
    {
        if (_selectedLayer is null || _isBusy) return;
        PushUndo();
        _selectedLayer.X = Math.Max(0, _selectedLayer.X + x);
        _selectedLayer.Y = Math.Max(0, _selectedLayer.Y + y);
        UpdateLayerVisual(_selectedLayer); ResizeCanvasToContent(); UpdateWorkspaceSummary();
    }

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

    private async Task LoadImagesAsync(IEnumerable<string> paths)
    {
        var validPaths = paths.Where(p => File.Exists(p) && MediaTypeService.GetKind(p) == EditorKind.Photo).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (validPaths.Length == 0 || _isBusy) return;
        _isBusy = true;
        RenderingBadge.Visibility = Visibility.Visible;
        SetStatus("Importing images…");
        try
        {
            var imported = await Task.Run(() => validPaths.Select(path =>
            {
                try
                {
                    var image = ImageEditService.Load(path);
                    return (Path: path, Image: image, Preview: ImageEditService.CreatePreview(image), Error: (string?)null);
                }
                catch (Exception ex) { return (Path: path, Image: (BitmapSource?)null, Preview: (BitmapSource?)null, Error: ex.Message); }
            }).ToArray());
            if (_closed) return;
            var succeeded = imported.Where(item => item.Image is not null && item.Preview is not null).ToArray();
            if (succeeded.Length == 0)
            {
                ToastRequested?.Invoke(this, new ToastMessage($"The selected image could not be imported. {imported[0].Error}", true));
                return;
            }
            if (Layers.Count > 0 || succeeded.Length > 1) PushUndo();
            ImageLayerItem? last = null;
            foreach (var item in succeeded)
            {
                var offset = 48 + Layers.Count * 34;
                last = new ImageLayerItem
                {
                    Path = item.Path, Name = IOPath.GetFileName(item.Path), OriginalImage = item.Image!, BaseImage = item.Image!, PreviewImage = item.Preview!,
                    X = SettingsService.Current.CenterNewLayers ? offset : 20, Y = SettingsService.Current.CenterNewLayers ? offset : 20
                };
                Layers.Add(last);
                _path ??= item.Path;
            }
            RebuildLayerVisuals();
            SelectLayer(last);
            EmptyState.Visibility = Visibility.Collapsed;
            UpdateWorkspaceSummary();
            _ = Dispatcher.BeginInvoke(FitComposition);
            var failed = imported.Length - succeeded.Length;
            if (failed > 0) ToastRequested?.Invoke(this, new ToastMessage($"Imported {succeeded.Length} image{(succeeded.Length == 1 ? "" : "s")}; {failed} could not be read.", true));
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Image import failed: {ex.Message}", true)); }
        finally { _isBusy = false; if (_renderCts is null) RenderingBadge.Visibility = Visibility.Collapsed; }
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
            border.LostMouseCapture += Layer_LostMouseCapture;
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
                border.Cursor = _isCropping ? Cursors.Cross : PanTool.IsChecked == true ? Cursors.Hand : Cursors.SizeAll;
            }
        }
    }

    private void SelectLayer(ImageLayerItem? layer)
    {
        _selectedLayer = layer;
        LayerList.SelectedItem = layer;
        UpdateSelectionVisuals();
        SyncControlsFromSelection();
        UpdateSelectionControls();
        UpdateWorkspaceSummary();
    }

    private void SyncControlsFromSelection()
    {
        _suppressControls = true;
        var a = _selectedLayer?.Adjustments ?? new ImageAdjustments();
        ExposureSlider.Value = a.Exposure; ContrastSlider.Value = a.Contrast; SaturationSlider.Value = a.Saturation; TemperatureSlider.Value = a.Temperature;
        HighlightsSlider.Value = a.Highlights; ShadowsSlider.Value = a.Shadows; VibranceSlider.Value = a.Vibrance;
        OpacitySlider.Value = (_selectedLayer?.Opacity ?? 1) * 100;
        if (_selectedLayer is not null) { WidthBox.Text = _selectedLayer.BaseImage.PixelWidth.ToString(); HeightBox.Text = _selectedLayer.BaseImage.PixelHeight.ToString(); }
        else { WidthBox.Clear(); HeightBox.Clear(); }
        _suppressControls = false;
        UpdateAdjustmentLabels();
    }

    private IEnumerable<ImageLayerItem> EditTargets() => EditTargetCombo.SelectedIndex == 1 ? Layers : _selectedLayer is null ? [] : [_selectedLayer];
    private ImageAdjustments CurrentAdjustments() => new(ExposureSlider.Value, ContrastSlider.Value, SaturationSlider.Value, TemperatureSlider.Value,
        HighlightsSlider.Value, ShadowsSlider.Value, VibranceSlider.Value);

    private void Adjustment_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ExposureValue is null) return;
        UpdateAdjustmentLabels();
        if (_suppressControls || _selectedLayer is null || _isBusy) return;
        _isDirty = true;
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
        HighlightsValue.Text = Math.Round(HighlightsSlider.Value).ToString("+0;-0;0");
        ShadowsValue.Text = Math.Round(ShadowsSlider.Value).ToString("+0;-0;0");
        VibranceValue.Text = Math.Round(VibranceSlider.Value).ToString("+0;-0;0");
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
    }

    private void Adjustment_Begin(object sender, MouseButtonEventArgs e) { if (_selectedLayer is not null && !_isBusy) PushUndo(); }
    private void Adjustment_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_selectedLayer is null || _suppressControls || _isBusy || e.IsRepeat) return;
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            PushUndo();
    }
    private void Opacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue is null) return;
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        if (_suppressControls || _isBusy || _selectedLayer is null) return;
        _isDirty = true;
        foreach (var layer in EditTargets()) { layer.Opacity = OpacitySlider.Value / 100; UpdateLayerVisual(layer); }
    }

    private async void QueueRender(IEnumerable<ImageLayerItem> requested)
    {
        var targets = requested.Distinct().ToArray();
        if (targets.Length == 0) return;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _renderCts = cancellation;
        var version = Interlocked.Increment(ref _renderVersion);
        RenderingBadge.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(90, cancellation.Token);
            foreach (var layer in targets)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (version != _renderVersion || !Layers.Contains(layer)) return;
                var baseImage = layer.BaseImage;
                var adjustments = layer.Adjustments;
                var preview = await Task.Run(() =>
                {
                    var source = ImageEditService.CreatePreview(baseImage);
                    if (adjustments == new ImageAdjustments()) return source;
                    var buffer = ImageEditService.CapturePixels(source);
                    var adjusted = ImageEditService.AdjustPixels(buffer, adjustments, cancellation.Token);
                    return ImageEditService.CreateBitmap(buffer, adjusted);
                }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (version != _renderVersion || !Layers.Contains(layer)) return;
                layer.PreviewImage = preview;
                UpdateLayerVisual(layer);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Preview failed: {ex.Message}", true)); }
        finally
        {
            if (version == _renderVersion) RenderingBadge.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_renderCts, cancellation))
            {
                _renderCts = null;
                cancellation.Dispose();
            }
        }
    }

    private void CancelPendingRender()
    {
        Interlocked.Increment(ref _renderVersion);
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
        RenderingBadge.Visibility = Visibility.Collapsed;
    }

    private void TransformTargets(Func<BitmapSource, BitmapSource> operation)
    {
        if (_isBusy) return;
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return;
        try
        {
            CancelPendingRender();
            var results = targets.Select(layer => operation(layer.BaseImage)).ToArray();
            PushUndo();
            for (var index = 0; index < targets.Length; index++)
            {
                targets[index].BaseImage = results[index];
                targets[index].PreviewImage = ImageEditService.CreatePreview(results[index]);
            }
            RebuildLayerVisuals(); QueueRender(targets); SyncControlsFromSelection(); UpdateWorkspaceSummary();
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Transform failed: {ex.Message}", true)); }
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => TransformTargets(i => ImageEditService.Rotate(i, -90));
    private void RotateRight_Click(object sender, RoutedEventArgs e) => TransformTargets(i => ImageEditService.Rotate(i, 90));
    private void FlipH_Click(object sender, RoutedEventArgs e) => TransformTargets(i => ImageEditService.Flip(i, true));
    private void FlipV_Click(object sender, RoutedEventArgs e) => TransformTargets(i => ImageEditService.Flip(i, false));
    private async void AutoTone_Click(object sender, RoutedEventArgs e) => await TransformTargetsAsync(ImageEditService.AutoTone, "Auto tone applied");
    private async void Grayscale_Click(object sender, RoutedEventArgs e) => await TransformTargetsAsync(ImageEditService.Grayscale, "Black and white applied");
    private async void Sepia_Click(object sender, RoutedEventArgs e) => await TransformTargetsAsync(ImageEditService.Sepia, "Sepia applied");
    private async void Sharpen_Click(object sender, RoutedEventArgs e) => await TransformTargetsAsync(ImageEditService.Sharpen, "Sharpening applied");

    private async Task TransformTargetsAsync(Func<BitmapSource, BitmapSource> operation, string completedStatus)
    {
        if (_isBusy) return;
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return;
        CancelPendingRender(); var before = CaptureSnapshot(); _isBusy = true; RenderingBadge.Visibility = Visibility.Visible;
        try
        {
            var sources = targets.Select(layer => layer.BaseImage).ToArray();
            var results = await Task.Run(() => sources.Select(operation).Select(result => (Base: result, Preview: ImageEditService.CreatePreview(result))).ToArray());
            if (_closed) return;
            PushUndo(before);
            for (var index = 0; index < targets.Length; index++) { targets[index].BaseImage = results[index].Base; targets[index].PreviewImage = results[index].Preview; }
            RebuildLayerVisuals(); QueueRender(targets); SyncControlsFromSelection(); UpdateWorkspaceSummary(); SetStatus(completedStatus);
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Filter failed: {ex.Message}", true)); }
        finally { _isBusy = false; if (_renderCts is null) RenderingBadge.Visibility = Visibility.Collapsed; }
    }

    private void TextTool_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        CancelCrop();
        _focusBeforeTextOverlay = Keyboard.FocusedElement;
        TextOverlay.Visibility = Visibility.Visible;
        TextLayerContent.SelectAll();
        TextLayerContent.Focus();
    }
    private void CancelText_Click(object sender, RoutedEventArgs e) => CloseTextDialog();

    private void CloseTextDialog()
    {
        TextOverlay.Visibility = Visibility.Collapsed;
        _focusBeforeTextOverlay?.Focus();
        _focusBeforeTextOverlay = null;
    }
    private void TextSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TextSizeValue is not null) TextSizeValue.Text = $"{TextSizeSlider.Value:0} px";
    }
    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        try
        {
            var text = TextLayerContent.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) { ToastRequested?.Invoke(this, new ToastMessage("Enter some text first.", true)); return; }
            var font = (TextFontCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Segoe UI";
            var parsed = ColorConverter.ConvertFromString(TextColorBox.Text.Trim());
            if (parsed is not Color color) throw new FormatException("Enter a color such as #FFFFFFFF or #A855F7.");
            var image = ImageEditService.CreateTextBitmap(text, font, TextSizeSlider.Value, TextBoldCheck.IsChecked == true, color);
            PushUndo();
            var bounds = CompositionBounds();
            var x = Layers.Count == 0 ? 60 : bounds.Left + Math.Max(0, (bounds.Width - image.PixelWidth) / 2);
            var y = Layers.Count == 0 ? 60 : bounds.Top + Math.Max(0, (bounds.Height - image.PixelHeight) / 2);
            var label = text.Replace("\r", " ").Replace("\n", " ").Trim();
            var layer = new ImageLayerItem
            {
                Path = string.Empty, Name = $"Text · {label[..Math.Min(24, label.Length)]}",
                OriginalImage = image, BaseImage = image, PreviewImage = image, X = x, Y = y
            };
            Layers.Add(layer); RebuildLayerVisuals(); SelectLayer(layer); UpdateWorkspaceSummary();
            CloseTextDialog(); SetStatus("Text layer added");
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Couldn't create the text layer: {ex.Message}", true)); }
    }

    private void Layer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ImageLayerItem layer) return;
        SelectLayer(layer);
        if (_isBusy || _isCropping || PanTool.IsChecked == true || MoveTool.IsChecked != true) return;
        _movingLayer = layer; _moveStart = e.GetPosition(CompositionCanvas); _layerStart = new Point(layer.X, layer.Y);
        _moveSnapshot = CaptureSnapshot(); _moveUndoCommitted = false;
        border.CaptureMouse(); e.Handled = true;
    }
    private void Layer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_movingLayer is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(CompositionCanvas); var x = Math.Max(0, _layerStart.X + point.X - _moveStart.X); var y = Math.Max(0, _layerStart.Y + point.Y - _moveStart.Y);
        if (SettingsService.Current.SnapLayersToGrid) { x = Math.Round(x / 10) * 10; y = Math.Round(y / 10) * 10; }
        if (Math.Abs(x - _movingLayer.X) < .01 && Math.Abs(y - _movingLayer.Y) < .01) return;
        if (!_moveUndoCommitted) { PushUndo(_moveSnapshot); _moveUndoCommitted = true; }
        _movingLayer.X = x; _movingLayer.Y = y; UpdateLayerVisual(_movingLayer);
    }
    private void Layer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border) border.ReleaseMouseCapture();
        FinishLayerMove();
        e.Handled = true;
    }
    private void Layer_LostMouseCapture(object sender, MouseEventArgs e) => FinishLayerMove();
    private void FinishLayerMove()
    {
        if (_movingLayer is null) return;
        _movingLayer = null;
        _moveSnapshot = null;
        _moveUndoCommitted = false;
        ResizeCanvasToContent();
        UpdateWorkspaceSummary();
    }

    private void MoveTool_Click(object sender, RoutedEventArgs e) => ActivateMoveTool();
    private void PanTool_Click(object sender, RoutedEventArgs e) => ActivatePanTool();

    private void CropTool_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isCropping = CropTool.IsChecked == true && _selectedLayer is not null;
        if (_isCropping) { CancelPan(); MoveTool.IsChecked = false; PanTool.IsChecked = false; CanvasScroll.Cursor = Cursors.Cross; }
        else { CropTool.IsChecked = false; MoveTool.IsChecked = true; PanTool.IsChecked = false; CanvasScroll.Cursor = Cursors.Arrow; }
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
        PushUndo(); layer.BaseImage = ImageEditService.Crop(layer.BaseImage, new Int32Rect(x, y, width, height)); layer.X = _cropRect.Left; layer.Y = _cropRect.Top; layer.PreviewImage = ImageEditService.CreatePreview(layer.BaseImage);
        CancelCrop(); RebuildLayerVisuals(); QueueRender([layer]); SyncControlsFromSelection(); UpdateWorkspaceSummary();
    }
    private void CancelCrop_Click(object sender, RoutedEventArgs e) => CancelCrop();
    private void CancelCrop() { _isDraggingCrop = false; _isCropping = false; CropTool.IsChecked = false; MoveTool.IsChecked = true; CompositionCanvas.ReleaseMouseCapture(); ApplyCropButton.Visibility = CancelCropButton.Visibility = Visibility.Collapsed; ClearCropVisuals(); CanvasScroll.Cursor = Cursors.Arrow; UpdateSelectionVisuals(); }
    private void ClearCropVisuals() { CropRectangle.Visibility = Visibility.Collapsed; ApplyCropButton.IsEnabled = false; _cropRect = Rect.Empty; foreach (var shade in new[] { CropShadeTop, CropShadeBottom, CropShadeLeft, CropShadeRight }) { shade.Width = 0; shade.Height = 0; } }

    private async void Resize_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (!int.TryParse(WidthBox.Text, out var width) || width < 1 || !int.TryParse(HeightBox.Text, out var height) || height < 1) { ToastRequested?.Invoke(this, new ToastMessage("Enter valid width and height values.", true)); return; }
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return; CancelPendingRender(); var before = CaptureSnapshot(); _isBusy = true; RenderingBadge.Visibility = Visibility.Visible;
        try
        {
            var lockAspect = LockAspectCheck.IsChecked == true;
            var highQuality = SettingsService.Current.HighQualityPreview;
            var sources = targets.Select(layer => layer.BaseImage).ToArray();
            var results = await Task.Run(() => sources.Select(source =>
            {
                var targetHeight = lockAspect ? Math.Max(1, (int)Math.Round(width * (double)source.PixelHeight / source.PixelWidth)) : height;
                var result = ImageEditService.Resize(source, width, targetHeight, highQuality);
                return (Base: result, Preview: ImageEditService.CreatePreview(result, highQuality: highQuality));
            }).ToArray());
            if (_closed) return;
            PushUndo(before);
            for (var index = 0; index < targets.Length; index++) { targets[index].BaseImage = results[index].Base; targets[index].PreviewImage = results[index].Preview; }
            RebuildLayerVisuals(); QueueRender(targets); SyncControlsFromSelection(); UpdateWorkspaceSummary();
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Resize failed: {ex.Message}", true)); }
        finally { _isBusy = false; if (_renderCts is null) RenderingBadge.Visibility = Visibility.Collapsed; }
    }
    private void Upscale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (UpscaleValue is not null) UpscaleValue.Text = $"{UpscaleSlider.Value:0}%"; }
    private async void Upscale_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return; var factor = UpscaleSlider.Value / 100;
        if (factor <= 1) { ToastRequested?.Invoke(this, new ToastMessage("Choose a scale above 100% to upscale.")); return; }
        CancelPendingRender(); var before = CaptureSnapshot(); _isBusy = true; RenderingBadge.Visibility = Visibility.Visible;
        _upscaleCts = new CancellationTokenSource();
        UpscaleButton.IsEnabled = false; UpscaleProgress.Value = 0; UpscaleProgress.Visibility = Visibility.Visible;
        try
        {
            var results = new List<(BitmapSource Base, BitmapSource Preview)>(targets.Length);
            for (var index = 0; index < targets.Length; index++)
            {
                var layer = targets[index];
                var itemProgress = new Progress<double>(value => UpscaleProgress.Value = (index + value / 100) / targets.Length * 100);
                var result = await AiUpscaleService.UpscaleAsync(layer.BaseImage, factor, itemProgress, _upscaleCts.Token);
                results.Add((result, await Task.Run(() => ImageEditService.CreatePreview(result), _upscaleCts.Token)));
            }
            if (_closed) return;
            PushUndo(before);
            for (var index = 0; index < targets.Length; index++) { targets[index].BaseImage = results[index].Base; targets[index].PreviewImage = results[index].Preview; }
            RebuildLayerVisuals(); QueueRender(targets); SyncControlsFromSelection(); UpdateWorkspaceSummary(); SetStatus($"AI-upscaled {targets.Length} layer{(targets.Length == 1 ? "" : "s")} to {UpscaleSlider.Value:0}% with Real-ESRGAN");
        }
        catch (OperationCanceledException) { SetStatus("AI upscale canceled"); }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Upscale failed: {ex.Message}", true)); }
        finally
        {
            _upscaleCts?.Dispose(); _upscaleCts = null;
            _isBusy = false; if (_renderCts is null) RenderingBadge.Visibility = Visibility.Collapsed; UpscaleProgress.Visibility = Visibility.Collapsed; UpscaleButton.IsEnabled = true;
        }
    }
    private void Background_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (BackgroundValue is not null) BackgroundValue.Text = $"{BackgroundSlider.Value:0}"; }
    private async void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return; CancelPendingRender(); var before = CaptureSnapshot(); _isBusy = true; RenderingBadge.Visibility = Visibility.Visible;
        try
        {
            var tolerance = 12 + BackgroundSlider.Value * 1.1; var feather = 28 + BackgroundSlider.Value * .7;
            var mode = BackgroundModeCombo.SelectedIndex switch { 1 => BackgroundRemovalMode.InnerOnly, 2 => BackgroundRemovalMode.AllMatching, _ => BackgroundRemovalMode.OuterOnly };
            var sources = targets.Select(layer => ImageEditService.FreezeForBackgroundAccess(layer.BaseImage)).ToArray();
            var results = await Task.Run(() => sources.Select(source =>
            {
                var result = ImageEditService.RemoveBackground(source, tolerance, feather, mode);
                return (Base: result, Preview: ImageEditService.CreatePreview(result));
            }).ToArray());
            if (_closed) return;
            PushUndo(before);
            for (var index = 0; index < targets.Length; index++) { targets[index].BaseImage = results[index].Base; targets[index].PreviewImage = results[index].Preview; }
            RebuildLayerVisuals(); QueueRender(targets);
            var region = mode switch { BackgroundRemovalMode.InnerOnly => "inner", BackgroundRemovalMode.AllMatching => "all matching", _ => "outer" };
            SetStatus($"Removed {region} background locally from {targets.Length} layer{(targets.Length == 1 ? "" : "s")}");
        }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Background removal failed: {ex.Message}", true)); }
        finally { _isBusy = false; if (_renderCts is null) RenderingBadge.Visibility = Visibility.Collapsed; }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var targets = EditTargets().ToArray(); if (targets.Length == 0) return;
        if (SettingsService.Current.ConfirmBeforeReset && MessageBox.Show("Reset edits for the current edit target?", "Hoshino Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CancelPendingRender();
        PushUndo(); foreach (var layer in targets) { layer.BaseImage = layer.OriginalImage; layer.PreviewImage = ImageEditService.CreatePreview(layer.OriginalImage); layer.Adjustments = new(); layer.Scale = 1; layer.Opacity = 1; }
        RebuildLayerVisuals(); SyncControlsFromSelection(); UpdateWorkspaceSummary();
    }

    private WorkspaceSnapshot CaptureSnapshot() => new(Layers.Select(l => new LayerState(l.Id, l.Path, l.Name, l.OriginalImage, l.BaseImage, l.PreviewImage, l.Adjustments, l.X, l.Y, l.Scale, l.Opacity)).ToList(), _selectedLayer?.Id);
    private void PushUndo(WorkspaceSnapshot? snapshot = null)
    {
        _isDirty = true;
        _undo.Push(snapshot ?? CaptureSnapshot());
        var configuredLimit = Math.Clamp(SettingsService.Current.UndoLimit, 1, 100);
        var currentImageBytes = Layers.Sum(layer =>
        {
            var baseBytes = (long)layer.BaseImage.PixelWidth * layer.BaseImage.PixelHeight * 4;
            var previewBytes = ReferenceEquals(layer.BaseImage, layer.PreviewImage) ? 0 : (long)layer.PreviewImage.PixelWidth * layer.PreviewImage.PixelHeight * 4;
            return baseBytes + previewBytes;
        });
        var memoryLimit = currentImageBytes == 0 ? configuredLimit : Math.Max(1, (int)(384L * 1024 * 1024 / currentImageBytes));
        TrimStack(_undo, Math.Min(configuredLimit, memoryLimit));
        _redo.Clear(); UpdateHistoryButtons();
    }
    private static void TrimStack(Stack<WorkspaceSnapshot> stack, int limit)
    {
        if (stack.Count <= limit) return;
        var newestFirst = stack.ToArray();
        stack.Clear();
        for (var index = Math.Min(limit, newestFirst.Length) - 1; index >= 0; index--) stack.Push(newestFirst[index]);
    }
    private void Restore(WorkspaceSnapshot snapshot)
    {
        CancelPendingRender(); Layers.Clear();
        foreach (var state in snapshot.Layers) Layers.Add(new ImageLayerItem { Id = state.Id, Path = state.Path, Name = state.Name, OriginalImage = state.Original, BaseImage = state.Base, PreviewImage = state.Preview, Adjustments = state.Adjustments, X = state.X, Y = state.Y, Scale = state.Scale, Opacity = state.Opacity });
        RebuildLayerVisuals(); SelectLayer(Layers.FirstOrDefault(l => l.Id == snapshot.SelectedId) ?? Layers.LastOrDefault()); UpdateWorkspaceSummary();
    }
    private void Undo_Click(object sender, RoutedEventArgs e) { if (_undo.Count == 0) return; _redo.Push(CaptureSnapshot()); Restore(_undo.Pop()); _isDirty = true; UpdateHistoryButtons(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (_redo.Count == 0) return; _undo.Push(CaptureSnapshot()); Restore(_redo.Pop()); _isDirty = true; UpdateHistoryButtons(); }
    private void UpdateHistoryButtons() { UndoButton.IsEnabled = _undo.Count > 0; RedoButton.IsEnabled = _redo.Count > 0; }

    private void BringForward_Click(object sender, RoutedEventArgs e) { if (_selectedLayer is null || _isBusy) return; var i = Layers.IndexOf(_selectedLayer); if (i >= Layers.Count - 1) return; PushUndo(); Layers.Move(i, i + 1); RebuildLayerVisuals(); }
    private void SendBackward_Click(object sender, RoutedEventArgs e) { if (_selectedLayer is null || _isBusy) return; var i = Layers.IndexOf(_selectedLayer); if (i <= 0) return; PushUndo(); Layers.Move(i, i - 1); RebuildLayerVisuals(); }
    private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (LayerList.SelectedItem is ImageLayerItem layer && layer != _selectedLayer) SelectLayer(layer); }
    private void RemoveLayer_Click(object sender, RoutedEventArgs e) => RemoveSelectedLayer();
    private void DuplicateLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is null || _isBusy) return;
        PushUndo();
        var source = _selectedLayer;
        var copy = new ImageLayerItem
        {
            Path = source.Path, Name = $"{source.Name} copy", OriginalImage = source.OriginalImage, BaseImage = source.BaseImage,
            PreviewImage = source.PreviewImage, Adjustments = source.Adjustments, X = source.X + 24, Y = source.Y + 24,
            Scale = source.Scale, Opacity = source.Opacity
        };
        Layers.Insert(Layers.IndexOf(source) + 1, copy);
        RebuildLayerVisuals(); SelectLayer(copy); UpdateWorkspaceSummary();
    }
    private void RemoveSelectedLayer()
    {
        if (_selectedLayer is null || _isBusy) return; CancelPendingRender(); PushUndo(); var index = Layers.IndexOf(_selectedLayer); Layers.Remove(_selectedLayer); RebuildLayerVisuals(); SelectLayer(Layers.Count == 0 ? null : Layers[Math.Min(index, Layers.Count - 1)]); UpdateWorkspaceSummary();
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

    private void UpdateSelectionControls()
    {
        var hasLayers = Layers.Count > 0;
        PhotoLayerToolControls.IsEnabled = hasLayers;
        PhotoInspectorControls.IsEnabled = hasLayers;
        LayerListActions.IsEnabled = _selectedLayer is not null;
        EditTargetCombo.IsEnabled = hasLayers;
        PhotoExportButton.IsEnabled = hasLayers;
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
    private void CanvasScroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle && !(e.ChangedButton == MouseButton.Left && PanTool.IsChecked == true)) return;
        _isPanning = true; _panStart = e.GetPosition(CanvasScroll);
        _panHorizontalStart = CanvasScroll.HorizontalOffset; _panVerticalStart = CanvasScroll.VerticalOffset;
        CanvasScroll.CaptureMouse(); CanvasScroll.Cursor = Cursors.Hand; e.Handled = true;
    }
    private void CanvasScroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var point = e.GetPosition(CanvasScroll);
        CanvasScroll.ScrollToHorizontalOffset(_panHorizontalStart - (point.X - _panStart.X));
        CanvasScroll.ScrollToVerticalOffset(_panVerticalStart - (point.Y - _panStart.Y));
        e.Handled = true;
    }
    private void CanvasScroll_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        CancelPan(); e.Handled = true;
    }
    private void CanvasScroll_LostMouseCapture(object sender, MouseEventArgs e) => _isPanning = false;
    private void CancelPan()
    {
        if (_isPanning) CanvasScroll.ReleaseMouseCapture();
        _isPanning = false;
        CanvasScroll.Cursor = PanTool.IsChecked == true ? Cursors.Hand : Cursors.Arrow;
    }

    private sealed record CompositionLayer(BitmapSource BaseImage, ImageAdjustments Adjustments, double X, double Y, double Scale, double Opacity);

    private static Rect CompositionBounds(IReadOnlyList<CompositionLayer> layers)
    {
        if (layers.Count == 0) return Rect.Empty;
        var left = layers.Min(layer => layer.X); var top = layers.Min(layer => layer.Y);
        var right = layers.Max(layer => layer.X + layer.BaseImage.PixelWidth * layer.Scale);
        var bottom = layers.Max(layer => layer.Y + layer.BaseImage.PixelHeight * layer.Scale);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static Task<BitmapSource> RenderCompositionAsync(IReadOnlyList<CompositionLayer> layers, CancellationToken cancellationToken)
        => Task.Run<BitmapSource>(() =>
        {
            var bounds = CompositionBounds(layers); var width = (int)Math.Ceiling(bounds.Width); var height = (int)Math.Ceiling(bounds.Height);
            if ((long)width * height > 50_000_000) throw new InvalidOperationException("The merged canvas exceeds the 50 megapixel export safety limit.");
            var rendered = new List<BitmapSource>(layers.Count);
            foreach (var layer in layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (layer.Adjustments == new ImageAdjustments()) rendered.Add(layer.BaseImage);
                else
                {
                    var buffer = ImageEditService.CapturePixels(layer.BaseImage);
                    rendered.Add(ImageEditService.CreateBitmap(buffer, ImageEditService.AdjustPixels(buffer, layer.Adjustments, cancellationToken)));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                for (var index = 0; index < layers.Count; index++)
                {
                    var layer = layers[index];
                    dc.PushOpacity(layer.Opacity);
                    dc.DrawImage(rendered[index], new Rect(layer.X - bounds.Left, layer.Y - bounds.Top, layer.BaseImage.PixelWidth * layer.Scale, layer.BaseImage.PixelHeight * layer.Scale));
                    dc.Pop();
                }
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
        }, cancellationToken);

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import images", Multiselect = true, Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        if (dialog.ShowDialog() == true) await LoadImagesAsync(dialog.FileNames);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (Layers.Count == 0) { ToastRequested?.Invoke(this, new ToastMessage("Import an image before exporting.", true)); return; }
        var settings = SettingsService.Current; var extension = settings.DefaultExportFormat.ToUpperInvariant() switch { "JPEG" => ".jpg", "TIFF" => ".tiff", "BMP" => ".bmp", _ => ".png" };
        var initial = Layers.Count == 1 ? $"{IOPath.GetFileNameWithoutExtension(Layers[0].Name)}-edited{extension}" : $"hoshino-composite{extension}";
        var dialog = new SaveFileDialog { Title = "Merge and export image", FileName = initial, Filter = "PNG image|*.png|JPEG image|*.jpg|TIFF image|*.tiff|Bitmap image|*.bmp", DefaultExt = extension, AddExtension = true };
        if (settings.RememberExportFolder && Directory.Exists(settings.LastExportFolder)) dialog.InitialDirectory = settings.LastExportFolder;
        if (dialog.ShowDialog() != true) return;
        if (Layers.Any(layer => !string.IsNullOrWhiteSpace(layer.Path) &&
                IOPath.GetFullPath(layer.Path).Equals(IOPath.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) &&
            MessageBox.Show("This export will replace one of the composition's source images. Continue?", "Replace source image?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var composition = Layers.Select(layer => new CompositionLayer(layer.BaseImage, layer.Adjustments, layer.X, layer.Y, layer.Scale, layer.Opacity)).ToArray();
        CancelPendingRender(); _isBusy = true; RenderingBadge.Visibility = Visibility.Visible; _exportCts = new CancellationTokenSource();
        try
        {
            var result = await RenderCompositionAsync(composition, _exportCts.Token);
            await Task.Run(() => ImageEditService.Save(result, dialog.FileName, settings.JpegQuality), _exportCts.Token);
            _isDirty = false; SetStatus($"Exported · {IOPath.GetFileName(dialog.FileName)}"); ToastRequested?.Invoke(this, new ToastMessage("Composition merged and exported."));
            settings.LastExportFolder = IOPath.GetDirectoryName(dialog.FileName);
            try { SettingsService.Save(); }
            catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"The image exported, but the export folder preference could not be saved: {ex.Message}", true)); }
        }
        catch (OperationCanceledException) { SetStatus("Image export canceled"); }
        catch (Exception ex) { ToastRequested?.Invoke(this, new ToastMessage($"Export failed: {ex.Message}", true)); }
        finally { _exportCts.Dispose(); _exportCts = null; _isBusy = false; RenderingBadge.Visibility = Visibility.Collapsed; }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
    private void SetStatus(string status) { Status = status; StatusChanged?.Invoke(this, status); }
    private void Root_DragOver(object sender, DragEventArgs e) { var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; e.Effects = paths?.Any(p => MediaTypeService.GetKind(p) == EditorKind.Photo) == true ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Root_Drop(object sender, DragEventArgs e) { var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths is not null) await LoadImagesAsync(paths); }
}
