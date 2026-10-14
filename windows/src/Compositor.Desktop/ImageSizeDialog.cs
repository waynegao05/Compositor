using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Desktop;

/// <summary>
/// Image ▸ Image Size: the whole picture resampled, with the resolution it is to be measured at. Avalonia
/// ships no such dialog, so this is one.
/// </summary>
internal sealed class ImageSizeDialog : DialogWindow
{
    private readonly TextBox _width;
    private readonly TextBox _height;
    private readonly TextBox _resolution;
    private readonly CheckBox _constrain = new() { Content = L10n.T("Constrain proportions"), IsChecked = true };
    private readonly ComboBox _sampling = new();
    private readonly double _aspect;
    private bool _updating;
    private (int Width, int Height, double Resolution, LayerSampling Sampling)? _result;

    private ImageSizeDialog(int width, int height, double resolution, LayerSampling sampling)
    {
        Title = L10n.T("Image Size");
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _aspect = height == 0 ? 1 : (double)width / height;
        _width = new TextBox { Text = width.ToString(), Width = 100 };
        _height = new TextBox { Text = height.ToString(), Width = 100 };
        _resolution = new TextBox { Text = resolution.ToString("0.##"), Width = 100 };
        _sampling.ItemsSource = new[] { "Bicubic (best for pictures)", "Nearest (best for pixel art)" }.Select(L10n.T).ToArray();
        _sampling.SelectedIndex = sampling == LayerSampling.Nearest ? 1 : 0;
        _sampling.Width = 240;

        // The two sizes stay in step while the box is ticked, each following the one just typed in.
        _width.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            Relink(fromWidth: true);
        };
        _height.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            Relink(fromWidth: false);
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
                Row("Width, pixels", _width),
                Row("Height, pixels", _height),
                _constrain,
                Row("Resolution, per inch", _resolution),
                Row("Resampling", _sampling),
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
            new TextBlock { Text = L10n.T(label), Width = 140, VerticalAlignment = VerticalAlignment.Center },
            control,
        },
    };

    /// <summary>The other side follows the one that was typed in, while proportions are held.</summary>
    private void Relink(bool fromWidth)
    {
        if (_updating || _constrain.IsChecked != true) return;
        _updating = true;
        try
        {
            if (fromWidth && int.TryParse(_width.Text, out var width) && width > 0)
            {
                _height.Text = Math.Max(1, (int)Math.Round(width / _aspect)).ToString();
            }
            else if (!fromWidth && int.TryParse(_height.Text, out var height) && height > 0)
            {
                _width.Text = Math.Max(1, (int)Math.Round(height * _aspect)).ToString();
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void Accept()
    {
        if (!int.TryParse(_width.Text, out var width) || !int.TryParse(_height.Text, out var height)) return;
        if (width is < 1 or > DocumentLimits.MaxSide || height is < 1 or > DocumentLimits.MaxSide) return;
        if (!double.TryParse(_resolution.Text, out var resolution) || !double.IsFinite(resolution)
            || resolution is < 1 or > 9600)
        {
            return;
        }
        _result = (width, height, resolution, _sampling.SelectedIndex == 1 ? LayerSampling.Nearest : LayerSampling.HighQuality);
        Close();
    }

    /// <summary>What was asked for, or null when the dialog was dismissed or the numbers made no sense.</summary>
    public static async Task<(int Width, int Height, double Resolution, LayerSampling Sampling)?> Ask(
        Window owner, int width, int height, double resolution, LayerSampling sampling)
    {
        var dialog = new ImageSizeDialog(width, height, resolution, sampling);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
