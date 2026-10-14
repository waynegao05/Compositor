using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// Image ▸ Trim: which edges to take away, and what counts as border. Avalonia ships no such dialog, so this
/// is one.
/// </summary>
internal sealed class TrimDialog : DialogWindow
{
    private readonly ComboBox _basedOn = new();
    private readonly CheckBox _top = new() { Content = L10n.T("Top") };
    private readonly CheckBox _bottom = new() { Content = L10n.T("Bottom") };
    private readonly CheckBox _left = new() { Content = L10n.T("Left") };
    private readonly CheckBox _right = new() { Content = L10n.T("Right") };
    private readonly Slider _tolerance = new() { Minimum = 0, Maximum = 255, Width = 200 };
    private TrimOptions? _result;

    private TrimDialog(TrimOptions start)
    {
        Title = L10n.T("Trim");
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _basedOn.ItemsSource = new[] { "Transparent pixels", "Top-left pixel color", "Bottom-right pixel color" }.Select(L10n.T).ToArray();
        _basedOn.SelectedIndex = (int)start.BasedOn;
        _basedOn.Width = 200;
        _top.IsChecked = start.Top;
        _bottom.IsChecked = start.Bottom;
        _left.IsChecked = start.Left;
        _right.IsChecked = start.Right;
        _tolerance.Value = start.Tolerance;
        var readout = new TextBlock { Text = start.Tolerance.ToString(), Width = 40, VerticalAlignment = VerticalAlignment.Center };
        _tolerance.PropertyChanged += (_, change) =>
        {
            if (change.Property == Slider.ValueProperty) readout.Text = ((int)_tolerance.Value).ToString();
        };

        var ok = new Button { Content = L10n.T("OK"), IsDefault = true };
        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                Row("Based on", _basedOn),
                new TextBlock { Text = L10n.T("Trim away") },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children = { _top, _bottom, _left, _right },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = L10n.T("Tolerance"), Width = 120, VerticalAlignment = VerticalAlignment.Center },
                        _tolerance,
                        readout,
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };
    }

    private static Control Row(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = L10n.T(label), Width = 120, VerticalAlignment = VerticalAlignment.Center },
            control,
        },
    };

    private void Accept()
    {
        _result = new TrimOptions(
            (TrimBasedOn)Math.Max(0, _basedOn.SelectedIndex),
            _top.IsChecked == true,
            _bottom.IsChecked == true,
            _left.IsChecked == true,
            _right.IsChecked == true,
            (byte)Math.Clamp((int)_tolerance.Value, 0, 255));
        Close();
    }

    /// <summary>What was asked for, or null when the dialog was dismissed.</summary>
    public static async Task<TrimOptions?> Ask(Window owner, TrimOptions start)
    {
        var dialog = new TrimDialog(start);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
