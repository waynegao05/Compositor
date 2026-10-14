using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Compositor.Desktop;

/// <summary>
/// A question with two answers, for the places where something would be thrown away. Avalonia ships no such
/// dialog, so this is one: the answer is false when it is dismissed by the window's own close button.
/// </summary>
internal sealed class ConfirmDialog : DialogWindow
{
    private bool _answered;

    private ConfirmDialog(string title, string message, string yes, string no)
    {
        Title = L10n.T(title);
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var keep = new Button { Content = L10n.T(no), IsCancel = true };
        var go = new Button { Content = L10n.T(yes), IsDefault = false };
        keep.Click += (_, _) => Close();
        go.Click += (_, _) =>
        {
            _answered = true;
            Close();
        };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = L10n.T(message), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { keep, go },
                },
            },
        };
        Opened += (_, _) => keep.Focus();
    }

    /// <summary>Whether the first answer was given: closing the dialog without choosing is the second one.</summary>
    public static async Task<bool> Ask(Window owner, string title, string message, string yes, string no)
    {
        var dialog = new ConfirmDialog(title, message, yes, no);
        await dialog.ShowDialog(owner);
        return dialog._answered;
    }
}
