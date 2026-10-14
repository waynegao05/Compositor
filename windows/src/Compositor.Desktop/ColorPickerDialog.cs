using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// The app's colour picker, as the Mac build's sheet has it: a saturation and brightness field, a hue strip,
/// the new colour, and its red, green and blue amounts and hex digits. It is a window of its own rather than a
/// dialog that takes the editor over, because the Mac's lives in a floating panel: the canvas stays live so a
/// click on it can sample the colour under the pointer, which is what the line under the fields says.
/// </summary>
internal sealed class ColorPickerDialog : DialogWindow
{
    /// <summary>The field's own size in points, which is the Mac sheet's 256.</summary>
    private const double FieldSize = 256;

    private readonly PickerHsb _hsb;
    private readonly SvField _field;
    private readonly HueStrip _hue;
    private readonly Border _preview = new() { Width = 64, Height = 64, CornerRadius = new CornerRadius(5) };
    private readonly NumericUpDown[] _channels = new NumericUpDown[3];
    private readonly TextBox _hex = new() { Width = 84, FontFamily = new FontFamily("Consolas,Menlo,monospace") };
    private bool _showing;
    private bool _done;

    /// <summary>
    /// Every colour the picker is moved to, which is what lets the sheet behind it preview what that colour
    /// would do — the Mac's own swatches preview live while the picker is up.
    /// </summary>
    public event Action<(double Red, double Green, double Blue)>? Moved;

    /// <summary>OK: the colour the picker ended on.</summary>
    public event Action<(double Red, double Green, double Blue)>? Applied;

    /// <summary>Cancel, or the window shut: nothing is to be taken.</summary>
    public event Action? Cancelled;

    /// <summary>
    /// Opens the picker on a colour the panel behind it is already showing, and reports every colour it is
    /// moved to. A panel's swatch is a colour inside that panel's own settings, so the report is what makes
    /// its preview follow — and a Cancel reports the colour it started on, which puts the panel back.
    /// </summary>
    public static async Task Pick(Window owner, string title, (double Red, double Green, double Blue) start,
        Action<(double Red, double Green, double Blue)> changed)
    {
        var picker = new ColorPickerDialog(title, start);
        picker.Moved += changed;
        picker.Cancelled += () => changed(start);
        await picker.ShowDialog(owner);
    }

    public ColorPickerDialog(string title, (double Red, double Green, double Blue) start)
    {
        Title = L10n.T(title);
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _hsb = new PickerHsb(start);
        _field = new SvField(_hsb);
        _field.Changed += Refresh;
        _hue = new HueStrip(_hsb);
        _hue.Changed += Refresh;

        var ok = new Button { Content = L10n.T("OK"), IsDefault = true };
        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        Ok = ok;
        Cancel = cancel;
        ok.Click += (_, _) =>
        {
            _done = true;
            Applied?.Invoke(Colour);
            Close();
        };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { cancel, ok },
        };

