using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;
using SkiaSharp;

namespace Compositor.Desktop;

/// <summary>
/// The Camera Raw Filter's panel: every group of settings as sliders, and Apply to write them into the layer's
/// pixels. It is not a window of its own — the editor window docks it at its right edge, because the canvas has
/// to stay live while it is up: the amounts are previewed on the canvas as they move, and the Mac build draws
/// upright guides and picks colours on that same canvas, which a modal dialog would not allow.
/// </summary>
internal sealed class CameraRawPanel
{
    private readonly List<(Slider Slider, Action<CameraRawSettings, double> Set, TextBlock Readout, string Format)> _rows = [];
    /// <summary>The panel's amounts by the label their row carries, which is how the self check moves one.</summary>
    private readonly Dictionary<string, Slider> _labelled = [];
    private readonly ComboBox _glowStyle = new();
    private readonly ComboBox _vignetteStyle = new();
    private readonly ComboBox _geometryProjection = new();
    private readonly ComboBox _curveChannel = new();
    private readonly ListBox _points = new() { Height = 96 };
    private readonly List<CameraRawPointColor> _pointList = [];
    private CameraRawPointColor? _point;
    private bool _showingPoints;
    private CurveEditor? _curve;
    private readonly List<double> _fallbacks = [];
    private readonly int _pointRow;
    private bool _loading;

    /// <summary>
    /// Asks for the picture to be shown with the amounts as they stand, which is called on every change. The
    /// panel does not wait for it: a slider being dragged should not stop moving while a filter runs.
    /// </summary>
    public Action<CameraRawSettings, bool, bool, bool>? Preview { get; set; }

    /// <summary>The amounts to write into the layer, once Apply is pressed.</summary>
    public event Action<CameraRawSettings>? Applied;

    /// <summary>Nothing is to be written; the caller puts the picture back as it was.</summary>
    public event Action? Cancelled;

    /// <summary>What is shown over the picture while the amounts are moved: clipped shadows in blue, clipped
    /// highlights in red, and the sharpening mask. None of it is ever applied on Apply.</summary>
    private readonly CheckBox _shadowClip = new() { Content = L10n.T("Clipped shadows") };
    private readonly CheckBox _highlightClip = new() { Content = L10n.T("Clipped highlights") };
    private readonly CheckBox _sharpenMaskView = new() { Content = L10n.T("Sharpening mask") };

    /// <summary>The scope above the groups, and the readout of the pixel the pointer is over.</summary>
    private readonly ScopesView _scopes = new() { Height = 110 };
    private readonly TextBlock _readout = new()
    {
        Text = EmptyReadout,
        FontSize = 11,
        Foreground = Skin.SecondaryBrush,
    };
    private const string EmptyReadout = "R —   G —   B —";

