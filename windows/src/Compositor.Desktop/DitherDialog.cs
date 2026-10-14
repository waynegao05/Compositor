using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// Filter ▸ Dither's panel: the look and its amounts, and Apply to run it over the selected layer. Avalonia
/// ships no such dialog, so this is one.
/// <para>
/// The controls a look does not use are hidden, as the Mac build's panel hides them: the tone amounts belong
/// to the styles that quantize to tones, the cell size and angle to the half-tone shapes, the characters and
/// their size to ASCII, the ink and paper swatches to the two-colour mode, and the pixel shape to a pixel
/// size above one. A hidden row takes no room in the stack, so the panel is as short as its look needs, and
/// a heading goes with the last of its rows.
/// </para>
/// </summary>
internal sealed class DitherDialog : DialogWindow
{
    /// <summary>One row of the panel, and whether the look being edited uses it.</summary>
    private readonly List<(Control Row, Func<bool> Applies)> _rows = [];
    /// <summary>One heading and the rows under it, so the heading can go when they all have.</summary>
    private readonly List<(Control Heading, List<Control> Rows)> _sections = [];
    private readonly List<(Slider Slider, Action<DitherSettings, double> Set)> _sliders = [];
    private readonly List<double> _fallbacks = [];
    /// <summary>The two colours of the ink and paper, and what they started as for Reset.</summary>
    private readonly List<(ColorSwatch Swatch, Action<DitherSettings, (double Red, double Green, double Blue)> Set,
        (double Red, double Green, double Blue) Fallback)> _swatches = [];
    private readonly ComboBox _style = new();
    /// <summary>What each item of the look list stands for: the look, or nothing for a rule between groups.</summary>
    private readonly List<DitherStyle?> _styleRows = [];
    private readonly ComboBox _shape = new();
    private readonly ComboBox _colors = new();
    private readonly CheckBox _lightOnDark = new();
    private readonly TextBox _characters = new();
    private readonly Slider _pixelSize;
    private List<Control>? _under;
    /// <summary>
    /// The look and amounts being edited: a copy of the ones the panel was opened with, which are the ones
    /// Dither was last used with. Editing a copy is what lets a Cancel leave them as they were.
    /// </summary>
    private readonly DitherSettings _amounts;
    private DitherSettings? _result;

    /// <summary>Asks for the picture to be shown with this look and its amounts as they stand.</summary>
    public Action<DitherStyle, DitherSettings>? Preview { get; set; }

    private static readonly string[] StyleNames =
    [
        "Atkinson (Classic Mac)", "Floyd–Steinberg", "Bayer 2 × 2", "Bayer 4 × 4", "Bayer 8 × 8",
        "Halftone Dots", "Halftone Lines", "Halftone Diamonds", "Mac Patterns", "ASCII",
    ];

