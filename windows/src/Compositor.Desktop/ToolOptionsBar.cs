using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;
using Compositor.Core.Format;

namespace Compositor.Desktop;

/// <summary>Which of the Shape tool's amounts was asked for.</summary>
internal enum ShapeSetting
{
    CornerRadius,
    LineWidth,
}

/// <summary>Which of the magic wand's amounts was asked for.</summary>
internal enum WandSetting
{
    Tolerance,
    SampleSize,
}

/// <summary>
/// The strip of options above the canvas, as the Mac keeps above its own: what the tool in hand can be told.
/// The port's amounts are reached from the Tools menu and answered through prompts, so the bar shows each one
/// as its name and its value and hands the asking back to the window, rather than growing a second set of
/// sliders beside the first.
/// </summary>
internal sealed class ToolOptionsBar : Border
{
    /// <summary>How tall the strip is, which is the Mac's own tool header.</summary>
    private const double StripHeight = 42;

    private readonly ToolOptions _options;
    private readonly TextBlock _title = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 0, 16, 0),
    };
    private readonly TextBlock _zoom = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _cells = new() { Orientation = Orientation.Horizontal, Spacing = 16 };

    /// <summary>Every row, by the name of the tools it belongs to: a name may cover several controls.</summary>
    private readonly Dictionary<string, List<Control>> _named = [];

    /// <summary>A setting was changed by the bar, so the window pushes it to the canvas and says what it is.</summary>
    public event Action? Changed;

    /// <summary>One of the brush's amounts was asked for, by the name the window's own prompt knows it by.</summary>
    public event Action<BrushSetting>? BrushSettingAsked;

    /// <summary>One of the magic wand's amounts was asked for.</summary>
    public event Action<WandSetting>? WandSettingAsked;

    /// <summary>One of the Shape tool's amounts was asked for.</summary>
    public event Action<ShapeSetting>? ShapeSettingAsked;

    /// <summary>A colour swatch was clicked: true for the foreground, false for the gradient's background.</summary>
    public event Action<bool>? ColourAsked;

    /// <summary>A layer is to be flipped, across its own middle, one way or the other.</summary>
    public event Action<bool>? FlipAsked;

    /// <summary>Which of the crop ratios was chosen, by its place in the window's own list.</summary>
    public event Action<int>? CropRatioChosen;
    public event Action? CropApplied;
    public event Action? CropCancelled;

    /// <summary>The Type tool's text is to be edited.</summary>
    public event Action? TextAsked;

    public ToolOptionsBar(ToolOptions options)
    {
        _options = options;
        Height = StripHeight;
        Background = Skin.ChromeBrush;
        Padding = new Thickness(10, 0, 10, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(_title);
        row.Children.Add(_cells);
        Child = row;
        Build();
    }

    /// <summary>
    /// Shows the rows the tool in hand has. Called when the tool or the panel selection changes and never on a
    /// repaint: a row rebuilt under the pointer's own drag would drop the drag.
    /// </summary>
    public void Show(Tool tool, bool hasDocument, bool maskSelected)
    {
        var brush = tool is Tool.Brush or Tool.Clone or Tool.Blur or Tool.Liquify or Tool.Smudge or Tool.Heal;
        _loading = true;
        try
        {
            On("brush", brush && hasDocument);
            On("mode", tool == Tool.Brush);
            On("heal", tool == Tool.Heal);
            On("clone", tool == Tool.Clone);
            On("mask", tool == Tool.Brush && maskSelected);
            On("blur", tool == Tool.Blur);
            On("marqueeShape", tool is Tool.Marquee or Tool.Ellipse);
            On("lasso", tool is Tool.Lasso or Tool.Polygon);
            On("eye", tool == Tool.Eyedropper);
            On("wand", tool == Tool.Wand);
            On("gradient", tool == Tool.Gradient);
            On("shape", tool == Tool.Shape);
            On("corner", tool == Tool.Shape && _options.Shape != ShapeKind.Line);
            On("linewidth", tool == Tool.Shape && _options.Shape == ShapeKind.Line);
            On("crop", tool == Tool.Crop);
            On("transform", tool == Tool.Move && hasDocument);
            On("type", tool == Tool.Type);
            On("zoom", tool == Tool.Pan);
            _title.Text = Names.TryGetValue(tool, out var name) ? L10n.T(name) : "";
            // The marquee's shape and the lasso's kind *are* the tool in hand, so the bar follows the tool
            // rather than the other way round: picking one here asks for the tool the window already has.
            _marqueeShape.SelectedIndex = tool == Tool.Ellipse ? 1 : 0;
            _lassoKind.SelectedIndex = tool == Tool.Polygon ? 1 : 0;
            Refresh();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The zoom the window is showing, for the Pan and Zoom rows.</summary>
    public void ShowZoom(double percent) => _zoom.Text = L10n.F("Zoom {0:0}%", percent);

    /// <summary>Whether a row is on show, which is what the self check reads to see the gating works.</summary>
    internal bool Shows(string name) => _named.TryGetValue(name, out var cells) && cells[0].IsVisible;

    /// <summary>
    /// The Anti-alias tick as a press on it would leave it, for the check: a tick's own write goes through the
    /// same Set every other control's does, so this drives the whole path rather than reaching past it.
    /// </summary>
    internal void PressAntialias(bool on) => _antialias.IsChecked = on;

    /// <summary>Chooses the mask color through the same event as the options-bar menu.</summary>
    internal void ChooseMaskPaint(bool reveal) => _maskPaint.SelectedIndex = reveal ? 1 : 0;

    internal bool MaskPaintReveals => _maskPaint.SelectedIndex == 1;

    /// <summary>The names of the rows on show, in the order they were built.</summary>
    internal IEnumerable<string> Showing => _named.Where(entry => entry.Value[0].IsVisible).Select(entry => entry.Key);

    /// <summary>Every row's value read back off its own control, which is what the bar is showing.</summary>
    private void Refresh()
    {
        _size.Content = L10n.F("Size {0:0}", _options.Brush.Diameter);
        _hardness.Content = L10n.F("Hardness {0:0}%", _options.Brush.Hardness * 100);
        _opacity.Content = L10n.F("Opacity {0:0}%", _options.Brush.Opacity * 100);
        _blurRadius.Content = L10n.F("Radius {0:0.#}", _options.Brush.BlurRadius);
        _tolerance.Content = L10n.F("Tolerance {0}", _options.Wand.Tolerance);
        _sampleSize.Content = L10n.F("Sample {0}", _options.Wand.Radius);
        _corner.Content = L10n.F("Radius {0:0}", _options.ShapeCornerRadius);
        _lineWidth.Content = L10n.F("Width {0:0}", _options.ShapeLineWidth);
        _fill.Show(_options.Brush.Red, _options.Brush.Green, _options.Brush.Blue);
        _gradientFill.Show(_options.GradientBackground.Red, _options.GradientBackground.Green,
            _options.GradientBackground.Blue);
        _brushMode.SelectedIndex = _options.Erase ? 1 : 0;
        _maskPaint.SelectedIndex = _options.Erase ? 0 : 1;
        _healMode.SelectedIndex = (int)_options.Brush.Healing;
        _aligned.IsChecked = _options.Brush.CloneAligned;
        _cloneAll.SelectedIndex = _options.Brush.CloneAllLayers ? 1 : 0;
        _contiguous.IsChecked = _options.Wand.Contiguous;
        _antialias.IsChecked = _options.SelectionAntialiased;
        _sampleRing.IsChecked = _options.ShowsSampleRing;
        _wandAll.SelectedIndex = _options.WandAllLayers ? 1 : 0;
        _shapeKind.SelectedIndex = (int)_options.Shape;
        _gradientKind.SelectedIndex = (int)_options.Gradient;
        _gradientTo.SelectedIndex = _options.GradientToBackground ? 1 : 0;
        _gradientReversed.IsChecked = _options.GradientReversed;
    }

    /// <summary>Whether the marquee is drawing an ellipse, which is the tool in hand rather than a setting.</summary>
    private bool _marqueeEllipse;

    /// <summary>The bar told which shape the marquee is drawing, since that is the tool and not a setting.</summary>
    public void ShowMarquee(bool ellipse)
    {
        _loading = true;
        try
        {
            _marqueeEllipse = ellipse;
            _marqueeShape.SelectedIndex = ellipse ? 1 : 0;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The marquee's shape was picked from the bar, which is asking for the tool that draws it.</summary>
    public event Action<bool>? MarqueeShapeChosen;

    /// <summary>The lasso's kind was picked from the bar, which is the same as picking the tool.</summary>
    public event Action<bool>? LassoKindChosen;


    private readonly Button _size = new();
    private readonly Button _hardness = new();
    private readonly Button _opacity = new();
    private readonly Button _blurRadius = new();
    private readonly Button _tolerance = new();
    private readonly Button _sampleSize = new();
    private readonly Button _corner = new();
    private readonly Button _lineWidth = new();
    private readonly Swatch _fill = new();
    private readonly Swatch _gradientFill = new();
    private readonly ComboBox _brushMode = new();
    private readonly ComboBox _maskPaint = new();
    private readonly ComboBox _healMode = new();
    private readonly CheckBox _aligned = new() { Content = L10n.T("Aligned") };
    private readonly ComboBox _cloneAll = new();
    private readonly ComboBox _marqueeShape = new();
    private readonly ComboBox _lassoKind = new();
    private readonly CheckBox _contiguous = new() { Content = L10n.T("Contiguous") };
    private readonly CheckBox _antialias = new() { Content = L10n.T("Anti-alias") };
    private readonly CheckBox _sampleRing = new() { Content = L10n.T("Sample Ring") };
    private readonly ComboBox _wandAll = new();
    private readonly ComboBox _shapeKind = new();
    private readonly ComboBox _gradientKind = new();
    private readonly ComboBox _gradientTo = new();
    private readonly CheckBox _gradientReversed = new() { Content = L10n.T("Reverse") };
    private readonly ComboBox _cropRatio = new() { Width = 150 };
    private readonly Button _cropApply = new() { Content = L10n.T("Apply Crop") };
    private readonly Button _cropCancel = new() { Content = L10n.T("Cancel") };
    private readonly Button _flipH = new() { Content = L10n.T("Flip H") };
    private readonly Button _flipV = new() { Content = L10n.T("Flip V") };
    private readonly Button _editText = new() { Content = L10n.T("Edit Text…") };

    /// <summary>What each tool's strip is called, which is the Mac's own title.</summary>
    private static readonly Dictionary<Tool, string> Names = new()
    {
        [Tool.Pan] = "Pan",
        [Tool.Move] = "Transform",
        [Tool.Marquee] = "Marquee",
        [Tool.Ellipse] = "Elliptical marquee",
        [Tool.Lasso] = "Lasso",
        [Tool.Polygon] = "Polygonal lasso",
        [Tool.Wand] = "Magic wand",
        [Tool.Brush] = "Brush",
        [Tool.Clone] = "Clone stamp",
        [Tool.Blur] = "Blur brush",
        [Tool.Liquify] = "Liquify brush",
        [Tool.Smudge] = "Smudge brush",
        [Tool.Heal] = "Spot healing",
        [Tool.Eyedropper] = "Eyedropper",
        [Tool.Type] = "Type",
        [Tool.Crop] = "Crop",
        [Tool.Shape] = "Shape",
        [Tool.Gradient] = "Gradient",
    };

    private void Build()
    {
        // The brush's own amounts, each a button showing its value that opens the window's own prompt.
        foreach (var (setting, button) in new (BrushSetting, Button)[]
                 {
                     (BrushSetting.Size, _size), (BrushSetting.Hardness, _hardness), (BrushSetting.Opacity, _opacity),
                     (BrushSetting.Radius, _blurRadius),
                 })
        {
            var which = setting;
            button.Click += (_, _) => BrushSettingAsked?.Invoke(which);
        }
        foreach (var (setting, button) in new (WandSetting, Button)[]
                 {
                     (WandSetting.Tolerance, _tolerance), (WandSetting.SampleSize, _sampleSize),
                 })
        {
            var which = setting;
            button.Click += (_, _) => WandSettingAsked?.Invoke(which);
        }
        _corner.Click += (_, _) => ShapeSettingAsked?.Invoke(ShapeSetting.CornerRadius);
        _lineWidth.Click += (_, _) => ShapeSettingAsked?.Invoke(ShapeSetting.LineWidth);
        _fill.Click += (_, _) => ColourAsked?.Invoke(true);
        _gradientFill.Click += (_, _) => ColourAsked?.Invoke(false);

        _brushMode.ItemsSource = new[] { "Paint", "Erase" }.Select(L10n.T).ToArray();
        _brushMode.SelectedIndex = 0;
        _brushMode.SelectionChanged += (_, _) => Set(ref _options.Erase, _brushMode.SelectedIndex == 1);
        _maskPaint.ItemsSource = new[] { "Paint Black · Hide", "Paint White · Reveal" }.Select(L10n.T).ToArray();
        _maskPaint.SelectedIndex = 0;
        _maskPaint.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _options.PaintOnMask = true;
            Set(ref _options.Erase, _maskPaint.SelectedIndex == 0);
        };
        _healMode.ItemsSource = new[] { "Content-Aware", "Create Texture", "Proximity Match" }.Select(L10n.T).ToArray();
        _healMode.SelectedIndex = 0;
        _healMode.SelectionChanged += (_, _) =>
        {
            var healed = _options.Brush with { Healing = (HealingMode)Math.Max(0, _healMode.SelectedIndex) };
            Set(ref _options.Brush, healed);
        };
        _aligned.IsCheckedChanged += (_, _) =>
        {
            var brush = _options.Brush with { CloneAligned = _aligned.IsChecked == true };
            Set(ref _options.Brush, brush);
        };
        _antialias.IsChecked = _options.SelectionAntialiased;
        _antialias.IsCheckedChanged += (_, _) =>
            Set(ref _options.SelectionAntialiased, _antialias.IsChecked == true);
        _sampleRing.IsChecked = _options.ShowsSampleRing;
        _sampleRing.IsCheckedChanged += (_, _) =>
            Set(ref _options.ShowsSampleRing, _sampleRing.IsChecked == true);
        _cloneAll.ItemsSource = new[] { "Sample: This Layer", "Sample: All Layers" }.Select(L10n.T).ToArray();
        _cloneAll.SelectedIndex = 0;
        _cloneAll.SelectionChanged += (_, _) =>
        {
            var brush = _options.Brush with { CloneAllLayers = _cloneAll.SelectedIndex == 1 };
            Set(ref _options.Brush, brush);
        };

        _marqueeShape.ItemsSource = new[] { "Rectangle", "Ellipse" }.Select(L10n.T).ToArray();
        _marqueeShape.SelectedIndex = 0;
        _marqueeShape.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _marqueeEllipse = _marqueeShape.SelectedIndex == 1;
            MarqueeShapeChosen?.Invoke(_marqueeEllipse);
        };
        _lassoKind.ItemsSource = new[] { "Freehand", "Polygonal" }.Select(L10n.T).ToArray();
        _lassoKind.SelectedIndex = 0;
        _lassoKind.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            LassoKindChosen?.Invoke(_lassoKind.SelectedIndex == 1);
        };
        _contiguous.IsCheckedChanged += (_, _) =>
        {
            var wand = _options.Wand with { Contiguous = _contiguous.IsChecked == true };
            Set(ref _options.Wand, wand);
        };
        _wandAll.ItemsSource = new[] { "Sample: This Layer", "Sample: All Layers" }.Select(L10n.T).ToArray();
        _wandAll.SelectedIndex = 0;
        _wandAll.SelectionChanged += (_, _) => Set(ref _options.WandAllLayers, _wandAll.SelectedIndex == 1);

        _shapeKind.ItemsSource = new[] { "Rectangle", "Ellipse", "Line" }.Select(L10n.T).ToArray();
        _shapeKind.SelectedIndex = 0;
        _shapeKind.SelectionChanged += (_, _) =>
        {
            var shape = (ShapeKind)Math.Max(0, _shapeKind.SelectedIndex);
            Set(ref _options.Shape, shape);
            // The corner radius belongs to a rectangle and the width to a line, so which of the two shows
            // follows the kind that was just picked.
            On("corner", shape != ShapeKind.Line);
            On("linewidth", shape == ShapeKind.Line);
        };
        _gradientKind.ItemsSource = new[] { "Linear", "Radial", "Angle", "Reflected", "Diamond" }.Select(L10n.T).ToArray();
        _gradientKind.SelectedIndex = 0;
        _gradientKind.SelectionChanged += (_, _) => Set(ref _options.Gradient, (GradientShape)Math.Max(0, _gradientKind.SelectedIndex));
        _gradientTo.ItemsSource = new[] { "To nothing", "To the background color" }.Select(L10n.T).ToArray();
        _gradientTo.SelectedIndex = 0;
        _gradientTo.SelectionChanged += (_, _) => Set(ref _options.GradientToBackground, _gradientTo.SelectedIndex == 1);
        _gradientReversed.IsCheckedChanged += (_, _) => Set(ref _options.GradientReversed, _gradientReversed.IsChecked == true);

        _cropRatio.SelectionChanged += (_, _) => CropRatioChosen?.Invoke(_cropRatio.SelectedIndex);
        _cropApply.Click += (_, _) => CropApplied?.Invoke();
        _cropCancel.Click += (_, _) => CropCancelled?.Invoke();
        _flipH.Click += (_, _) => FlipAsked?.Invoke(true);
        _flipV.Click += (_, _) => FlipAsked?.Invoke(false);
        _editText.Click += (_, _) => TextAsked?.Invoke();

        Cell("brush", _size);
        Cell("brush", _hardness);
        Cell("brush", _opacity);
        Cell("brush", _fill);
        Cell("mode", _brushMode);
        Cell("mask", _maskPaint);
        Cell("heal", _healMode);
        // The Blur brush's own Radius, which the Mac's brush controls show for that tool alone.
        Cell("blur", _blurRadius);
        Cell("clone", _aligned);
        Cell("clone", _cloneAll);
        Cell("marqueeShape", _marqueeShape);
        Cell("lasso", _lassoKind);
        Cell("lasso", _antialias);
        // The eyedropper's own row, which the Mac keeps in the picker's controls: the ring is the only thing that
        // tool can be told.
        Cell("eye", _sampleRing);
        Cell("wand", _tolerance);
        Cell("wand", _sampleSize);
        Cell("wand", _contiguous);
        Cell("wand", _wandAll);
        Cell("gradient", _gradientKind);
        Cell("gradient", _gradientTo);
        Cell("gradient", _gradientFill);
        Cell("gradient", _gradientReversed);
        Cell("shape", _shapeKind);
        Cell("corner", _corner);
        Cell("linewidth", _lineWidth);
        Cell("crop", _cropRatio);
        Cell("crop", _cropApply);
        Cell("crop", _cropCancel);
        Cell("transform", _flipH);
        Cell("transform", _flipV);
        Cell("type", _editText);
        Cell("zoom", _zoom);
    }


    /// <summary>The crop ratios the window offers, so the bar's own list is the same list.</summary>
    public void ShowCropRatios(IReadOnlyList<string> ratios)
    {
        _cropRatio.ItemsSource = ratios.Select(L10n.T).ToArray();
        _cropRatio.SelectedIndex = 0;
    }

    /// <summary>Which ratio the crop frame is on, so the bar follows the menu and the frame follows the bar.</summary>
    public void ShowCropRatio(int index) => _cropRatio.SelectedIndex = index;

    /// <summary>One option changed by the bar: the window is told, and the values are read back.</summary>
    private void Set<T>(ref T field, T value)
    {
        // While the bar is filling itself in, a widget written by Show reads back as a change. It is not one:
        // the window is the one that asked for the filling, and answering it would be a loop.
        if (_loading) return;
        field = value;
        _loading = true;
        try { Refresh(); }
        finally { _loading = false; }
        Changed?.Invoke();
    }

    /// <summary>Whether the bar is filling itself in, which is when its own writes are not changes.</summary>
    private bool _loading;

    /// <summary>One option's place in the strip, under its tool's name, kept so Show can hide it.</summary>
    private void Cell(string name, Control control)
    {
        _cells.Children.Add(control);
        if (!_named.TryGetValue(name, out var cells))
        {
            cells = [];
            _named[name] = cells;
        }
        cells.Add(control);
    }

    /// <summary>Whether a row and every control in it are on show.</summary>
    private void On(string name, bool shown)
    {
        if (!_named.TryGetValue(name, out var cells)) return;
        foreach (var cell in cells) cell.IsVisible = shown;
    }

    /// <summary>A clickable colour of the bar's own, which the window finds out about rather than owns.</summary>
    private sealed class Swatch : Button
    {
        public Swatch()
        {
            Width = 52;
            Height = 22;
            Padding = new Thickness(0);
            BorderThickness = new Thickness(1);
            BorderBrush = new SolidColorBrush(Colors.White, 0.35);
        }

        public void Show(double red, double green, double blue) => Background = new SolidColorBrush(
            Color.FromRgb((byte)Math.Clamp(Math.Round(red * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(green * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(blue * 255), 0, 255)));
    }
}
