using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Format;

namespace Compositor.Desktop;

/// <summary>
/// One of the effects a layer draws around itself. The menu opens this with the effect's name, and its rows
/// are the amounts that effect reads; ticked off it takes the effect away. Avalonia ships no such dialog, so
/// this is one.
/// </summary>
internal sealed class EffectDialog : DialogWindow
{
    private readonly List<(Slider Slider, Action<object, double> Set)> _rows = [];
    private readonly List<double> _fallbacks = [];
    private readonly CheckBox _on = new() { Content = L10n.T("Draw this effect") };
    private readonly CheckBox? _inside;
    private readonly EffectKind _kind;
    private LayerEffects? _result;

    private EffectDialog(EffectKind kind, LayerEffects? start)
    {
        _kind = kind;
        Title = L10n.T(TitleFor(kind));
        Width = 440;
        Height = 520;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var group = new StackPanel { Margin = new Thickness(16), Spacing = 4 };
        var current = start;
        switch (kind)
        {
            case EffectKind.Stroke:
            {
                var stroke = current?.Stroke ?? new StrokeEffect();
                _on.IsChecked = current?.Stroke is { IsEnabled: true };
                Add(group, "Size, pixels", 0, StrokeEffect.MaxSize, stroke.Size, 4, (s, v) => ((StrokeEffect)s).Size = v, "0");
                Colour(group, "Color", stroke.Red, stroke.Green, stroke.Blue,
                    (effect, r, g, b) => { var target = (StrokeEffect)effect; target.Red = r; target.Green = g; target.Blue = b; });
                Add(group, "Opacity", 0, 1, stroke.Opacity, 1, (s, v) => ((StrokeEffect)s).Opacity = v, "0.00");
                _inside = new CheckBox { Content = L10n.T("Inside the edge"), IsChecked = stroke.Inside };
                group.Children.Add(_inside);
                break;
            }
            case EffectKind.DropShadow:
            {
                var shadow = current?.Shadow ?? new ShadowEffect();
                _on.IsChecked = current?.Shadow is { IsEnabled: true };
                Add(group, "Angle, degrees", -360, 360, shadow.Angle, 90, (s, v) => ((ShadowEffect)s).Angle = v);
                Add(group, "Distance, pixels", 0, 5000, shadow.Distance, 20, (s, v) => ((ShadowEffect)s).Distance = v, "0");
                Add(group, "Blur, pixels", 0, 500, shadow.Blur, 20, (s, v) => ((ShadowEffect)s).Blur = v);
                Colour(group, "Color", shadow.Red, shadow.Green, shadow.Blue,
                    (effect, r, g, b) => { var target = (ShadowEffect)effect; target.Red = r; target.Green = g; target.Blue = b; });
                Add(group, "Opacity", 0, 1, shadow.Opacity, 0.5, (s, v) => ((ShadowEffect)s).Opacity = v, "0.00");
                break;
            }
            case EffectKind.ColorOverlay:
            {
                var overlay = current?.ColorOverlay ?? new ColorOverlayEffect();
                _on.IsChecked = current?.ColorOverlay is { IsEnabled: true };
                Colour(group, "Color", overlay.Red, overlay.Green, overlay.Blue,
                    (effect, r, g, b) => { var target = (ColorOverlayEffect)effect; target.Red = r; target.Green = g; target.Blue = b; });
                Add(group, "Opacity", 0, 1, overlay.Opacity, 1, (s, v) => ((ColorOverlayEffect)s).Opacity = v, "0.00");
                break;
            }
            case EffectKind.InnerShadow:
            {
                var inner = current?.InnerShadow ?? new InnerShadowEffect();
                _on.IsChecked = current?.InnerShadow is { IsEnabled: true };
                Add(group, "Angle, degrees", -360, 360, inner.Angle, 90, (s, v) => ((InnerShadowEffect)s).Angle = v);
                Add(group, "Distance, pixels", 0, 5000, inner.Distance, 10, (s, v) => ((InnerShadowEffect)s).Distance = v, "0");
                Add(group, "Blur, pixels", 0, 500, inner.Blur, 10, (s, v) => ((InnerShadowEffect)s).Blur = v);
                Colour(group, "Color", inner.Red, inner.Green, inner.Blue,
                    (effect, r, g, b) => { var target = (InnerShadowEffect)effect; target.Red = r; target.Green = g; target.Blue = b; });
                Add(group, "Opacity", 0, 1, inner.Opacity, 0.5, (s, v) => ((InnerShadowEffect)s).Opacity = v, "0.00");
                break;
            }
            case EffectKind.OuterGlow:
            {
                var glow = current?.OuterGlow ?? new OuterGlowEffect();
                _on.IsChecked = current?.OuterGlow is { IsEnabled: true };
                Add(group, "Size, pixels", 0, 500, glow.Size, 20, (s, v) => ((OuterGlowEffect)s).Size = v);
                Colour(group, "Color", glow.Red, glow.Green, glow.Blue,
                    (effect, r, g, b) => { var target = (OuterGlowEffect)effect; target.Red = r; target.Green = g; target.Blue = b; });
                Add(group, "Opacity", 0, 1, glow.Opacity, 0.75, (s, v) => ((OuterGlowEffect)s).Opacity = v, "0.00");
                break;
            }
            default:
            {
                var glow = current?.InnerGlow ?? new InnerGlowEffect();
                _on.IsChecked = current?.InnerGlow is { IsEnabled: true };
                Add(group, "Size, pixels", 0, 500, glow.Size, 10, (s, v) => ((InnerGlowEffect)s).Size = v);
                Colour(group, "Color", glow.Red, glow.Green, glow.Blue,
                    (effect, r, g, b) => { var target = (InnerGlowEffect)effect; target.Red = r; target.Green = g; target.Blue = b; });
                Add(group, "Opacity", 0, 1, glow.Opacity, 0.75, (s, v) => ((InnerGlowEffect)s).Opacity = v, "0.00");
                break;
            }
        }

        var ok = new Button { Content = L10n.T("Apply"), IsDefault = true };
        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        var remove = new Button { Content = L10n.T("Remove") };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        remove.Click += (_, _) =>
        {
            _on.IsChecked = false;
            Accept();
        };

        var top = new StackPanel { Margin = new Thickness(16, 16, 16, 0), Children = { _on } };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 8, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { remove, cancel, ok },
        };
        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(buttons);
        body.Children.Add(new ScrollViewer { Content = group });
        Content = body;
    }

    /// <summary>What the menu calls each effect, as the Mac build's Effects menu does.</summary>
    public static string TitleFor(EffectKind kind) => kind switch
    {
        EffectKind.Stroke => "Stroke",
        EffectKind.DropShadow => "Drop Shadow",
        EffectKind.ColorOverlay => "Color Overlay",
        EffectKind.InnerShadow => "Inner Shadow",
        EffectKind.OuterGlow => "Outer Glow",
        _ => "Inner Glow",
    };

    /// <summary>Three rows, one channel each, all writing into the same effect.</summary>
    private void Colour(StackPanel parent, string label, double red, double green, double blue,
        Action<object, double, double, double> set)
    {
        var values = new[] { red, green, blue };
        foreach (var (channel, index) in new[] { ("Red", 0), ("Green", 1), ("Blue", 2) })
        {
            var at = index;
            Add(parent, L10n.F("{0}: {1}", L10n.T(label), L10n.T(channel)), 0, 1, values[index], values[index],
                (effect, value) =>
                {
                    values[at] = value;
                    set(effect, values[0], values[1], values[2]);
                }, "0.00");
        }
    }

    /// <summary>
    /// A row that writes its value onto the effect being built. The effect is built fresh as the rows run, so
    /// the rows are handed the object its own kind makes.
    /// </summary>
    private void Add(StackPanel parent, string label, double least, double most, double value, double fallback,
        Action<object, double> set, string format = "0.#")
    {
        var slider = new Slider { Minimum = least, Maximum = most, Value = value, Width = 210 };
        var readout = new TextBlock { Text = "", Width = 44, VerticalAlignment = VerticalAlignment.Center };
        void Show() => readout.Text = slider.Value.ToString(format);
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            Show();
        };
        Show();
        parent.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T(label), Width = 140, VerticalAlignment = VerticalAlignment.Center },
                slider,
                readout,
            },
        });
        _rows.Add((slider, set));
        _fallbacks.Add(fallback);
    }

    /// <summary>
    /// What the panel says, as a bag holding this kind's effect and nothing else: the edit takes this kind's
    /// slot out of it and leaves the layer's other effects where they were, so an empty bag means "take this
    /// one away".
    /// </summary>
    private void Accept()
    {
        var bag = new LayerEffects();
        if (_on.IsChecked == true)
        {
            var effect = New();
            foreach (var (slider, set) in _rows) set(effect, slider.Value);
            if (_inside is { IsChecked: true }) ((StrokeEffect)effect).Inside = true;
            switch (_kind)
            {
                case EffectKind.Stroke: bag.Stroke = (StrokeEffect)effect; break;
                case EffectKind.DropShadow: bag.Shadow = (ShadowEffect)effect; break;
                case EffectKind.ColorOverlay: bag.ColorOverlay = (ColorOverlayEffect)effect; break;
                case EffectKind.InnerShadow: bag.InnerShadow = (InnerShadowEffect)effect; break;
                case EffectKind.OuterGlow: bag.OuterGlow = (OuterGlowEffect)effect; break;
                default: bag.InnerGlow = (InnerGlowEffect)effect; break;
            }
        }
        _result = bag;
        Close();
    }

    /// <summary>This kind's effect, all defaults, for the rows to write onto.</summary>
    private object New() => _kind switch
    {
        EffectKind.Stroke => new StrokeEffect(),
        EffectKind.DropShadow => new ShadowEffect(),
        EffectKind.ColorOverlay => new ColorOverlayEffect(),
        EffectKind.InnerShadow => new InnerShadowEffect(),
        EffectKind.OuterGlow => new OuterGlowEffect(),
        _ => new InnerGlowEffect(),
    };

    /// <summary>The effects to put on the layer, or null when the dialog was dismissed.</summary>
    public static async Task<LayerEffects?> Ask(Window owner, EffectKind kind, LayerEffects? start)
    {
        var dialog = new EffectDialog(kind, start);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
