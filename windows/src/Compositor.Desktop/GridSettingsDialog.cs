using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// View ▸ Grid Settings: how far apart the major lines are and how finely each square is split. Avalonia
/// ships no such dialog, so this is one.
/// </summary>
internal sealed class GridSettingsDialog : DialogWindow
{
    private readonly TextBox _spacing;
    private readonly TextBox _subdivisions;
    private LayoutGrid? _result;

    /// <summary>
    /// The body this dialog is made of, handed over and let go of, for the colour check — a control can only
    /// be drawn once it has no window of its own holding it.
    /// </summary>
    internal Control TakeBody()
    {
        var body = (Control)Content!;
        Content = null;
        return body;
    }

    internal GridSettingsDialog(LayoutGrid start)
    {
        Title = L10n.T("Grid Settings");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _spacing = new TextBox { Text = start.Spacing.ToString(), Width = 100 };
        _subdivisions = new TextBox { Text = start.Subdivisions.ToString(), Width = 100 };

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
                Row("Spacing, pixels", _spacing),
                Row("Subdivisions", _subdivisions),
                new TextBlock
                {
                    Text = L10n.F("Between {0} and {1} pixels apart, split into at most {2}.",
                        LayoutGrid.LeastSpacing, LayoutGrid.MostSpacing, LayoutGrid.MostSubdivisions),
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
    }

    private static Control Row(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = L10n.T(label), Width = 130, VerticalAlignment = VerticalAlignment.Center },
            control,
        },
    };

    private void Accept()
    {
        if (!int.TryParse(_spacing.Text, out var spacing) || !int.TryParse(_subdivisions.Text, out var subdivisions)) return;
        _result = new LayoutGrid(spacing, subdivisions);
        Close();
    }

    /// <summary>The grid as it was set, or null when the dialog was dismissed.</summary>
    public static async Task<LayoutGrid?> Ask(Window owner, LayoutGrid start)
    {
        var dialog = new GridSettingsDialog(start);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
