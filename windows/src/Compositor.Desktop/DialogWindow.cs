using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Compositor.Desktop;

/// <summary>
/// What every dialog of this app is: a window on the editor's own chrome. Fluent draws a window's background
/// itself — white in its light palette, black in its dark one — and the Mac has one appearance and paints its
/// dialogs the same 0.14 grey as the editor behind them (ContentView's <c>Color(white: 0.14)</c>), so they all
/// start from here rather than each remembering to say so.
/// </summary>
internal abstract class DialogWindow : Window
{
    protected DialogWindow()
    {
        Background = Skin.ChromeBrush;
        // A plain TextBlock has no foreground of its own, so on a dark dialog it would draw black on near
        // black. Setting the inherited one leaves every control that says what its own text is — a button, a
        // field, a menu row — exactly as the theme drew it.
        TextElement.SetForeground(this, Skin.LabelBrush);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (FocusManager?.GetFocusedElement() is not TextBox box) return;
            // A TextBox leaves native composition keys unhandled. Stop them before they reach a dialog's
            // Enter/Escape handlers or its default/cancel buttons.
            if (e.Key == Key.ImeProcessed || box.GetVisualDescendants().OfType<TextPresenter>()
                .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText)))
            {
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
