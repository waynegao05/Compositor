using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;
using Compositor.Core.Format;

namespace Compositor.Desktop;

/// <summary>
/// An adjustment layer's settings, as the sliders its kind presents. The kind is fixed when the layer is
/// made, so one panel serves them all; whatever the panel does not show is carried over from the layer, so
/// Apply never loses anything. Avalonia ships no such dialog, so this is one.
/// </summary>
internal sealed class AdjustmentDialog : DialogWindow
{
    private readonly List<(Slider Slider, Action<LayerAdjustment, double> Set)> _rows = [];
    private readonly List<double> _fallbacks = [];
    private readonly List<(CheckBox Box, Action<LayerAdjustment, bool> Set, bool Fallback)> _boxes = [];
    /// <summary>The colours the panel offers as swatches, the bar they feed, and what they started as.</summary>
    private readonly List<(ColorSwatch Swatch, Action<LayerAdjustment, (double Red, double Green, double Blue)> Set,
        (double Red, double Green, double Blue) Fallback, bool AtStart, ColorStrip Strip)> _swatches = [];
    private readonly ComboBox? _range;
    private CurveEditor? _curve;
    private readonly ComboBox? _levelsChannel;
    private readonly LayerAdjustment _start;
    private LayerAdjustment? _result;
    private LevelsSettings _levels;
    private HueSaturationSettings _hsv;
    private bool _loadingRange;

    /// <summary>Asks for the picture to be shown with these settings as they stand.</summary>
    public Action<LayerAdjustment>? Preview { get; set; }

