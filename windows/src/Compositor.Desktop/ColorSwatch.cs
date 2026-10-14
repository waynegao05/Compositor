using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Compositor.Desktop;

/// <summary>
/// A colour drawn as a swatch, in the button that opens the picker on it: the colour, a white line inside it
/// and a black line round it, which is how the Mac's sheets draw a colour they can be told. A sheet uses one
/// where it used to offer numbers, and the click opens the app's own picker, as the Mac's does.
/// </summary>
internal sealed class ColorSwatch : Button
{
    /// <summary>The width of a swatch, which is the Mac sheet's 24 points.</summary>
    private const double Side = 24;

    private readonly SwatchFace _face = new();

    public ColorSwatch(string hint)
    {
        Content = _face;
        Width = Height = Side;
        Padding = new Thickness(0);
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ToolTip.SetTip(this, L10n.T(hint));
    }

    /// <summary>The colour it shows, a channel each from 0 to 1.</summary>
    public (double Red, double Green, double Blue) Colour
    {
        get => _face.Colour;
        set
        {
            _face.Colour = value;
            _face.InvalidateVisual();
        }
    }

    /// <summary>One colour, drawn the Mac's way: the fill, a white line inside it and a black line on the edge.</summary>
    private sealed class SwatchFace : Control
    {
        public (double Red, double Green, double Blue) Colour { get; set; }

        public override void Render(DrawingContext context)
        {
            var box = new Rect(Bounds.Size);
            var shape = new RoundedRect(box, 6);
            context.DrawRectangle(new SolidColorBrush(Channel(Colour.Red, Colour.Green, Colour.Blue)), null, shape);
            context.DrawRectangle(null, new Pen(Brushes.White, 1.5), new RoundedRect(box.Deflate(1), 5));
            context.DrawRectangle(null, new Pen(Brushes.Black, 1), shape);
        }
    }

    /// <summary>A colour as Skia's brush wants it, at the eight bits a channel that a swatch shows.</summary>
    internal static Color Channel(double red, double green, double blue) => Color.FromRgb(
        (byte)Math.Clamp(Math.Round(red * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(green * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(blue * 255), 0, 255));
}

/// <summary>
/// What a gradient runs between, drawn as the Mac's Gradient Map sheet draws it: the two ends side by side in
/// one bar, so what the two swatches under it are making can be seen without applying it.
/// </summary>
internal sealed class ColorStrip : Control
{
    /// <summary>The colour at each end, a channel each from 0 to 1.</summary>
    public (double Red, double Green, double Blue) From { get; set; }

    public (double Red, double Green, double Blue) To { get; set; } = (1, 1, 1);

    /// <summary>Says the two colours have moved, so the bar is drawn again.</summary>
    public void Redraw() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var shape = new RoundedRect(new Rect(Bounds.Size), 4);
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ColorSwatch.Channel(From.Red, From.Green, From.Blue), 0),
                new GradientStop(ColorSwatch.Channel(To.Red, To.Green, To.Blue), 1),
            },
        }, null, shape);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.Black, 0.35), 1), shape);
    }
}