        var numbers = new StackPanel { Spacing = 6 };
        var names = new[] { "R", "G", "B" };
        for (var channel = 0; channel < 3; channel++)
        {
            var index = channel;
            var amount = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 255,
                Increment = 1,
                // Wide enough for the three digits and the spinner beside them, which a narrower one squeezes
                // out of sight.
                Width = 112,
                FormatString = "0",
                ClipValueToMinMax = true,
            };
            amount.ValueChanged += (_, _) =>
            {
                if (_showing || amount.Value is not { } value) return;
                // The channel is written into the colour the field is showing, so the other two follow.
                var rgb = Colour;
                var channels = new[] { rgb.Red, rgb.Green, rgb.Blue };
                channels[index] = Math.Clamp((double)value / 255, 0, 1);
                _hsb.SetRgb((channels[0], channels[1], channels[2]));
                Refresh();
            };
            _channels[channel] = amount;
            numbers.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = names[channel], Width = 14, VerticalAlignment = VerticalAlignment.Center },
                    amount,
                },
            });
        }
        _hex.KeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Enter) return;
            CommitHex();
            pressed.Handled = true;
        };
        _hex.LostFocus += (_, _) => CommitHex();
        numbers.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "#", Width = 14, VerticalAlignment = VerticalAlignment.Center },
                _hex,
            },
        });

        var right = new StackPanel
        {
            Width = 180,
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children = { _preview, buttons },
                },
                numbers,
                new TextBlock
                {
                    Text = L10n.T("Click the canvas to sample"),
                    Foreground = Skin.SecondaryBrush,
                    FontSize = 11,
                },
            },
        };

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(20),
            Children = { _field, _hue, right },
        };
        Refresh();
        Closed += (_, _) =>
        {
            if (!_done) Cancelled?.Invoke();
        };
    }

    /// <summary>The colour the picker is on, as the swatch above the fields shows it.</summary>
    internal (double Red, double Green, double Blue) Colour => _hsb.Rgb;

    /// <summary>The field a pointer drags over, and the strip the hue is dragged on: the checks click these.</summary>
    internal Control Field => _field;

    internal Control Hue => _hue;

    /// <summary>The two buttons, which the checks press rather than going through the key table.</summary>
    internal Button Ok { get; }

    internal Button Cancel { get; }

    /// <summary>
    /// Takes a colour sampled from the canvas: the field moves to it and the numbers follow, which is what a
    /// click on the picture does while the picker is up.
    /// </summary>
    public void Sample((double Red, double Green, double Blue) colour)
    {
        _hsb.SetRgb(colour);
        Refresh();
    }

    /// <summary>What the working colour looks like: the field's ring, the strip's arrows, the swatch, the
    /// numbers and the hex digits, all from the one place so they cannot disagree. Whoever asked for the
    /// picker is told what it has moved to.</summary>
    private void Refresh()
    {
        _field.InvalidateVisual();
        _hue.InvalidateVisual();
        var colour = Colour;
        _preview.Background = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Clamp(Math.Round(colour.Red * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(colour.Green * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(colour.Blue * 255), 0, 255)));
        _showing = true;
        try
        {
            var channels = new[] { colour.Red, colour.Green, colour.Blue };
            for (var channel = 0; channel < 3; channel++)
            {
                _channels[channel].Value = (decimal)Math.Clamp(Math.Round(channels[channel] * 255), 0, 255);
            }
            _hex.Text = _hsb.Hex;
        }
        finally
        {
            _showing = false;
        }
        Moved?.Invoke(colour);
    }

    /// <summary>The hex box typed into: the colour follows it, or the box goes back to what the colour is.</summary>
    private void CommitHex()
    {
        if (PickerHsb.FromHex(_hex.Text ?? "") is { } parsed)
        {
            _hsb.SetRgb(parsed);
            Refresh();
            return;
        }
        _showing = true;
        try
        {
            _hex.Text = _hsb.Hex;
        }
        finally
        {
            _showing = false;
        }
    }

    private static void Set((double Red, double Green, double Blue) rgb, int channel, double value)
    {
        // A tuple cannot be written into by index, so each channel is set by name.
        _ = rgb;
        _ = channel;
        _ = value;
    }

    /// <summary>
    /// The saturation and brightness field: white to the pure hue left to right, and transparent to black top
    /// to bottom, with a ring where the colour is.
    /// </summary>
    private sealed class SvField : Control
    {
        private readonly PickerHsb _hsb;

        public SvField(PickerHsb hsb)
        {
            _hsb = hsb;
            Width = FieldSize;
            Height = FieldSize;
        }

        public event Action? Changed;

        public override void Render(DrawingContext context)
        {
            var box = new Rect(Bounds.Size);
            var hue = new PickerHsb(_hsb.Hue, 1, 1).Rgb;
            var pure = new SolidColorBrush(Color.FromRgb(
                (byte)Math.Clamp(Math.Round(hue.Red * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(hue.Green * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(hue.Blue * 255), 0, 255)));
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(pure.Color, 1) },
            }, box);
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 1) },
            }, box);
            var at = new Point(_hsb.Saturation * Bounds.Width, (1 - _hsb.Brightness) * Bounds.Height);
            context.DrawEllipse(null, new Pen(Brushes.White, 1.5), at, 6, 6);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.Black, 0.75), 0.75), at, 6.75, 6.75);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Pointer.Capture(this);
            Take(e.GetPosition(this));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer.Captured != this) return;
            Take(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }

        private void Take(Point at)
        {
            _hsb.Saturation = Math.Clamp(at.X / Math.Max(1, Bounds.Width), 0, 1);
            _hsb.Brightness = 1 - Math.Clamp(at.Y / Math.Max(1, Bounds.Height), 0, 1);
            Changed?.Invoke();
        }
    }

    /// <summary>The hue strip: the wheel from red back to red down the strip, with an arrow each side of where
    /// the hue is, drawn at the top for 360 degrees and the bottom for none, as the Mac's sheet draws it.</summary>
    private sealed class HueStrip : Control
    {
        private readonly PickerHsb _hsb;

        public HueStrip(PickerHsb hsb)
        {
            _hsb = hsb;
            Width = 34;
            Height = FieldSize;
        }

        public event Action? Changed;

        public override void Render(DrawingContext context)
        {
            var strip = new Rect(7, 0, 20, Bounds.Height);
            var stops = new GradientStops();
            for (var step = 0; step <= 6; step++)
            {
                var hue = 360 - step * 60;
                var colour = new PickerHsb(hue, 1, 1).Rgb;
                stops.Add(new GradientStop(Color.FromRgb(
                    (byte)Math.Clamp(Math.Round(colour.Red * 255), 0, 255),
                    (byte)Math.Clamp(Math.Round(colour.Green * 255), 0, 255),
                    (byte)Math.Clamp(Math.Round(colour.Blue * 255), 0, 255)), step / 6.0));
            }
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = stops,
            }, strip);
            var at = (1 - _hsb.Hue / 360) * Bounds.Height;
            var ink = Skin.LabelBrush;
            context.DrawGeometry(ink, null, Arrow(0, at - 5, towardsRight: true));
            context.DrawGeometry(ink, null, Arrow(27, at - 5, towardsRight: false));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Pointer.Capture(this);
            Take(e.GetPosition(this));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer.Captured != this) return;
            Take(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }

        private void Take(Point at)
        {
            _hsb.Hue = (1 - Math.Clamp(at.Y / Math.Max(1, Bounds.Height), 0, 1)) * 360;
            Changed?.Invoke();
        }

        /// <summary>A small triangle pointing at the strip, on one side of it or the other.</summary>
        private static StreamGeometry Arrow(double x, double y, bool towardsRight)
        {
            var geometry = new StreamGeometry();
            using var path = geometry.Open();
            var tip = towardsRight ? x + 7 : x;
            var back = towardsRight ? x : x + 7;
            path.BeginFigure(new Point(back, y), true);
            path.LineTo(new Point(tip, y + 5));
            path.LineTo(new Point(back, y + 10));
            path.EndFigure(true);
            return geometry;
        }
    }
}