    /// <summary>Whether the drawn lines are read, the lines themselves, and whether more are being drawn.</summary>
    private CameraRawUprightMode _upright;
    private readonly List<CameraRawGeometryGuide> _guides = [];
    private bool _drawing;
    private readonly ComboBox _uprightChoice = new() { Width = 160 };
    private readonly Button _drawGuides = new() { Content = L10n.T("Draw Guides") };
    private readonly TextBlock _guideNote = new()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        FontSize = 11,
        Opacity = 0.75,
    };

    /// <summary>
    /// What the canvas is to show has changed — a line was drawn or cleared, or the panel has started or
    /// stopped asking for one — so the window arms it or lets it go and hands it the lines it has.
    /// </summary>
    public event Action? CanvasChanged;

    /// <summary>The lines drawn on the picture, in the layer's own fractions, as the panel has them.</summary>
    public IReadOnlyList<CameraRawGeometryGuide> Guides => _guides;

    /// <summary>Whether the panel is asking for a line to be drawn on the picture right now.</summary>
    public bool DrawingGuides => _drawing;

    /// <summary>
    /// Adds a line drawn on the picture, given in the layer's own fractions. The first line levels itself and a
    /// steep second one asks for a keystone, so a line is enough to change the picture: the preview is asked
    /// for again, and the panel's Upright choice follows to Guided.
    /// </summary>
    public void AddGuide(CameraRawGeometryGuide guide)
    {
        if (!guide.IsUsable) return;
        _guides.Add(guide);
        _upright = CameraRawUprightMode.Guided;
        _uprightChoice.SelectedIndex = (int)_upright;
        RefreshGuides();
        CanvasChanged?.Invoke();
        RefreshPreview();
    }

    /// <summary>The lines put away, which leaves the slider amounts alone.</summary>
    public void ClearGuides()
    {
        _guides.Clear();
        RefreshGuides();
        CanvasChanged?.Invoke();
        RefreshPreview();
    }

    /// <summary>Stops asking for lines to be drawn, which is what Apply and Cancel do too.</summary>
    public void StopDrawingGuides() => SetDrawing(false);

    private void SetDrawing(bool drawing)
    {
        if (_drawing == drawing) return;
        _drawing = drawing;
        _drawGuides.Content = L10n.T(drawing ? "Stop drawing" : "Draw Guides");
        RefreshGuides();
        CanvasChanged?.Invoke();
    }

    /// <summary>The row's own words: how many lines there are and what to do with the next one.</summary>
    private void RefreshGuides()
    {
        _guideNote.Text = _guides.Count == 0
            ? L10n.T("Drag on the picture to draw a line that should be level or upright. One line turns the picture; a second, steeper one adds a keystone.")
            : L10n.F(_drawing
                ? "{0} guide(s) drawn. Drag again to add another."
                : "{0} guide(s) drawn. Draw Guides adds another.", _guides.Count);
    }

    /// <summary>The body, for the window to dock at its right edge.</summary>
    public Control View { get; }

    /// <summary>How many amounts the panel is made of. A bitmap does not lay a scroll view's content out, so
    /// the drawing cannot show the rows; the self check counts them here instead.</summary>
    internal int AmountCount => _rows.Count;

    /// <summary>Shows what is being asked for now, overlays and all.</summary>
    private void RefreshPreview()
    {
        if (!_loading) Preview?.Invoke(Current(), _shadowClip.IsChecked == true,
            _highlightClip.IsChecked == true, _sharpenMaskView.IsChecked == true);
    }

    /// <summary>Asks for a picture of the amounts as they stand, without any of them having moved.</summary>
    public void Show() => RefreshPreview();

    /// <summary>Shows the scope the last preview counted, which is the picture the canvas is drawing.</summary>
    public void ShowScope(CameraRawScope? scope) => _scopes.Scope = scope;

    /// <summary>
    /// Shows the colour of the pixel the pointer is over, as the readout under the scope. Nothing is passed
    /// when the pointer is off the picture, and the readout goes back to its dashes.
    /// </summary>
    public void ShowReadout((int Red, int Green, int Blue)? pixel) => _readout.Text = pixel is { } value
        ? $"R {value.Red}   G {value.Green}   B {value.Blue}"
        : EmptyReadout;

    /// <summary>
    /// Which of the two clipping views is on, so the caller knows what the triangles and the checkboxes agree
    /// on. They are one switch each, whichever of the two is used to move them.
    /// </summary>
    public (bool Shadows, bool Highlights) Clipping =>
        (_shadowClip.IsChecked == true, _highlightClip.IsChecked == true);

    /// <summary>The two triangles lit as their checkboxes have them, which is what a tick of one changes.</summary>
    private void RefreshClipping()
    {
        _scopes.ShowsShadows = _shadowClip.IsChecked == true;
        _scopes.ShowsHighlights = _highlightClip.IsChecked == true;
    }

    /// <summary>Presses one of the scope's clipping triangles, and the switch it stands for goes with it.</summary>
    internal void PressClippingTriangle(bool shadows) => _scopes.Press(shadows);

    /// <summary>Asks for the other of the histogram and the vectorscope, as a right-click on the scope does.</summary>
    internal void SwapScope() => _scopes.Swap();

    /// <summary>
    /// One of the panel's amounts, for the checks: a slider is the one control a pointer can be aimed at, and the
    /// panel's amounts are inside a scroll view — the place this port's notes warn that a control can be drawn and
    /// still not reachable, so a click on one is the only thing that proves it.
    /// </summary>
    internal Slider? Amount(string label) => _labelled.TryGetValue(label, out var slider) ? slider : null;

    /// <summary>Presses the Draw Guides button, which asks the canvas to take lines or lets it go.</summary>
    internal void PressDrawGuides() => SetDrawing(!_drawing);

    /// <summary>Whether the density is being drawn rather than the three ribbons.</summary>
    internal bool ShowingVectorscope => _scopes.Vectorscope;

    /// <summary>Writes the amounts as they stand into the layer. The buttons go through here, and so does the
    /// self check, so what it drives is the path a press takes.</summary>
    public void Apply()
    {
        SetDrawing(false);
        Applied?.Invoke(Current());
    }

    /// <summary>Asks for nothing to be written. The buttons go through here, and so does the self check.</summary>
    public void Cancel()
    {
        SetDrawing(false);
        Cancelled?.Invoke();
    }

    /// <summary>Restores the model defaults and clears the point colors, overlays and drawn lines.</summary>
    public void Reset()
    {
        _loading = true;
        try
        {
            for (var i = 0; i < _rows.Count; i++) _rows[i].Slider.Value = _fallbacks[i];
            _glowStyle.SelectedIndex = 0;
            _vignetteStyle.SelectedIndex = 0;
            _geometryProjection.SelectedIndex = 0;
            _uprightChoice.SelectedIndex = 0;
            _curveChannel.SelectedIndex = 0;
            if (_curve is not null) _curve.Curves = new Compositor.Core.Format.CurvesSettings();
            _point = null;
            _pointList.Clear();
            Labelled();
            _shadowClip.IsChecked = _highlightClip.IsChecked = _sharpenMaskView.IsChecked = false;
            StopDrawingGuides();
            ClearGuides();
        }
        finally { _loading = false; }
        RefreshPreview();
    }

    /// <summary>
    /// Moves one of the panel's own amounts, by the label its row carries. A slider's value is a property and
    /// not a template, so the panel can be driven without a pointer: the change runs the same handler a drag
    /// would, preview and all. The self check is the only caller.
    /// </summary>
    internal void Move(string label, double value)
    {
        if (!_labelled.TryGetValue(label, out var slider))
        {
            throw new InvalidOperationException($"the panel has no amount called {label}");
        }
        slider.Value = value;
    }

    /// <summary>What one of the panel's amounts is set to, which the self check reads to see where the panel
    /// opened: the same slider <see cref="Amount"/> hands back. The self check is the only caller.</summary>
    internal double SetTo(string label) => Amount(label) is { } slider
        ? slider.Value
        : throw new InvalidOperationException($"the panel has no amount called {label}");

    /// <summary>The amounts as the panel has them, for a preview of what they would do.</summary>
    public CameraRawSettings Current()
    {
        var settings = new CameraRawSettings
        {
            GlowStyle = Math.Max(0, _glowStyle.SelectedIndex),
            VignetteStyle = Math.Max(0, _vignetteStyle.SelectedIndex),
            Geometry = new CameraRawGeometrySettings
            {
                Projection = (GeometryProjection)Math.Max(0, _geometryProjection.SelectedIndex),
                Upright = _upright,
            },
            Curve = _curve is { } curve ? curve.Curves : new Compositor.Core.Format.CurvesSettings(),
            // The mixer's places are written into by the rows, so the settings the rows are handed have all
            // twenty-four of them whatever the layer's panel started from.
            Mixer = new double[24],
        };
        foreach (var (slider, set, _, _) in _rows) set(settings, slider.Value);
        settings.Points = Points();
        settings.Geometry.Guides.AddRange(_guides);
        return settings;
    }

    internal CameraRawPanel(CameraRawSettings start, SKColor brush)
    {
        brush.ToHsl(out var brushHue, out var brushSaturation, out _);
        _brushHue = brushHue;
        _brushSaturation = brushSaturation;
        var defaults = new CameraRawSettings();
        var groups = new StackPanel { Margin = new Thickness(16), Spacing = 4 };

        // The overlays come first: they are shown over whatever the groups below are doing, and none of them
        // is written into the layer when Apply is pressed. The clipping triangles in the scope are the same two
        // switches, so one setter drives both and neither can be lit while the other is not.
        var overlays = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var box in new[] { _shadowClip, _highlightClip, _sharpenMaskView })
        {
            box.IsCheckedChanged += (_, _) => { RefreshClipping(); RefreshPreview(); };
            box.Margin = new Thickness(0, 0, 12, 0);
            overlays.Children.Add(box);
        }
        _scopes.ClippingToggled += shadows =>
        {
            var box = shadows ? _shadowClip : _highlightClip;
            box.IsChecked = box.IsChecked != true;
        };
        _scopes.ModeSwapped += () => _scopes.Vectorscope = !_scopes.Vectorscope;
        groups.Children.Add(overlays);

        groups.Children.Add(Heading("Light"));
        Add(groups, "Exposure, stops", -5, 5, start.Exposure, defaults.Exposure, (s, v) => s.Exposure = v, "0.00");
        Add(groups, "Contrast", -100, 100, start.Contrast, defaults.Contrast, (s, v) => s.Contrast = v);
        Add(groups, "Highlights", -100, 100, start.Highlights, defaults.Highlights, (s, v) => s.Highlights = v);
        Add(groups, "Shadows", -100, 100, start.Shadows, defaults.Shadows, (s, v) => s.Shadows = v);
        Add(groups, "Whites", -100, 100, start.Whites, defaults.Whites, (s, v) => s.Whites = v);
        Add(groups, "Blacks", -100, 100, start.Blacks, defaults.Blacks, (s, v) => s.Blacks = v);

        groups.Children.Add(Heading("Color"));
        Add(groups, "Temperature, cool to warm", -100, 100, start.Temperature, defaults.Temperature, (s, v) => s.Temperature = v);
        Add(groups, "Tint, green to magenta", -100, 100, start.Tint, defaults.Tint, (s, v) => s.Tint = v);
        Add(groups, "Vibrance", -100, 100, start.Vibrance, defaults.Vibrance, (s, v) => s.Vibrance = v);
        Add(groups, "Saturation", -100, 100, start.Saturation, defaults.Saturation, (s, v) => s.Saturation = v);

        groups.Children.Add(Heading("Effects"));
        Add(groups, "Texture", -100, 100, start.Texture, defaults.Texture, (s, v) => s.Texture = v);
        Add(groups, "Clarity", -100, 100, start.Clarity, defaults.Clarity, (s, v) => s.Clarity = v);
        Add(groups, "Dehaze", -100, 100, start.Dehaze, defaults.Dehaze, (s, v) => s.Dehaze = v);
        Add(groups, "Glow", 0, 100, start.Glow, defaults.Glow, (s, v) => s.Glow = v);
        groups.Children.Add(Choice("Glow style", _glowStyle, ["Diffusion", "Bloom", "Halation"]));
        Add(groups, "Glow range", 0, 100, start.GlowRange, defaults.GlowRange, (s, v) => s.GlowRange = v);
        Add(groups, "Glow spread", 0, 100, start.GlowSpread, defaults.GlowSpread, (s, v) => s.GlowSpread = v);
        Add(groups, "Glow warmth", -100, 100, start.GlowWarmth, defaults.GlowWarmth, (s, v) => s.GlowWarmth = v);
        Add(groups, "Vignette amount", -100, 100, start.VignetteAmount, defaults.VignetteAmount, (s, v) => s.VignetteAmount = v);
        groups.Children.Add(Choice("Vignette style", _vignetteStyle,
            ["Highlight priority", "Color priority", "Paint overlay"]));
        Add(groups, "Vignette midpoint", 0, 100, start.VignetteMidpoint, defaults.VignetteMidpoint, (s, v) => s.VignetteMidpoint = v);
        Add(groups, "Vignette roundness", -100, 100, start.VignetteRoundness, defaults.VignetteRoundness, (s, v) => s.VignetteRoundness = v);
        Add(groups, "Vignette feather", 0, 100, start.VignetteFeather, defaults.VignetteFeather, (s, v) => s.VignetteFeather = v);
        Add(groups, "Vignette highlights", -100, 100, start.VignetteHighlights, defaults.VignetteHighlights, (s, v) => s.VignetteHighlights = v);
        Add(groups, "Grain amount", 0, 100, start.GrainAmount, defaults.GrainAmount, (s, v) => s.GrainAmount = v);
        Add(groups, "Grain size", 0, 100, start.GrainSize, defaults.GrainSize, (s, v) => s.GrainSize = v);
        Add(groups, "Grain roughness", 0, 100, start.GrainRoughness, defaults.GrainRoughness, (s, v) => s.GrainRoughness = v);

        groups.Children.Add(Heading("Detail"));
        Add(groups, "Sharpen amount", 0, 150, start.SharpenAmount, defaults.SharpenAmount, (s, v) => s.SharpenAmount = v);
        Add(groups, "Sharpen radius", 0.5, 100, start.SharpenRadius, defaults.SharpenRadius, (s, v) => s.SharpenRadius = v, "0.0");
        Add(groups, "Sharpen detail", 0, 100, start.SharpenDetail, defaults.SharpenDetail, (s, v) => s.SharpenDetail = v);
        Add(groups, "Sharpen masking", 0, 100, start.SharpenMasking, defaults.SharpenMasking, (s, v) => s.SharpenMasking = v);
        Add(groups, "Noise luminance", 0, 100, start.NoiseLuminance, defaults.NoiseLuminance, (s, v) => s.NoiseLuminance = v);
        Add(groups, "Noise luminance detail", 0, 100, start.NoiseLuminanceDetail, defaults.NoiseLuminanceDetail, (s, v) => s.NoiseLuminanceDetail = v);
        Add(groups, "Noise luminance contrast", 0, 100, start.NoiseLuminanceContrast, defaults.NoiseLuminanceContrast, (s, v) => s.NoiseLuminanceContrast = v);
        Add(groups, "Noise color", 0, 100, start.NoiseColor, defaults.NoiseColor, (s, v) => s.NoiseColor = v);
        Add(groups, "Noise color detail", 0, 100, start.NoiseColorDetail, defaults.NoiseColorDetail, (s, v) => s.NoiseColorDetail = v);
        Add(groups, "Noise color smoothness", 0, 100, start.NoiseColorSmoothness, defaults.NoiseColorSmoothness, (s, v) => s.NoiseColorSmoothness = v);

        groups.Children.Add(Heading("Optics"));
        Add(groups, "Remove chromatic aberration", 0, 1, start.RemoveChromaticAberration ? 1 : 0, defaults.RemoveChromaticAberration ? 1 : 0, (s, v) => s.RemoveChromaticAberration = v > 0.5, "0");
        Add(groups, "Lens profile", 0, 1, start.EnableLensProfile ? 1 : 0, defaults.EnableLensProfile ? 1 : 0, (s, v) => s.EnableLensProfile = v > 0.5, "0");
        Add(groups, "Profile distortion", 0, 100, start.ProfileDistortion, defaults.ProfileDistortion, (s, v) => s.ProfileDistortion = v);
        Add(groups, "Profile vignetting", 0, 100, start.ProfileVignetting, defaults.ProfileVignetting, (s, v) => s.ProfileVignetting = v);
        Add(groups, "Distortion", -100, 100, start.Distortion, defaults.Distortion, (s, v) => s.Distortion = v);
        Add(groups, "Purple amount", 0, 100, start.PurpleAmount, defaults.PurpleAmount, (s, v) => s.PurpleAmount = v);
        Add(groups, "Purple hue low", 0, 360, start.PurpleHueLow, defaults.PurpleHueLow, (s, v) => s.PurpleHueLow = v);
        Add(groups, "Purple hue high", 0, 360, start.PurpleHueHigh, defaults.PurpleHueHigh, (s, v) => s.PurpleHueHigh = v);
        Add(groups, "Green amount", 0, 100, start.GreenAmount, defaults.GreenAmount, (s, v) => s.GreenAmount = v);
        Add(groups, "Green hue low", 0, 360, start.GreenHueLow, defaults.GreenHueLow, (s, v) => s.GreenHueLow = v);
        Add(groups, "Green hue high", 0, 360, start.GreenHueHigh, defaults.GreenHueHigh, (s, v) => s.GreenHueHigh = v);
        Add(groups, "Lens vignette", -100, 100, start.OpticsVignetteAmount, defaults.OpticsVignetteAmount, (s, v) => s.OpticsVignetteAmount = v);
        Add(groups, "Lens vignette midpoint", 0, 100, start.OpticsVignetteMidpoint, defaults.OpticsVignetteMidpoint, (s, v) => s.OpticsVignetteMidpoint = v);

        groups.Children.Add(Heading("Geometry"));
        // Guided upright is the Mac's own: a line drawn on the picture that should be level or upright. It sits
        // with the amounts it is added to, because that is what it is — the lines ask for a turn and, when one
        // of them is steep, a keystone, and the sliders add to that.
        _upright = start.Geometry.Upright;
        _uprightChoice.ItemsSource = new[] { "Off", "Guided" }.Select(L10n.T).ToArray();
        _uprightChoice.SelectedIndex = (int)_upright;
        _uprightChoice.SelectionChanged += (_, _) =>
        {
            _upright = (CameraRawUprightMode)Math.Max(0, _uprightChoice.SelectedIndex);
            RefreshPreview();
        };
        _drawGuides.Click += (_, _) => SetDrawing(!_drawing);
        var clear = new Button { Content = L10n.T("Clear guides") };
        clear.Click += (_, _) => ClearGuides();
        groups.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T("Upright"), Width = 145, VerticalAlignment = VerticalAlignment.Center },
                _uprightChoice,
            },
        });
        groups.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _drawGuides, clear },
        });
        groups.Children.Add(_guideNote);
        RefreshGuides();
        groups.Children.Add(Choice("Projection", _geometryProjection, ["Perspective", "Rectilinear"]));
        Add(groups, "Vertical", -100, 100, start.Geometry.Vertical, defaults.Geometry.Vertical, (s, v) => s.Geometry.Vertical = v);
        Add(groups, "Horizontal", -100, 100, start.Geometry.Horizontal, defaults.Geometry.Horizontal, (s, v) => s.Geometry.Horizontal = v);
        Add(groups, "Rotate", -45, 45, start.Geometry.Rotate, defaults.Geometry.Rotate, (s, v) => s.Geometry.Rotate = v);
        Add(groups, "Aspect", -100, 100, start.Geometry.Aspect, defaults.Geometry.Aspect, (s, v) => s.Geometry.Aspect = v);
        Add(groups, "Scale", -100, 100, start.Geometry.Scale, defaults.Geometry.Scale, (s, v) => s.Geometry.Scale = v);
        Add(groups, "Offset X", -100, 100, start.Geometry.OffsetX, defaults.Geometry.OffsetX, (s, v) => s.Geometry.OffsetX = v);
        Add(groups, "Offset Y", -100, 100, start.Geometry.OffsetY, defaults.Geometry.OffsetY, (s, v) => s.Geometry.OffsetY = v);
        Add(groups, "Constrain crop", 0, 1, start.Geometry.ConstrainCrop ? 1 : 0, defaults.Geometry.ConstrainCrop ? 1 : 0,
            (s, v) => s.Geometry.ConstrainCrop = v > 0.5, "0");

        groups.Children.Add(Heading("Calibration"));
        Add(groups, "Process version", 1, 6, start.ProcessVersion, defaults.ProcessVersion, (s, v) => s.ProcessVersion = (int)Math.Round(v), "0");
        Add(groups, "Shadow tint", -100, 100, start.ShadowTint, defaults.ShadowTint, (s, v) => s.ShadowTint = v);
        Add(groups, "Red hue", -100, 100, start.RedHue, defaults.RedHue, (s, v) => s.RedHue = v);
        Add(groups, "Red saturation", -100, 100, start.RedSaturation, defaults.RedSaturation, (s, v) => s.RedSaturation = v);
        Add(groups, "Green hue", -100, 100, start.GreenHue, defaults.GreenHue, (s, v) => s.GreenHue = v);
        Add(groups, "Green saturation", -100, 100, start.GreenSaturation, defaults.GreenSaturation, (s, v) => s.GreenSaturation = v);
        Add(groups, "Blue hue", -100, 100, start.BlueHue, defaults.BlueHue, (s, v) => s.BlueHue = v);
        Add(groups, "Blue saturation", -100, 100, start.BlueSaturation, defaults.BlueSaturation, (s, v) => s.BlueSaturation = v);

        groups.Children.Add(Heading("Curve"));
        _curveChannel.ItemsSource = new[] { "Whole picture", "Red", "Green", "Blue" }.Select(L10n.T).ToArray();
        _curveChannel.SelectedIndex = Math.Clamp((int)start.Curve.Channel, 0, 3);
        _curveChannel.Width = 160;
        _curve = new CurveEditor { Curves = Clone(start.Curve), Channel = _curveChannel.SelectedIndex, Height = 220 };
        _curveChannel.SelectionChanged += (_, _) => _curve.Channel = Math.Max(0, _curveChannel.SelectedIndex);
        _curve.Changed += RefreshPreview;
        groups.Children.Add(_curveChannel);
        groups.Children.Add(_curve);
        Add(groups, "Refine saturation", -100, 100, start.RefineSaturation, defaults.RefineSaturation, (s, v) => s.RefineSaturation = v);

        groups.Children.Add(Heading("Color mixer"));
        // The hues come first in the mixer's own places and then the saturations, which is the order the
        // kernel reads them in rather than the order a panel would list them.
        var mixer = start.Mixer;
        for (var family = 0; family < CameraRawSettings.MixerFamilies.Length; family++)
        {
            var hue = family;
            var saturation = CameraRawSettings.MixerFamilies.Length + family;
            var luminance = CameraRawSettings.MixerFamilies.Length * 2 + family;
            var name = CameraRawSettings.MixerFamilies[family];
            Add(groups, $"{name}: hue", -100, 100, At(mixer, hue), At(defaults.Mixer, hue), (s, v) => s.Mixer[hue] = v);
            Add(groups, $"{name}: saturation", -100, 100, At(mixer, saturation), At(defaults.Mixer, saturation), (s, v) => s.Mixer[saturation] = v);
            Add(groups, $"{name}: luminance", -100, 100, At(mixer, luminance), At(defaults.Mixer, luminance), (s, v) => s.Mixer[luminance] = v);
        }

        groups.Children.Add(Heading("Point color"));
        groups.Children.Add(new TextBlock
        {
            Text = L10n.T("Pick the color the brush is set to out of the picture, then move it. The Mac build picks colors by clicking on the canvas, which this panel does not do."),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.75,
        });
        var addPoint = new Button { Content = L10n.T("Add the brush color") };
        var removePoint = new Button { Content = L10n.T("Remove") };
        addPoint.Click += (_, _) => AddPoint();
        removePoint.Click += (_, _) => RemovePoint();
        groups.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { addPoint, removePoint },
        });
        groups.Children.Add(_points);
        _points.SelectionChanged += (_, _) => SelectPoint();
        _pointRow = _rows.Count;
        // The nine numbers of the one being edited, which is what the panel's sliders move.
        Add(groups, "Hue", 0, 360, 0, 0, (_, v) => Point(point => point.Hue = v), "0");
        Add(groups, "Saturation", 0, 1, 0, 0, (_, v) => Point(point => point.Saturation = v), "0.00");
        Add(groups, "Lightness", 0, 1, 0, 0, (_, v) => Point(point => point.Luminance = v), "0.00");
        Add(groups, "Turn the hue", -100, 100, 0, 0, (_, v) => Point(point => point.HueShift = v));
        Add(groups, "Raise the saturation", -100, 100, 0, 0, (_, v) => Point(point => point.SaturationShift = v));
        Add(groups, "Move the lightness", -100, 100, 0, 0, (_, v) => Point(point => point.LuminanceShift = v));
        Add(groups, "Hue range", 5, 180, 30, 30, (_, v) => Point(point => point.HueRange = v), "0");
        Add(groups, "Saturation range", 0.05, 1, 0.4, 0.4, (_, v) => Point(point => point.SaturationRange = v), "0.00");
        Add(groups, "Lightness range", 0.05, 1, 0.4, 0.4, (_, v) => Point(point => point.LuminanceRange = v), "0.00");
        foreach (var point in start.Points) _pointList.Add(point.Normalized());


        groups.Children.Add(Heading("Color grading"));
        Add(groups, "Shadows: hue", 0, 360, start.ShadowHue, defaults.ShadowHue, (s, v) => s.ShadowHue = v, "0");
        Add(groups, "Shadows: amount", 0, 100, start.ShadowSaturation, defaults.ShadowSaturation, (s, v) => s.ShadowSaturation = v, "0");
        Add(groups, "Shadows: lightness", -100, 100, start.ShadowLuminance, defaults.ShadowLuminance, (s, v) => s.ShadowLuminance = v);
        Add(groups, "Midtones: hue", 0, 360, start.MidtoneHue, defaults.MidtoneHue, (s, v) => s.MidtoneHue = v, "0");
        Add(groups, "Midtones: amount", 0, 100, start.MidtoneSaturation, defaults.MidtoneSaturation, (s, v) => s.MidtoneSaturation = v, "0");
        Add(groups, "Midtones: lightness", -100, 100, start.MidtoneLuminance, defaults.MidtoneLuminance, (s, v) => s.MidtoneLuminance = v);
        Add(groups, "Highlights: hue", 0, 360, start.HighlightHue, defaults.HighlightHue, (s, v) => s.HighlightHue = v, "0");
        Add(groups, "Highlights: amount", 0, 100, start.HighlightSaturation, defaults.HighlightSaturation, (s, v) => s.HighlightSaturation = v, "0");
        Add(groups, "Highlights: lightness", -100, 100, start.HighlightLuminance, defaults.HighlightLuminance, (s, v) => s.HighlightLuminance = v);
        Add(groups, "Whole picture: hue", 0, 360, start.GlobalHue, defaults.GlobalHue, (s, v) => s.GlobalHue = v, "0");
        Add(groups, "Whole picture: amount", 0, 100, start.GlobalSaturation, defaults.GlobalSaturation, (s, v) => s.GlobalSaturation = v, "0");
        Add(groups, "Whole picture: lightness", -100, 100, start.GlobalLuminance, defaults.GlobalLuminance, (s, v) => s.GlobalLuminance = v);
        Add(groups, "Grading blending", 0, 100, start.GradeBlending, defaults.GradeBlending, (s, v) => s.GradeBlending = v, "0");
        Add(groups, "Grading balance", -100, 100, start.GradeBalance, defaults.GradeBalance, (s, v) => s.GradeBalance = v);

        _glowStyle.SelectedIndex = start.GlowStyle;
        _vignetteStyle.SelectedIndex = start.VignetteStyle;
        _geometryProjection.SelectedIndex = (int)start.Geometry.Projection;
        _guides.AddRange(start.Geometry.Guides);
        RefreshGuides();
        Labelled();
        if (_pointList.Count > 0) _points.SelectedIndex = 0;
        foreach (var choice in new[] { _glowStyle, _vignetteStyle, _geometryProjection })
            choice.SelectionChanged += (_, _) => RefreshPreview();

        var ok = new Button { Content = L10n.T("Apply") };
        var cancel = new Button { Content = L10n.T("Cancel") };
        var reset = new Button { Content = L10n.T("Reset") };
        ok.Click += (_, _) => Apply();
        cancel.Click += (_, _) => Cancel();
        reset.Click += (_, _) => Reset();

        var title = new TextBlock
        {
            Text = L10n.T("Camera Raw Filter"),
            Margin = new Thickness(16, 12, 16, 4),
            Foreground = Skin.LabelBrush,
            FontWeight = FontWeight.SemiBold,
        };
        // The scope sits above the groups and outside their scroll, so it stays in view while they are worked
        // through — the Mac's panel puts its histogram in the same place.
        var scope = new StackPanel
        {
            Margin = new Thickness(16, 0, 16, 6),
            Spacing = 4,
            Children = { _scopes, _readout },
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 10, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { reset, cancel, ok },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(scope, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(title);
        dock.Children.Add(scope);
        dock.Children.Add(buttons);
        // The groups fill what is left, and scroll: the panel is the height of the window and they are not.
        dock.Children.Add(new ScrollViewer { Content = groups });
        View = dock;
    }

    /// <summary>The colours as the panel has them, with the one being edited stored back first.</summary>
    private List<CameraRawPointColor> Points()
    {
        StorePoint();
        return _pointList.Select(point => point.Normalized()).ToList();
    }

    /// <summary>Changes the colour being edited, if there is one.</summary>
    private void Point(Action<CameraRawPointColor> change)
    {
        if (_point is null) return;
        change(_point);
        Labelled();
    }

    /// <summary>Adds the brush's own colour to the points, at the middle of the picture's lightness.</summary>
    private void AddPoint()
    {
        if (_pointList.Count >= CameraRawSettings.MostPoints) return;
        StorePoint();
        _pointList.Add(new CameraRawPointColor
        {
            Hue = _brushHue,
            Saturation = _brushSaturation,
            Luminance = 0.5,
        });
        Labelled();
        _points.SelectedIndex = _pointList.Count - 1;
        _point = _pointList[^1];
        LoadPoint();
        RefreshPreview();
    }

    /// <summary>Takes the colour being edited out of the list.</summary>
    private void RemovePoint()
    {
        if (_points.SelectedIndex < 0 || _points.SelectedIndex >= _pointList.Count) return;
        var index = _points.SelectedIndex;
        _pointList.RemoveAt(index);
        _point = null;
        Labelled();
        _points.SelectedIndex = Math.Min(index, _pointList.Count - 1);
        _point = _points.SelectedIndex >= 0 ? _pointList[_points.SelectedIndex] : null;
        LoadPoint();
        RefreshPreview();
    }

    /// <summary>The list has moved to another colour: what was being edited is kept and the other loaded.</summary>
    private void SelectPoint()
    {
        if (_showingPoints) return;
        StorePoint();
        _point = _points.SelectedIndex >= 0 && _points.SelectedIndex < _pointList.Count
            ? _pointList[_points.SelectedIndex]
            : null;
        LoadPoint();
        RefreshPreview();
    }

    private void StorePoint()
    {
        if (_point is null) return;
        var at = _pointList.IndexOf(_point);
        if (at >= 0) _pointList[at] = _point = _point.Normalized();
    }

    /// <summary>
    /// The sliders read the colour being edited. The rows are the panel's own, so they are moved without
    /// asking for a preview of each one.
    /// </summary>
    private void LoadPoint()
    {
        if (_point is not { } point) return;
        var values = new[]
        {
            point.Hue, point.Saturation, point.Luminance,
            point.HueShift, point.SaturationShift, point.LuminanceShift,
            point.HueRange, point.SaturationRange, point.LuminanceRange,
        };
        var wasLoading = _loading;
        _loading = true;
        try
        {
            for (var index = 0; index < values.Length; index++)
                _rows[_pointRow + index].Slider.Value = values[index];
        }
        finally { _loading = wasLoading; }
    }

    /// <summary>Puts the numbers of each colour, and whether one is being edited at all, into the list.</summary>
    private void Labelled()
    {
        _showingPoints = true;
        try
        {
            var at = _points.SelectedIndex;
            _points.ItemsSource = _pointList
                .Select((point, index) => L10n.F("{0}: hue {1:0}°, saturation {2:0.00}, lightness {3:0.00}",
                    index + 1, point.Hue, point.Saturation, point.Luminance))
                .ToList();
            _points.SelectedIndex = at;
        }
        finally
        {
            _showingPoints = false;
        }
    }

    /// <summary>One of the mixer's numbers, or nothing when the settings came without their twenty-four.</summary>
    private static double At(double[] mixer, int index) => index < mixer.Length ? mixer[index] : 0;

    private static Control Heading(string text) => new TextBlock
    {
        Text = L10n.T(text),
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 2),
    };

    private void Add(StackPanel parent, string label, double least, double most, double value, double fallback,
        Action<CameraRawSettings, double> set, string format = "0.#")
    {
        var slider = new Slider { Minimum = least, Maximum = most, Value = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        var readout = new TextBlock { Text = "", Width = 44, VerticalAlignment = VerticalAlignment.Center };
        void UpdateReadout() => readout.Text = slider.Value.ToString(format);
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            UpdateReadout();
            RefreshPreview();
        };
        UpdateReadout();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("145,*,44") };
        var caption = new TextBlock
        {
            Text = L10n.T(label), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0),
        };
        slider.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        row.Children.Add(caption);
        row.Children.Add(slider);
        row.Children.Add(readout);
        parent.Children.Add(row);
        _fallbacks.Add(fallback);
        _rows.Add((slider, set, readout, format));
        _labelled[label] = slider;
    }

    private static Control Choice(string label, ComboBox box, string[] options)
    {
        box.ItemsSource = options.Select(L10n.T).ToArray();
        box.SelectedIndex = 0;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T(label), Width = 145, VerticalAlignment = VerticalAlignment.Center },
                box,
            },
        };
    }

    /// <summary>The brush's colour while the panel was opened, which is the colour a point starts at.</summary>
    private readonly double _brushHue;
    private readonly double _brushSaturation;

    /// <summary>
    /// The curve copied, so dragging a handle does not reach the layer until Apply: the editor writes into the
    /// copy it is given, and a history snapshot holds the record it started from.
    /// </summary>
    private static Compositor.Core.Format.CurvesSettings Clone(Compositor.Core.Format.CurvesSettings curves) => new()
    {
        Channel = curves.Channel,
        Channels = curves.Channels
            .Select(points => points.Select(point => new Compositor.Core.Format.CurvePoint { X = point.X, Y = point.Y }).ToList())
            .ToList(),
    };
}
