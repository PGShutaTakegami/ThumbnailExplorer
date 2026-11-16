using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ThumbnailExplorer;

public partial class MainWindow
{
    private readonly List<CanvasLayer> _layers = new();
    private int _activeLayer = -1;
    private bool _settingLayerControls;
    private Point? _dragOrigin;
    private Point _layerOrigin;
    private bool _eraseRecorded;

    private void InitializeLayers()
    {
        int[] dimensions = [24, 40, 56, 72, 96, 120, 144, 168, 192, 224, 256, 320, 384, 448, 512];
        WidthPicker.ItemsSource = dimensions;
        HeightPicker.ItemsSource = dimensions;
        Ink.StrokeErasing += (_, _) =>
        {
            if (_eraseRecorded)
                return;
            _eraseRecorded = true;
            RecordUndo(new StrokeCollection(Ink.Strokes));
            MarkDirty();
        };
        Ink.StrokeErased += (_, _) => UpdateEditorButtons();
        Ink.PreviewMouseLeftButtonUp += (_, _) => _eraseRecorded = false;
        Ink.PreviewStylusUp += (_, _) => _eraseRecorded = false;
        Ink.LostMouseCapture += (_, _) => _eraseRecorded = false;
    }

    private void LoadLayers(IReadOnlyList<CanvasLayer>? layers)
    {
        _layers.Clear();
        if (layers is not null)
            _layers.AddRange(layers);
        _activeLayer = _layers.Count - 1;
        RenderLayers();
        SyncLayerControls();
    }

    private void SyncLayerControls()
    {
        _settingLayerControls = true;
        LayerPicker.ItemsSource = _layers.Select((layer, index) => $"{index + 1}. {layer.Label}").ToArray();
        LayerPicker.SelectedIndex = _activeLayer;
        LayerControls.Visibility = _layers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LayerForwardButton.IsEnabled = _activeLayer >= 0 && _activeLayer < _layers.Count - 1;
        LayerBackwardButton.IsEnabled = _activeLayer > 0;
        RemoveLayerButton.IsEnabled = _activeLayer >= 0;
        CanvasLayer? layer = _activeLayer >= 0 ? _layers[_activeLayer] : null;
        TextProperties.Visibility = layer is { Kind: "text" } ? Visibility.Visible : Visibility.Collapsed;
        ShapeProperties.Visibility = layer is not null && layer.Kind != "text" ? Visibility.Visible : Visibility.Collapsed;
        if (layer is { Kind: "text" })
        {
            CaptionInput.Text = layer.Text;
            FontPicker.SelectedItem = layer.Font;
            FontSizePicker.SelectedItem = (int)layer.FontSize;
            SetColorSwatch(TextColorButton, ReadColor(layer.Color));
        }
        else if (layer is not null)
        {
            WidthPicker.SelectedItem = (int)layer.Width;
            HeightPicker.SelectedItem = (int)layer.Height;
            SetColorSwatch(ShapeColorButton, ReadColor(layer.Color));
        }
        _settingLayerControls = false;
    }

    private static Color ReadColor(string color)
    {
        try { return (Color)ColorConverter.ConvertFromString(color); }
        catch (FormatException) { return Colors.Black; }
    }

    private void RenderLayers()
    {
        Layers.Children.Clear();
        for (int i = 0; i < _layers.Count; i++)
        {
            CanvasLayer layer = _layers[i];
            var brush = new SolidColorBrush(ReadColor(layer.Color));
            FrameworkElement element;
            if (layer.Kind == "text")
            {
                element = new TextBlock
                {
                    Text = layer.Text,
                    Width = layer.Width,
                    MinHeight = Math.Min(layer.FontSize, 50),
                    Foreground = brush,
                    Background = Brushes.Transparent,
                    FontFamily = new FontFamily(layer.Font),
                    FontSize = layer.FontSize,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                };
            }
            else
            {
                Shape shape = layer.Kind switch
                {
                    "ellipse" => new Ellipse(),
                    "triangle" => new Polygon { Points = new PointCollection([new Point(.5, 0), new Point(1, 1), new Point(0, 1)]) },
                    _ => new Rectangle()
                };
                shape.Width = layer.Width;
                shape.Height = layer.Height;
                shape.Fill = brush;
                shape.Stretch = Stretch.Fill;
                element = shape;
            }

            element.Tag = i;
            element.Cursor = Cursors.SizeAll;
            Canvas.SetLeft(element, layer.X);
            Canvas.SetTop(element, layer.Y);
            Layers.Children.Add(element);
        }
    }

    private void AddTextClick(object sender, RoutedEventArgs e)
    {
        var style = ThumbnailStore.DefaultText;
        AddLayer(new CanvasLayer("text", 76, 190, 360, 0, style.Color, L.Get("Text"), style.Font, style.Size));
        CaptionInput.Focus();
        CaptionInput.SelectAll();
    }

    private void AddShapeClick(object sender, RoutedEventArgs e)
    {
        string kind = ShapePicker.SelectedIndex switch { 1 => "ellipse", 2 => "triangle", _ => "rectangle" };
        AddLayer(new CanvasLayer(kind, 172, 180, 168, 144, "#FF8978EF"));
    }