    internal AdjustmentDialog(LayerAdjustment start)
    {
        _start = start;
        _levels = Levels(start);
        _hsv = Hsv(start);
        var kind = start.Kind;
        Title = L10n.F("{0} Adjustment", L10n.T(LayerPlacement.Name(kind)));
        Width = 460;
        Height = 580;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var group = new StackPanel { Margin = new Thickness(16), Spacing = 4 };
        switch (kind)
        {
            case AdjustmentKind.HueSaturation:
            {
                var hsv = start.ResolvedHSV;
                _range = new ComboBox
                {
                    ItemsSource = HueBand.Ranges.Select(range => L10n.T(range.ToString())).ToList(),
                    SelectedIndex = HueBand.Ranges.IndexOf(hsv.Range),
                };
                group.Children.Add(Row("Range", _range));
                Add(group, "Hue", -180, 180, hsv.Current.Hue, 0, (s, v) => s.HsvSettings = Hsv(s, hue: v));
                Add(group, "Saturation", -100, 100, hsv.Current.Saturation, 0, (s, v) => s.HsvSettings = Hsv(s, saturation: v));
                Add(group, "Lightness", -100, 100, hsv.Current.Lightness, 0, (s, v) => s.HsvSettings = Hsv(s, lightness: v));
                Check(group, "Colorize", hsv.Colorize, (s, v) => s.HsvSettings = Hsv(s, colorize: v));
                _range.SelectionChanged += (_, _) => ChangeRange();
                break;
            }
            case AdjustmentKind.Levels:
            {
                var range = start.Levels.Ranges[(int)start.Levels.Channel];
                _levelsChannel = new ComboBox
                {
                    ItemsSource = new[] { "RGB", "Red", "Green", "Blue" }.Select(L10n.T).ToArray(),
                    SelectedIndex = (int)start.Levels.Channel,
                };
                group.Children.Add(Row("Channel", _levelsChannel));
                Add(group, "Black point", 0, 254, range.Black, 0, (s, v) => s.Levels.Ranges[(int)s.Levels.Channel].Black = v);
                Add(group, "Gamma", 0.1, 9.99, range.Gamma, 1, (s, v) => s.Levels.Ranges[(int)s.Levels.Channel].Gamma = v, "0.00");
                Add(group, "White point", 1, 255, range.White, 255, (s, v) => s.Levels.Ranges[(int)s.Levels.Channel].White = v);
                Add(group, "Output black", 0, 255, range.OutputBlack, 0, (s, v) => s.Levels.Ranges[(int)s.Levels.Channel].OutputBlack = v);
                Add(group, "Output white", 0, 255, range.OutputWhite, 255, (s, v) => s.Levels.Ranges[(int)s.Levels.Channel].OutputWhite = v);
                _levelsChannel.SelectionChanged += (_, _) => ChangeRange();
                break;
            }
            case AdjustmentKind.Exposure:
                Add(group, "Exposure, stops", -20, 20, start.Exposure.Exposure, 0, (s, v) => s.ExposureSettings = Exposure(s, exposure: v), "0.00");
                Add(group, "Offset", -0.5, 0.5, start.Exposure.Offset, 0, (s, v) => s.ExposureSettings = Exposure(s, offset: v), "0.000");
                Add(group, "Gamma", 0.01, 9.99, start.Exposure.Gamma, 1, (s, v) => s.ExposureSettings = Exposure(s, gamma: v), "0.00");
                break;
            case AdjustmentKind.Grain:
                Add(group, "Amount", 0, 100, start.Grain.Amount, 25, (s, v) => s.GrainSettings = Grain(s, amount: v));
                Add(group, "Size", 0.5, 20, start.Grain.Size, 1.5, (s, v) => s.GrainSettings = Grain(s, size: v), "0.0");
                Add(group, "Roughness", 0, 100, start.Grain.Roughness, 50, (s, v) => s.GrainSettings = Grain(s, roughness: v));
                break;
            case AdjustmentKind.Curves:
            {
                var channel = new ComboBox
                {
                    ItemsSource = new[] { "RGB", "Red", "Green", "Blue" }.Select(L10n.T).ToArray(),
                    SelectedIndex = Math.Clamp((int)start.Curves.Channel, 0, 3),
                    Width = 120,
                };
                group.Children.Add(Row("Channel", channel));
                _curve = new CurveEditor { Curves = Clone(start.Curves), Channel = channel.SelectedIndex, Height = 260 };
                channel.SelectionChanged += (_, _) => _curve.Channel = Math.Max(0, channel.SelectedIndex);
                _curve.Changed += () => Preview?.Invoke(Built(_start));
                group.Children.Add(_curve);
                break;
            }
            case AdjustmentKind.GradientMap:
            {
                // The two ends are swatches that open the picker, over the bar they make, as the Mac's sheet has
                // it — where this panel used to offer three numbers for each end.
                var strip = new ColorStrip
                {
                    Height = 20,
                    Margin = new Thickness(0, 4, 0, 6),
                    From = End(start.GradientMap.Shadows),
                    To = End(start.GradientMap.Highlights),
                };
                group.Children.Add(strip);
                // What Reset goes back to, which is what a layer of this kind is made with.
                var fresh = new LayerAdjustment { Kind = kind }.GradientMap;
                Swatch(group, "Shadows", End(start.GradientMap.Shadows), End(fresh.Shadows),
                    "Color Picker (Gradient Map Shadows)", "Choose the shadows color", strip, atStart: true,
                    (s, colour) => s.GradientMapSettings = Map(s, shadowRed: colour.Red, shadowGreen: colour.Green, shadowBlue: colour.Blue));
                Swatch(group, "Highlights", End(start.GradientMap.Highlights), End(fresh.Highlights),
                    "Color Picker (Gradient Map Highlights)", "Choose the highlights color", strip, atStart: false,
                    (s, colour) => s.GradientMapSettings = Map(s, highlightRed: colour.Red, highlightGreen: colour.Green, highlightBlue: colour.Blue));
                Check(group, "Reverse", start.GradientMap.Reversed, (s, v) => s.GradientMapSettings = Map(s, reversed: v));
                break;
            }
            case AdjustmentKind.AddNoise:
                Add(group, "Amount, %", 0.1, 400, start.ResolvedNoiseAmount, 10, (s, v) => s.NoiseAmount = v);
                Check(group, "Gaussian", start.ResolvedNoiseGaussian, (s, v) => s.NoiseGaussian = v);
                Check(group, "Monochromatic", start.ResolvedNoiseMonochromatic, (s, v) => s.NoiseMonochromatic = v);
                break;
            case AdjustmentKind.GaussianBlur:
                Add(group, "Radius, pixels", 0.1, 250, start.GaussianRadius, 10, (s, v) => s.BlurRadius = v);
                break;
            case AdjustmentKind.MotionBlur:
                Add(group, "Angle, degrees", -90, 90, start.ResolvedMotionAngle, 0, (s, v) => s.MotionAngle = v);
                Add(group, "Distance, pixels", 1, 2000, start.ResolvedMotionDistance, 10, (s, v) => s.MotionDistance = v, "0");
                break;
            case AdjustmentKind.Invert:
                group.Children.Add(new TextBlock { Text = L10n.T("Invert has no settings: it turns every pixel over.") });
                break;
            case AdjustmentKind.BlackWhite:
                Add(group, "Reds", -200, 300, start.BlackWhite.Reds, 40, (s, v) => s.BlackWhiteSettings = Mix(s, reds: v));
                Add(group, "Yellows", -200, 300, start.BlackWhite.Yellows, 60, (s, v) => s.BlackWhiteSettings = Mix(s, yellows: v));
                Add(group, "Greens", -200, 300, start.BlackWhite.Greens, 40, (s, v) => s.BlackWhiteSettings = Mix(s, greens: v));
                Add(group, "Cyans", -200, 300, start.BlackWhite.Cyans, 60, (s, v) => s.BlackWhiteSettings = Mix(s, cyans: v));
                Add(group, "Blues", -200, 300, start.BlackWhite.Blues, 20, (s, v) => s.BlackWhiteSettings = Mix(s, blues: v));
                Add(group, "Magentas", -200, 300, start.BlackWhite.Magentas, 80, (s, v) => s.BlackWhiteSettings = Mix(s, magentas: v));
                Check(group, "Tint", start.BlackWhite.Tint, (s, v) => s.BlackWhiteSettings = Mix(s, tint: v));
                Add(group, "Tint hue", 0, 360, start.BlackWhite.TintHue, 40, (s, v) => s.BlackWhiteSettings = Mix(s, tintHue: v));
                Add(group, "Tint saturation", 0, 100, start.BlackWhite.TintSaturation, 20, (s, v) => s.BlackWhiteSettings = Mix(s, tintSaturation: v));
                break;
            default:
                Add(group, "Shadows: cyan to red", -100, 100, start.ColorBalance.ShadowCyanRed, 0, (s, v) => s.ColorBalanceSettings = Balance(s, shadowCyanRed: v));
                Add(group, "Shadows: magenta to green", -100, 100, start.ColorBalance.ShadowMagentaGreen, 0, (s, v) => s.ColorBalanceSettings = Balance(s, shadowMagentaGreen: v));
                Add(group, "Shadows: yellow to blue", -100, 100, start.ColorBalance.ShadowYellowBlue, 0, (s, v) => s.ColorBalanceSettings = Balance(s, shadowYellowBlue: v));
                Add(group, "Midtones: cyan to red", -100, 100, start.ColorBalance.MidCyanRed, 0, (s, v) => s.ColorBalanceSettings = Balance(s, midCyanRed: v));
                Add(group, "Midtones: magenta to green", -100, 100, start.ColorBalance.MidMagentaGreen, 0, (s, v) => s.ColorBalanceSettings = Balance(s, midMagentaGreen: v));
                Add(group, "Midtones: yellow to blue", -100, 100, start.ColorBalance.MidYellowBlue, 0, (s, v) => s.ColorBalanceSettings = Balance(s, midYellowBlue: v));
                Add(group, "Highlights: cyan to red", -100, 100, start.ColorBalance.HighlightCyanRed, 0, (s, v) => s.ColorBalanceSettings = Balance(s, highlightCyanRed: v));
                Add(group, "Highlights: magenta to green", -100, 100, start.ColorBalance.HighlightMagentaGreen, 0, (s, v) => s.ColorBalanceSettings = Balance(s, highlightMagentaGreen: v));
                Add(group, "Highlights: yellow to blue", -100, 100, start.ColorBalance.HighlightYellowBlue, 0, (s, v) => s.ColorBalanceSettings = Balance(s, highlightYellowBlue: v));
                Check(group, "Preserve luminosity", start.ColorBalance.PreserveLuminosity, (s, v) => s.ColorBalanceSettings = Balance(s, preserve: v), fallback: true);
                break;
        }

        var ok = new Button { Content = L10n.T("Apply"), IsDefault = true };
        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        var reset = new Button { Content = L10n.T("Reset") };
        ok.Click += (_, _) => Accept(start);
        cancel.Click += (_, _) => Close();
        reset.Click += (_, _) => Restore(new LayerAdjustment { Kind = kind });
        group.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { reset, cancel, ok },
        });
        Content = new ScrollViewer { Content = group };
    }

    private void StoreRange()
    {
        if (_loadingRange) return;
        if (_start.Kind == AdjustmentKind.Levels && _rows.Count == 5)
        {
            _levels.Ranges[(int)_levels.Channel] = new LevelsChannelRange
            {
                Black = _rows[0].Slider.Value, Gamma = _rows[1].Slider.Value,
                White = _rows[2].Slider.Value, OutputBlack = _rows[3].Slider.Value,
                OutputWhite = _rows[4].Slider.Value,
            };
        }
        else if (_start.Kind == AdjustmentKind.HueSaturation && _rows.Count == 3)
        {
            _hsv.Set(_rows[0].Slider.Value, _rows[1].Slider.Value, _rows[2].Slider.Value);
            _hsv.Colorize = _boxes[0].Box.IsChecked == true;
        }
    }

    private void ChangeRange()
    {
        if (_loadingRange) return;
        StoreRange();
        if (_levelsChannel is { SelectedIndex: >= 0 } channel) _levels.Channel = (LevelsChannel)channel.SelectedIndex;
        if (_range is { SelectedIndex: >= 0 } range) _hsv.Range = HueBand.Ranges[range.SelectedIndex];
        LoadRange();
        Preview?.Invoke(Built(_start));
    }

    private void LoadRange()
    {
        _loadingRange = true;
        try
        {
            double[] values;
            if (_start.Kind == AdjustmentKind.Levels)
            {
                var range = _levels.Ranges[(int)_levels.Channel];
                values = [range.Black, range.Gamma, range.White, range.OutputBlack, range.OutputWhite];
            }
            else
            {
                var range = _hsv.Current;
                values = [range.Hue, range.Saturation, range.Lightness];
            }
            for (var i = 0; i < values.Length; i++) _rows[i].Slider.Value = values[i];
        }
        finally { _loadingRange = false; }
    }

    private static HueSaturationSettings Hsv(LayerAdjustment settings,
        double? hue = null, double? saturation = null, double? lightness = null, bool? colorize = null)
    {
        var from = settings.ResolvedHSV;
        var next = new HueSaturationSettings
        {
            Range = from.Range,
            Colorize = colorize ?? from.Colorize,
            InvertRange = from.InvertRange,
        };
        foreach (var entry in from.Adjustments.Entries) next.Adjustments.Set(entry.Key, new RangeAdjustment
        { Hue = entry.Value.Hue, Saturation = entry.Value.Saturation, Lightness = entry.Value.Lightness });
        foreach (var entry in from.Bands.Entries) next.Bands.Set(entry.Key, new HueBand
        { FalloffStart = entry.Value.FalloffStart, RangeStart = entry.Value.RangeStart,
            RangeEnd = entry.Value.RangeEnd, FalloffEnd = entry.Value.FalloffEnd });
        var current = from.Current;
        next.Set(hue ?? current.Hue, saturation ?? current.Saturation, lightness ?? current.Lightness);
        return next;
    }

    private static ExposureSettings Exposure(LayerAdjustment settings,
        double? exposure = null, double? offset = null, double? gamma = null) => new()
    {
        Exposure = exposure ?? settings.Exposure.Exposure,
        Offset = offset ?? settings.Exposure.Offset,
        Gamma = gamma ?? settings.Exposure.Gamma,
    };

    private static GradientMapSettings Map(LayerAdjustment settings,
        double? shadowRed = null, double? shadowGreen = null, double? shadowBlue = null,
        double? highlightRed = null, double? highlightGreen = null, double? highlightBlue = null,
        bool? reversed = null)
    {
        var map = settings.GradientMap;
        return new GradientMapSettings
        {
            Shadows = AdjustmentColor.From(
                shadowRed ?? map.Shadows.Red, shadowGreen ?? map.Shadows.Green, shadowBlue ?? map.Shadows.Blue),
            Highlights = AdjustmentColor.From(
                highlightRed ?? map.Highlights.Red, highlightGreen ?? map.Highlights.Green, highlightBlue ?? map.Highlights.Blue),
            Reversed = reversed ?? map.Reversed,
        };
    }

    private static GrainSettings Grain(LayerAdjustment settings, double? amount = null, double? size = null, double? roughness = null)
    {
        var grain = settings.Grain;
        return new GrainSettings { Amount = amount ?? grain.Amount, Size = size ?? grain.Size, Roughness = roughness ?? grain.Roughness, Seed = grain.Seed };
    }

    private static BlackWhiteSettings Mix(LayerAdjustment settings,
        double? reds = null, double? yellows = null, double? greens = null, double? cyans = null,
        double? blues = null, double? magentas = null, bool? tint = null,
        double? tintHue = null, double? tintSaturation = null)
    {
        var mix = settings.BlackWhite;
        return new BlackWhiteSettings
        {
            Reds = reds ?? mix.Reds,
            Yellows = yellows ?? mix.Yellows,
            Greens = greens ?? mix.Greens,
            Cyans = cyans ?? mix.Cyans,
            Blues = blues ?? mix.Blues,
            Magentas = magentas ?? mix.Magentas,
            Tint = tint ?? mix.Tint,
            TintHue = tintHue ?? mix.TintHue,
            TintSaturation = tintSaturation ?? mix.TintSaturation,
        };
    }

    private static ColorBalanceSettings Balance(LayerAdjustment settings,
        double? shadowCyanRed = null, double? shadowMagentaGreen = null, double? shadowYellowBlue = null,
        double? midCyanRed = null, double? midMagentaGreen = null, double? midYellowBlue = null,
        double? highlightCyanRed = null, double? highlightMagentaGreen = null, double? highlightYellowBlue = null,
        bool? preserve = null)
    {
        var balance = settings.ColorBalance;
        return new ColorBalanceSettings
        {
            ShadowCyanRed = shadowCyanRed ?? balance.ShadowCyanRed,
            ShadowMagentaGreen = shadowMagentaGreen ?? balance.ShadowMagentaGreen,
            ShadowYellowBlue = shadowYellowBlue ?? balance.ShadowYellowBlue,
            MidCyanRed = midCyanRed ?? balance.MidCyanRed,
            MidMagentaGreen = midMagentaGreen ?? balance.MidMagentaGreen,
            MidYellowBlue = midYellowBlue ?? balance.MidYellowBlue,
            HighlightCyanRed = highlightCyanRed ?? balance.HighlightCyanRed,
            HighlightMagentaGreen = highlightMagentaGreen ?? balance.HighlightMagentaGreen,
            HighlightYellowBlue = highlightYellowBlue ?? balance.HighlightYellowBlue,
            PreserveLuminosity = preserve ?? balance.PreserveLuminosity,
        };
    }

    /// <summary>One end of the Gradient Map's colours as a colour the panel can draw.</summary>
    private static (double Red, double Green, double Blue) End(AdjustmentColor colour) =>
        (colour.Red, colour.Green, colour.Blue);

    /// <summary>
    /// One end of the Gradient Map as a swatch that opens the app's picker on it. What the picker reports is
    /// written into the settings and previewed, and the bar above the swatches is redrawn to match; a Cancel
    /// reports the colour it opened on and puts everything back.
    /// </summary>
    private void Swatch(StackPanel parent, string label, (double Red, double Green, double Blue) colour,
        (double Red, double Green, double Blue) fallback, string title, string hint, ColorStrip strip, bool atStart,
        Action<LayerAdjustment, (double Red, double Green, double Blue)> set)
    {
        var swatch = new ColorSwatch(hint) { Colour = colour };
        swatch.Click += (_, _) => _ = ColorPickerDialog.Pick(this, title, swatch.Colour, moved =>
        {
            swatch.Colour = moved;
            if (atStart) strip.From = moved;
            else strip.To = moved;
            strip.Redraw();
            Preview?.Invoke(Built(_start));
        });
        parent.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                swatch,
                new TextBlock { Text = L10n.T(label), VerticalAlignment = VerticalAlignment.Center },
            },
        });
        _swatches.Add((swatch, set, fallback, atStart, strip));
    }

    private void Check(StackPanel parent, string label, bool value, Action<LayerAdjustment, bool> set, bool fallback = false)
    {
        var box = new CheckBox { Content = L10n.T(label), IsChecked = value };
        parent.Children.Add(box);
        _boxes.Add((box, set, fallback));
        box.IsCheckedChanged += (_, _) => { if (!_loadingRange) Preview?.Invoke(Built(_start)); };
    }

    private static Control Row(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = L10n.T(label), Width = 150, VerticalAlignment = VerticalAlignment.Center },
            control,
        },
    };

    private void Add(StackPanel parent, string label, double least, double most, double value, double fallback,
        Action<LayerAdjustment, double> set, string format = "0.#")
    {
        var slider = new Slider { Minimum = least, Maximum = most, Value = value, Width = 220 };
        var readout = new TextBlock { Text = "", Width = 44, VerticalAlignment = VerticalAlignment.Center };
        void Show() => readout.Text = slider.Value.ToString(format);
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            Show();
            if (!_loadingRange) Preview?.Invoke(Built(_start));
        };
        Show();
        parent.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T(label), Width = 150, VerticalAlignment = VerticalAlignment.Center },
                slider,
                readout,
            },
        });
        _rows.Add((slider, set));
        _fallbacks.Add(fallback);
    }

    /// <summary>Back to what the layer would be made with.</summary>
    private void Restore(LayerAdjustment fresh)
    {
        _loadingRange = true;
        try
        {
            _levels = Levels(fresh);
            _hsv = Hsv(fresh);
            for (var i = 0; i < _rows.Count; i++) _rows[i].Slider.Value = _fallbacks[i];
            foreach (var (box, _, fallback) in _boxes) box.IsChecked = fallback;
            if (_range is not null) _range.SelectedIndex = HueBand.Ranges.IndexOf(ColorRange.Master);
            if (_levelsChannel is not null) _levelsChannel.SelectedIndex = (int)fresh.Levels.Channel;
            if (_curve is not null) _curve.Curves = Clone(fresh.Curves);
            foreach (var (swatch, set, fallback, atStart, strip) in _swatches)
            {
                swatch.Colour = fallback;
                if (atStart) strip.From = fallback;
                else strip.To = fallback;
                strip.Redraw();
            }
        }
        finally { _loadingRange = false; }
        Preview?.Invoke(Built(_start));
    }

    /// <summary>
    /// The layer's curves copied, so dragging a handle does not reach the layer until Apply: the editor writes
    /// into the copy it is given, and a history snapshot holds the record it started from.
    /// </summary>
    private static CurvesSettings Clone(CurvesSettings curves) => new()
    {
        Channel = curves.Channel,
        Channels = curves.Channels.Select(points => points.Select(point => new CurvePoint { X = point.X, Y = point.Y }).ToList()).ToList(),
    };

    /// <summary>A copy of the layer's levels, so the panel's changes do not reach the layer until Apply.</summary>
    private static LevelsSettings Levels(LayerAdjustment settings)
    {
        var copy = new LevelsSettings { Channel = settings.Levels.Channel };
        copy.Ranges.Clear();
        foreach (var range in settings.Levels.Ranges)
        {
            copy.Ranges.Add(new LevelsChannelRange
            {
                Black = range.Black,
                Gamma = range.Gamma,
                White = range.White,
                OutputBlack = range.OutputBlack,
                OutputWhite = range.OutputWhite,
            });
        }
        return copy;
    }

    /// <summary>The settings as the panel has them, for a preview of what they would do.</summary>
    private LayerAdjustment Built(LayerAdjustment start)
    {
        StoreRange();
        var settings = new LayerAdjustment
        {
            Kind = start.Kind,
            Hue = start.Hue,
            Saturation = start.Saturation,
            Lightness = start.Lightness,
            Colorize = start.Colorize,
            Levels = Levels(new LayerAdjustment { Levels = _levels }),
            HsvSettings = Hsv(new LayerAdjustment { HsvSettings = _hsv }),
            Curves = start.Curves,
            ExposureSettings = start.ExposureSettings,
            GradientMapSettings = start.GradientMapSettings,
            GrainSettings = start.GrainSettings,
            BlackWhiteSettings = start.BlackWhiteSettings,
            ColorBalanceSettings = start.ColorBalanceSettings,
            BlurRadius = start.BlurRadius,
            MotionAngle = start.MotionAngle,
            MotionDistance = start.MotionDistance,
            NoiseAmount = start.NoiseAmount,
            NoiseGaussian = start.NoiseGaussian,
            NoiseMonochromatic = start.NoiseMonochromatic,
            NoiseSeed = start.NoiseSeed,
        };
        if (_curve is { } curve) settings.Curves = curve.Curves;
        foreach (var (slider, set) in _rows) set(settings, slider.Value);
        foreach (var (box, set, _) in _boxes) set(settings, box.IsChecked == true);
        foreach (var (swatch, set, _, _, _) in _swatches) set(settings, swatch.Colour);
        // The selected range and every previously edited range are retained in the draft.
        if (_range is { SelectedIndex: >= 0 } range)
        {
            settings.HsvSettings ??= Hsv(settings);
            settings.HsvSettings.Range = HueBand.Ranges[range.SelectedIndex];
        }
        return settings;
    }

    private void Accept(LayerAdjustment start)
    {
        var settings = Built(start);
        _result = settings.IsValid ? settings : null;
        Close();
    }

    /// <summary>The settings to put back on the layer, or null when the panel was dismissed or asks nothing.</summary>
    public static async Task<LayerAdjustment?> Ask(Window owner, LayerAdjustment start,
        Action<LayerAdjustment>? preview = null)
    {
        var dialog = new AdjustmentDialog(start) { Preview = preview };
        var closed = dialog.ShowDialog(owner);
        dialog.Preview?.Invoke(dialog.Built(start));
        await closed;
        return dialog._result;
    }
}
