using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Core.Document;
using Compositor.Core.Model;

namespace Compositor.Desktop;

/// <summary>
/// Image ▸ Canvas Size: the canvas in pixels, and which corner or edge the picture keeps. Avalonia ships no
/// such dialog, so this is one.
/// </summary>
internal sealed class CanvasSizeDialog : DialogWindow
{
    private readonly TextBox _width;
    private readonly TextBox _height;
    private readonly ComboBox _anchor = new();
    private (int Width, int Height, int Anchor)? _result;

    private CanvasSizeDialog(int width, int height, int anchor)
    {
        Title = L10n.T("Canvas Size");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _width = new TextBox { Text = width.ToString(), Width = 100 };
        _height = new TextBox { Text = height.ToString(), Width = 100 };
        // Row-major from the top left, the order CanvasEdits numbers its anchors in.
        _anchor.ItemsSource = new[]
        {
            "Top left", "Top", "Top right",
            "Left", "Centre", "Right",
            "Bottom left", "Bottom", "Bottom right",
        }.Select(L10n.T).ToArray();
        _anchor.SelectedIndex = Math.Clamp(anchor, 0, 8);
        _anchor.Width = 140;

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
                Row("Width, pixels", _width),
                Row("Height, pixels", _height),
                Row("Anchor", _anchor),
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
        Opened += (_, _) =>
        {
            _width.Focus();
            _width.SelectAll();
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
        if (!int.TryParse(_width.Text, out var width) || !int.TryParse(_height.Text, out var height)) return;
        if (width is < 1 or > DocumentLimits.MaxSide || height is < 1 or > DocumentLimits.MaxSide) return;
        _result = (width, height, Math.Max(0, _anchor.SelectedIndex));
        Close();
    }

    /// <summary>What was asked for, or null when the dialog was dismissed or the numbers made no sense.</summary>
    public static async Task<(int Width, int Height, int Anchor)?> Ask(Window owner, int width, int height, int anchor)
    {
        var dialog = new CanvasSizeDialog(width, height, anchor);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