    private void AddLayer(CanvasLayer layer)
    {
        if (_layers.Count == 100)
        {
            Status.Text = L.Get("LayerLimit");
            return;
        }
        double offset = (_layers.Count % 6) * 10;
        _layers.Add(layer with { X = layer.X + offset, Y = layer.Y + offset });
        _activeLayer = _layers.Count - 1;
        RenderLayers();
        SyncLayerControls();
        MarkDirty();
    }

    private void RemoveLayerClick(object sender, RoutedEventArgs e)
    {
        if (_activeLayer < 0)
            return;
        _layers.RemoveAt(_activeLayer);
        _activeLayer = Math.Min(_activeLayer, _layers.Count - 1);
        RenderLayers();
        SyncLayerControls();
        MarkDirty();
    }

    private void LayerForwardClick(object sender, RoutedEventArgs e) => ReorderLayer(1);
    private void LayerBackwardClick(object sender, RoutedEventArgs e) => ReorderLayer(-1);

    private void ReorderLayer(int offset)
    {
        int target = _activeLayer + offset;
        if (_activeLayer < 0 || target < 0 || target >= _layers.Count)
            return;
        (_layers[_activeLayer], _layers[target]) = (_layers[target], _layers[_activeLayer]);
        _activeLayer = target;
        RenderLayers();
        SyncLayerControls();
        MarkDirty();
    }

    private void LayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _settingLayerControls || LayerPicker.SelectedIndex < 0)
            return;
        _activeLayer = LayerPicker.SelectedIndex;
        SyncLayerControls();
    }

    private void CaptionInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _settingLayerControls || _activeLayer < 0)
            return;
        _layers[_activeLayer] = _layers[_activeLayer] with { Text = CaptionInput.Text };
        RenderLayers();
        // Keep the caret in the editor as the layer label updates.
        _settingLayerControls = true;
        LayerPicker.ItemsSource = _layers.Select((layer, index) => $"{index + 1}. {layer.Label}").ToArray();
        LayerPicker.SelectedIndex = _activeLayer;
        _settingLayerControls = false;
        MarkDirty();
    }

    private void FontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _settingLayerControls || _activeLayer < 0 || _layers[_activeLayer].Kind != "text")
            return;
        var layer = _layers[_activeLayer];
        ReplaceLayer(layer with
        {
            Font = FontPicker.SelectedItem as string ?? layer.Font,
            FontSize = FontSizePicker.SelectedItem is int size ? size : layer.FontSize
        });
    }

    private void ShapeSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _settingLayerControls || _activeLayer < 0 || _layers[_activeLayer].Kind == "text")
            return;
        var layer = _layers[_activeLayer];
        double width = WidthPicker.SelectedItem is int w ? w : layer.Width;
        double height = HeightPicker.SelectedItem is int h ? h : layer.Height;
        ReplaceLayer(layer with { Width = width, Height = height });
    }

    private void TextColorClick(object sender, RoutedEventArgs e) => ChangeLayerColor();
    private void ShapeColorClick(object sender, RoutedEventArgs e) => ChangeLayerColor();

    private void ChangeLayerColor()
    {
        if (_activeLayer >= 0 && ChooseColor(ReadColor(_layers[_activeLayer].Color)) is { } color)
            ReplaceLayer(_layers[_activeLayer] with { Color = color.ToString() });
    }

    private void ReplaceLayer(CanvasLayer layer)
    {
        _layers[_activeLayer] = layer;
        RenderLayers();
        SyncLayerControls();
        MarkDirty();
    }

    private void LayerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { Tag: int index })
            return;
        _activeLayer = index;
        SyncLayerControls();
        _dragOrigin = e.GetPosition(Layers);
        _layerOrigin = new Point(_layers[index].X, _layers[index].Y);
        Layers.CaptureMouse();
        e.Handled = true;
    }

    private void LayerMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;
        var offset = e.GetPosition(Layers) - start;
        var layer = _layers[_activeLayer];
        double x = Math.Clamp(_layerOrigin.X + offset.X, 16 - layer.Width, 496);
        double y = Math.Clamp(_layerOrigin.Y + offset.Y, 16 - Math.Max(layer.Height, layer.FontSize), 496);
        if (x == layer.X && y == layer.Y)
            return;
        _layers[_activeLayer] = layer with { X = x, Y = y };
        Canvas.SetLeft(Layers.Children[_activeLayer], x);
        Canvas.SetTop(Layers.Children[_activeLayer], y);
        MarkDirty();
    }

    private void LayerMouseUp(object sender, MouseButtonEventArgs e) => Layers.ReleaseMouseCapture();
    private void LayerLostCapture(object sender, MouseEventArgs e) => _dragOrigin = null;

    private void PenChecked(object sender, RoutedEventArgs e)
    {
        EraserButton.IsChecked = false;
        UpdateInkMode();
    }

    private void EraserChecked(object sender, RoutedEventArgs e)
    {
        PenButton.IsChecked = false;
        UpdateInkMode();
    }

    private void PenUnchecked(object sender, RoutedEventArgs e) => UpdateInkMode();
    private void EraserUnchecked(object sender, RoutedEventArgs e) => UpdateInkMode();

    private void UpdateInkMode()
    {
        Ink.EditingMode = PenButton.IsChecked == true ? InkCanvasEditingMode.Ink :
            EraserButton.IsChecked == true ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.None;
        Ink.IsHitTestVisible = Ink.EditingMode != InkCanvasEditingMode.None;
    }
}
