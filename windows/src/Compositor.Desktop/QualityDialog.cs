using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Core.IO;

namespace Compositor.Desktop;

/// <summary>
/// The quality a JPEG is written at, as one slider. Avalonia ships no such dialog, so this is one.
/// </summary>
internal sealed class QualityDialog : DialogWindow
{
    private readonly Slider _quality = new() { Minimum = 1, Maximum = 100, Value = ImageWriter.DefaultQuality, Width = 220 };
    private int? _result;

    private QualityDialog()
    {
        Title = L10n.T("JPEG Quality");
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var readout = new TextBlock { Text = ((int)_quality.Value).ToString(), Width = 44, VerticalAlignment = VerticalAlignment.Center };
        _quality.PropertyChanged += (_, change) =>
        {
            if (change.Property == Slider.ValueProperty) readout.Text = ((int)_quality.Value).ToString();
        };

        var ok = new Button { Content = L10n.T("Export"), IsDefault = true };
        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        ok.Click += (_, _) =>
        {
            _result = (int)Math.Clamp(_quality.Value, 1, 100);
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L10n.T("Higher keeps more of the picture and makes a larger file.") },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = L10n.T("Quality"), Width = 70, VerticalAlignment = VerticalAlignment.Center },
                        _quality,
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

    /// <summary>The quality to write at, or null when the dialog was dismissed.</summary>
    public static async Task<int?> Ask(Window owner)
    {
        var dialog = new QualityDialog();
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
