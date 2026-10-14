using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Compositor.Core.Document;
using SkiaSharp;

namespace Compositor.Desktop;

/// <summary>
/// Select ▸ Colour Range, as the Mac build presents it: a panel of its own with the eyedroppers, the selection
/// drawn small in black and white, Fuzziness and Invert — and the picture live behind it, because a colour is
/// picked by clicking the picture rather than by typing it. It is a window of its own rather than a dialog that
/// takes the editor over, since a modal one could not be clicked through to the canvas.
/// </summary>
internal sealed class ColorRangePanel : DialogWindow
{
    private readonly ColorRangeSession _session;
    private readonly Dictionary<ColorRangeSession.Picking, Button> _modes = [];
    private readonly MaskView _mask = new();
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _problem = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
    private readonly Slider _fuzziness;
    private readonly TextBlock _readout = new() { Width = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _invert = new() { Content = L10n.T("Invert") };
    private bool _showing;
    private bool _done;

    /// <summary>One of the panel's own controls moved: the selection is built again from what they say.</summary>
    public event Action? Changed;

    /// <summary>OK: the selection as it stands is what was wanted.</summary>
    public event Action? Applied;

    /// <summary>Cancel, or the window shut: the selection there was is to be put back.</summary>
    public event Action? Cancelled;

    public ColorRangePanel(ColorRangeSession session)
    {
        _session = session;
        Title = L10n.T("Color Range");
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var mode in Enum.GetValues<ColorRangeSession.Picking>())
        {
            var button = new Button { Content = L10n.T(mode.ToString()) };
            var picked = mode;
            button.Click += (_, _) =>
            {
                session.Mode = picked;
                Lit();
            };
            ToolTip.SetTip(button, L10n.T(mode switch
            {
                ColorRangeSession.Picking.Replace => "Click the picture to select that color",
                ColorRangeSession.Picking.Add => "Click the picture to add that color to the selection",
                _ => "Click the picture to take that color out of the selection",
            }));
            _modes[mode] = button;
            modes.Children.Add(button);
        }

        _fuzziness = new Slider
        {
            Minimum = 0,
            Maximum = 200,
            Value = session.Fuzziness,
            Width = 190,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fuzziness.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty || _showing) return;
            session.Fuzziness = _fuzziness.Value;
            _readout.Text = $"{session.Fuzziness:0}";
            Changed?.Invoke();
        };
        _readout.Text = $"{session.Fuzziness:0}";
        _invert.IsChecked = session.Invert;
        _invert.IsCheckedChanged += (_, _) =>
        {
            if (_showing) return;
            session.Invert = _invert.IsChecked == true;
            Changed?.Invoke();
        };

        var ok = new Button { Content = L10n.T("OK"), IsDefault = true };
        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        ok.Click += (_, _) =>
        {
            _done = true;
            Applied?.Invoke();
            Close();
        };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                modes,
                _mask,
                _hint,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = L10n.T("Fuzziness"), Width = 70, VerticalAlignment = VerticalAlignment.Center },
                        _fuzziness,
                        _readout,
                    },
                },
                _invert,
                _problem,
                buttons,
            },
        };
        Lit();
        Closed += (_, _) =>
        {
            // The preview is the panel's own bitmap, so it goes with it rather than waiting for the collector.
            _mask.Show(null);
            if (!_done) Cancelled?.Invoke();
        };
    }

    /// <summary>Whether the panel is drawing a selection at all, which the self check reads.</summary>
    internal bool ShowingMask { get; private set; }

    /// <summary>The colour the panel has been told to show as its preview, and what to say about the picks.</summary>
    internal void Showing(SKBitmap? mask, int picked, int taken)
    {
        ShowingMask = mask is not null;
        _mask.Show(mask);
        _hint.Text = picked == 0
            ? L10n.T("Click the picture to pick the color to select.")
            : L10n.F("Shift-click adds a color, Alt-click takes one away. {0} color(s) picked, {1} taken away.", picked, taken);
        _problem.Text = L10n.T(_session.Problem ?? "");
        _showing = true;
        try
        {
            _fuzziness.Value = _session.Fuzziness;
            _readout.Text = $"{_session.Fuzziness:0}";
            _invert.IsChecked = _session.Invert;
        }
        finally
        {
            _showing = false;
        }
        Lit();
    }

    /// <summary>Lights the eyedropper the next click will use, as the Mac's panel highlights it.</summary>
    private void Lit()
    {
        foreach (var (mode, button) in _modes)
        {
            button.Background = mode == _session.Mode ? Skin.TabFront : Brushes.Transparent;
        }
    }

    /// <summary>The panel's own button for one of its eyedroppers, which the self check presses.</summary>
    internal Button ModeButton(ColorRangeSession.Picking mode) => _modes[mode];

    /// <summary>
    /// The selection drawn small: white where it holds, black where it does not, in the panel's own box. A
    /// bitmap is drawn rather than a control's fill, so the mask the core built is what is on screen.
    /// </summary>
    private sealed class MaskView : Control
    {
        private SKBitmap? _mask;
        private WriteableBitmap? _image;

        public MaskView() => Height = ColorRangeSession.MaskHeight;

        public void Show(SKBitmap? mask)
        {
            _mask?.Dispose();
            _image?.Dispose();
            _mask = mask;
            _image = mask is null ? null : ToImage(mask);
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var box = new Rect(Bounds.Size);
            context.FillRectangle(Brushes.Black, box);
            if (_image is { } image)
            {
                var scale = Math.Min(box.Width / image.PixelSize.Width, box.Height / image.PixelSize.Height);
                var width = image.PixelSize.Width * scale;
                var height = image.PixelSize.Height * scale;
                var at = new Rect((box.Width - width) / 2, (box.Height - height) / 2, width, height);
                context.DrawImage(image, new Rect(0, 0, image.PixelSize.Width, image.PixelSize.Height), at);
            }
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.White, 0.2), 1), box);
        }

        /// <summary>The mask as the screen wants it: its gray, opaque, in the order a screen's pixels are in.</summary>
        private static WriteableBitmap ToImage(SKBitmap mask)
        {
            var target = new WriteableBitmap(new PixelSize(mask.Width, mask.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var locked = target.Lock())
            {
                var pixels = mask.GetPixelSpan();
                unsafe
                {
                    var start = (byte*)locked.Address;
                    for (var y = 0; y < mask.Height; y++)
                    {
                        var from = pixels.Slice(y * mask.Width, mask.Width);
                        var to = new Span<byte>(start + y * locked.RowBytes, mask.Width * 4);
                        for (var x = 0; x < mask.Width; x++)
                        {
                            to[x * 4] = from[x];
                            to[x * 4 + 1] = from[x];
                            to[x * 4 + 2] = from[x];
                            to[x * 4 + 3] = 255;
                        }
                    }
                }
            }
            return target;
        }
    }
}