    private DitherDialog(DitherStyle style, DitherSettings start)
    {
        _amounts = start.Copy();
        Title = L10n.T("Dither");
        Width = 460;
        Height = 660;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var defaults = new DitherSettings();
        var group = new StackPanel { Margin = new Thickness(16), Spacing = 4 };

        // The looks are grouped as the Mac's panel groups them, with a rule between the groups, so the list is
        // filled here rather than by Choice and _styleRows says which look an item is.
        _styleRows.AddRange(GroupedChoice.Fill(_style, DitherSettings.Groups, look => StyleNames[(int)look]));
        _style.SelectedIndex = Math.Max(0, _styleRows.IndexOf(style));
        Row(group, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T("Look"), Width = 130, VerticalAlignment = VerticalAlignment.Center },
                _style,
            },
        }, () => true);

        Heading(group, "Pixels");
        _pixelSize = Add(group, "Pixel size", 1, 32, start.PixelSize, defaults.PixelSize,
            (s, v) => s.PixelSize = v, "0", () => Style() != DitherStyle.Ascii);
        Choice(group, "Pixel shape", _shape, ["Square", "Dot"], (int)start.PixelShape,
            () => _pixelSize.Value > 1 && Style() != DitherStyle.Ascii);

        Heading(group, "Tones");
        Add(group, "Tones", 2, 8, start.Levels, defaults.Levels, (s, v) => s.Levels = v, "0",
            () => DitherSettings.HasTones(Style()));
        Add(group, "Diffusion, %", 0, 100, start.Diffusion, defaults.Diffusion, (s, v) => s.Diffusion = v, "0",
            () => DitherSettings.Diffuses(Style()));
        Add(group, "Density", -100, 100, start.Density, defaults.Density, (s, v) => s.Density = v, "0.#", () => true);
        Add(group, "Contrast", -100, 100, start.Contrast, defaults.Contrast, (s, v) => s.Contrast = v, "0.#", () => true);

        Heading(group, "Halftone and characters");
        Add(group, "Cell size", 4, 64, start.CellSize, defaults.CellSize, (s, v) => s.CellSize = v, "0",
            () => DitherSettings.IsHalftone(Style()));
        Add(group, "Angle, degrees", -90, 90, start.Angle, defaults.Angle, (s, v) => s.Angle = v, "0.#",
            () => DitherSettings.IsHalftone(Style()));
        Add(group, "Text size", 6, 64, start.TextSize, defaults.TextSize, (s, v) => s.TextSize = v, "0",
            () => Style() == DitherStyle.Ascii);
        _characters.Text = start.Characters;
        _characters.Width = 240;
        Text(group, "Characters", _characters, () => Style() == DitherStyle.Ascii);
        Choice(group, "Marks", _lightOnDark, start.LightOnDark, () => DitherSettings.DrawsMarks(Style()));

        Heading(group, "Colors");
        Choice(group, "Ink and paper", _colors, ["Black & White", "Two Colors", "Original"], (int)start.Colors, () => true);
        // The two colours are swatches that open the picker, as the Mac's panel has them, where this panel used
        // to offer three numbers for each of them.
        var dark = Swatch("Dark", (start.DarkRed, start.DarkGreen, start.DarkBlue),
            (defaults.DarkRed, defaults.DarkGreen, defaults.DarkBlue), "Color Picker (Dither Dark Color)", "Choose the dark color",
            (s, colour) =>
            {
                s.DarkRed = colour.Red;
                s.DarkGreen = colour.Green;
                s.DarkBlue = colour.Blue;
            });
        var light = Swatch("Light", (start.LightRed, start.LightGreen, start.LightBlue),
            (defaults.LightRed, defaults.LightGreen, defaults.LightBlue), "Color Picker (Dither Light Color)", "Choose the light color",
            (s, colour) =>
            {
                s.LightRed = colour.Red;
                s.LightGreen = colour.Green;
                s.LightBlue = colour.Blue;
            });
        Row(group, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T("Dark"), VerticalAlignment = VerticalAlignment.Center },
                dark,
                new TextBlock { Text = L10n.T("Light"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                light,
            },
        }, TwoColours);

        var ok = new Button { Content = L10n.T("Apply"), IsDefault = true };
        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        var reset = new Button { Content = L10n.T("Reset") };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        reset.Click += (_, _) => Restore(defaults);
        group.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { reset, cancel, ok },
        });

        // A look, a pixel size or an ink choice that changes which controls apply is followed at once.
        _style.SelectionChanged += (_, _) => { Refresh(); RefreshPreview(); };
        _shape.SelectionChanged += (_, _) => { Refresh(); RefreshPreview(); };
        _colors.SelectionChanged += (_, _) => { Refresh(); RefreshPreview(); };
        _pixelSize.PropertyChanged += (_, change) =>
        {
            if (change.Property == Slider.ValueProperty) Refresh();
        };
        _lightOnDark.IsCheckedChanged += (_, _) => RefreshPreview();
        _characters.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) RefreshPreview();
        };
        Refresh();

        Content = new ScrollViewer { Content = group };
    }

    /// <summary>Whether the two-colour swatches apply, which they do once the ink and paper are chosen.</summary>
    private void RefreshPreview() => Preview?.Invoke(Style(), Current());

    private bool TwoColours() => (DitherColors)Math.Max(0, _colors.SelectedIndex) == DitherColors.TwoColors;

    /// <summary>Shows the rows this look uses and hides the rest, headings included when their rows have gone.</summary>
    private void Refresh()
    {
        foreach (var (row, applies) in _rows) row.IsVisible = applies();
        foreach (var (heading, rows) in _sections) heading.IsVisible = rows.Exists(row => row.IsVisible);
    }

    private void Heading(StackPanel parent, string text)
    {
        var heading = new TextBlock
        {
            Text = L10n.T(text),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 2),
        };
        parent.Children.Add(heading);
        _under = [];
        _sections.Add((heading, _under));
    }

    /// <summary>Adds a row to the panel, under the heading it belongs to.</summary>
    private void Row(StackPanel parent, Control row, Func<bool> applies)
    {
        parent.Children.Add(row);
        _rows.Add((row, applies));
        _under?.Add(row);
    }

    private void Choice(StackPanel parent, string label, ComboBox box, string[] options, int selected, Func<bool> applies)
    {
        box.ItemsSource = options.Select(L10n.T).ToArray();
        box.SelectedIndex = Math.Clamp(selected, 0, options.Length - 1);
        Row(parent, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T(label), Width = 130, VerticalAlignment = VerticalAlignment.Center },
                box,
            },
        }, applies);
    }

    /// <summary>"Light on dark" is a box, not a list, since it says yes or no.</summary>
    private void Choice(StackPanel parent, string label, CheckBox box, bool selected, Func<bool> applies)
    {
        box.Content = L10n.T(label);
        box.IsChecked = selected;
        Row(parent, box, applies);
    }

    private void Text(StackPanel parent, string label, TextBox box, Func<bool> applies) => Row(parent, new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = L10n.T(label), Width = 130, VerticalAlignment = VerticalAlignment.Center },
            box,
        },
    }, applies);

    /// <summary>
    /// One of the two colours as a swatch that opens the app's picker on it. What the picker reports is written
    /// into the look and previewed, so the picture follows the colour while it is being chosen; a Cancel reports
    /// the colour it opened on and puts both back.
    /// </summary>
    private ColorSwatch Swatch(string label, (double Red, double Green, double Blue) colour,
        (double Red, double Green, double Blue) fallback,
        string title, string hint, Action<DitherSettings, (double Red, double Green, double Blue)> set)
    {
        var swatch = new ColorSwatch(hint) { Colour = colour };
        swatch.Click += (_, _) => _ = ColorPickerDialog.Pick(this, title, swatch.Colour, moved =>
        {
            swatch.Colour = moved;
            set(_amounts, moved);
            RefreshPreview();
        });
        _swatches.Add((swatch, set, fallback));
        return swatch;
    }

    /// <summary>Adds one amount, and hands back its slider when the caller needs to watch it.</summary>
    private Slider Add(StackPanel parent, string label, double least, double most, double value, double fallback,
        Action<DitherSettings, double> set, string format, Func<bool> applies)
    {
        var slider = new Slider { Minimum = least, Maximum = most, Value = value, Width = 240 };
        var readout = new TextBlock { Text = "", Width = 44, VerticalAlignment = VerticalAlignment.Center };
        void Show() => readout.Text = slider.Value.ToString(format);
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            Show();
            RefreshPreview();
        };
        Show();
        Row(parent, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T(label), Width = 130, VerticalAlignment = VerticalAlignment.Center },
                slider,
                readout,
            },
        }, applies);
        _sliders.Add((slider, set));
        _fallbacks.Add(fallback);
        return slider;
    }

    /// <summary>Back to the look Dither opens with.</summary>
    private void Restore(DitherSettings defaults)
    {
        for (var i = 0; i < _sliders.Count; i++) _sliders[i].Slider.Value = _fallbacks[i];
        _style.SelectedIndex = 0;
        _shape.SelectedIndex = 0;
        _colors.SelectedIndex = 0;
        _lightOnDark.IsChecked = defaults.LightOnDark;
        _characters.Text = defaults.Characters;
        foreach (var (swatch, set, fallback) in _swatches)
        {
            swatch.Colour = fallback;
            set(_amounts, fallback);
        }
        Refresh();
        RefreshPreview();
    }

    /// <summary>The look the panel has chosen, which is what its item stands for rather than its index. The
    /// self check reads it to see which look the panel opened on.</summary>
    internal DitherStyle Style()
    {
        var index = _style.SelectedIndex;
        return index >= 0 && index < _styleRows.Count && _styleRows[index] is { } style ? style : DitherStyle.Atkinson;
    }

    /// <summary>The amounts as the panel has them, for a preview of what they would do. The self check reads
    /// them to see which amounts the panel opened with.</summary>
    internal DitherSettings Current()
    {
        var settings = _amounts;
        settings.PixelShape = (DitherPixelShape)Math.Max(0, _shape.SelectedIndex);
        settings.Colors = (DitherColors)Math.Max(0, _colors.SelectedIndex);
        settings.LightOnDark = _lightOnDark.IsChecked == true;
        settings.Characters = _characters.Text ?? DitherSettings.DefaultCharacters;
        foreach (var (slider, set) in _sliders) set(settings, slider.Value);
        foreach (var (swatch, set, _) in _swatches) set(settings, swatch.Colour);
        return settings;
    }

    private void Accept()
    {
        _result = Current();
        Close();
    }

    /// <summary>
    /// The panel's body, built but not shown, for the self check: a dialog cannot be shown without a pointer,
    /// and building it is what runs its gating — every row is asked whether the look applies to it as it is
    /// added, so a row that would do nothing for the look is hidden before the panel is ever on a screen.
    /// </summary>
    internal static Control Body(DitherStyle style, DitherSettings start)
    {
        var dialog = new DitherDialog(style, start);
        var body = (Control)dialog.Content!;
        dialog.Content = null;
        return body;
    }

    /// <summary>
    /// The look and its amounts, or null when the panel was dismissed. It opens on the look and amounts it is
    /// given, which are the ones this panel was last used with.
    /// </summary>
    public static async Task<(DitherStyle Style, DitherSettings Settings)?> Ask(Window owner, DitherStyle style,
        DitherSettings start, Action<DitherStyle, DitherSettings>? preview = null)
    {
        var dialog = new DitherDialog(style, start) { Preview = preview };
        var closed = dialog.ShowDialog(owner);
        dialog.RefreshPreview();
        await closed;
        if (dialog._result is not { } settings) return null;
        return (dialog.Style(), settings);
    }
}
