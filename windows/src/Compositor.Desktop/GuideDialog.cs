using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Core.Format;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// View ▸ New Guide: which way the line runs and where it sits. Avalonia ships no such dialog, so this is one.
/// </summary>
internal sealed class GuideDialog : DialogWindow
{
    private readonly ComboBox _axis = new();
    private readonly TextBox _position;
    private readonly TextBlock _error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private (GuideAxis Axis, double Position)? _result;

    private GuideDialog(int width, int height)
    {
        Title = L10n.T("New Guide");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _axis.ItemsSource = new[] { "Horizontal (across)", "Vertical (up and down)" }.Select(L10n.T).ToArray();
        _axis.SelectedIndex = 0;
        _axis.Width = 180;
        _position = new TextBox { Text = (height / 2).ToString(), Width = 100 };

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
                Row("Direction", _axis),
                Row("Position, pixels", _position),
                _error,
                new TextBlock
                {
                    Text = L10n.F("The canvas is {0} x {1}. A guide may sit outside it, out in the pasteboard.", width, height),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
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
        Opened += (_, _) =>
        {
            _position.Focus();
            _position.SelectAll();
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
        if (!double.TryParse(_position.Text, out var position) || !GuideEdits.Allowed(position))
        {
            _error.Text = L10n.T("Enter a finite position between -1,000,000 and 1,000,000 pixels.");
            return;
        }
        // A guide across the canvas is a horizontal one, so the first entry is the horizontal axis.
        var axis = _axis.SelectedIndex == 1 ? GuideAxis.Vertical : GuideAxis.Horizontal;
        _result = (axis, position);
        Close();
    }

    /// <summary>What was asked for, or null when the dialog was dismissed or the number made no sense.</summary>
    public static async Task<(GuideAxis Axis, double Position)?> Ask(Window owner, int width, int height)
    {
        var dialog = new GuideDialog(width, height);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
