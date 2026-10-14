using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Compositor.Core.Document;
using Compositor.Core.IO;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    /// <summary>
    /// The canvas owns editing keys while it holds a text session. Modified keys must not fall through to
    /// image cut, paste, fill, undo or other document commands. Saving remains available through its command
    /// ID, so rebinding a shortcut does not defeat the boundary. Text selection and clipboard editing are
    /// still provided by the Edit Text dialog, not by the inline canvas editor.
    /// </summary>
    private bool ProtectCanvasTextShortcut(KeyEventArgs e)
    {
        if (!_canvas.TextEditing || !_canvas.IsFocused || e.Handled) return false;
        // Leave native composition untouched; CanvasView owns the IME key gate and the committed text.
        if (_canvas.TextComposing || e.Key == Key.ImeProcessed) return true;
        if (e.Key is Key.Escape or Key.Enter or Key.Return or Key.Back or Key.Delete
            or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            return true;
        var held = ShortcutKeys.Held(e.KeyModifiers);
        if (held is ShortcutModifiers.None or ShortcutModifiers.Shift) return true;
        var chord = new ShortcutChord(e.Key.ToString(), held);
        if (!_byKey.TryGetValue(chord, out var id)) return false;
        if (id == $"{Shortcuts.Menus}:Save" || id == $"{Shortcuts.Menus}:Save As") return false;
        e.Handled = true;
        return true;
    }

    /// <summary>Drive the real window/canvas key route, including remapped commands and IME preedit.</summary>
    internal static string TextShortcutSelfCheck()
    {
        var window = new MainWindow();
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
        KeyEventArgs Key(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            window.LastDelivered = null;
            var e = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, KeyModifiers = modifiers };
            window._canvas.RaiseEvent(e);
            return e;
        }
        void Begin()
        {
            window._canvas.Focus();
            window.TypeHere(new SKPoint(10, 10));
            Check(window._canvas.IsFocused && window._text is not null, "Canvas text shortcut fixture has no active text editor.");
        }
        try
        {
            window._shortcutSettings.Overrides.Clear();
            window.ShowKeys();
            var document = LayerPlacement.NewDocument(640, 320)
                ?? throw new InvalidOperationException("Could not create the canvas text shortcut fixture.");
            window._document = document;
            window.Show(window._open);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.SetTool(Tool.Type);
            Begin();
            window.TypedText("ABCD");
            var session = window._text!;
            var count = document.Layers.Count;
            foreach (var (key, modifiers) in new[]
            {
                (Avalonia.Input.Key.A, KeyModifiers.Control),
                (Avalonia.Input.Key.C, KeyModifiers.Control),
                (Avalonia.Input.Key.X, KeyModifiers.Control),
                (Avalonia.Input.Key.V, KeyModifiers.Control),
                (Avalonia.Input.Key.Z, KeyModifiers.Control),
                (Avalonia.Input.Key.Z, KeyModifiers.Control | KeyModifiers.Shift),
                (Avalonia.Input.Key.C, KeyModifiers.Control | KeyModifiers.Shift),
                (Avalonia.Input.Key.I, KeyModifiers.Control),
                (Avalonia.Input.Key.N, KeyModifiers.Control),
            })
            {
                Check(Key(key, modifiers).Handled && window.LastDelivered is null,
                    $"Inline text editing delivered image shortcut {modifiers}+{key}.");
                Check(ReferenceEquals(session, window._text) && session.Content == "ABCD"
                    && document.Layers.Count == count, "An image shortcut changed the active text session.");
            }
            // Resolve the command, not hard-coded Ctrl+C: user bindings obey the same protection.
            window._shortcutSettings.Overrides[$"{Shortcuts.Menus}:Copy"] = new("B", ShortcutModifiers.Control);
            window.ShowKeys();
            Check(Key(Avalonia.Input.Key.B, KeyModifiers.Control).Handled && window.LastDelivered is null,
                "A remapped image command escaped the text editing guard.");

            // Spy on the save command to verify dispatch without opening a picker or touching a real project.
            var saves = 0;
            window._verbs[$"{Shortcuts.Menus}:Save"] = _ => { saves++; return true; };
            window._verbs[$"{Shortcuts.Menus}:Save As"] = _ => { saves++; return true; };
            Key(Avalonia.Input.Key.S, KeyModifiers.Control);
            Key(Avalonia.Input.Key.S, KeyModifiers.Control | KeyModifiers.Shift);
            window._shortcutSettings.Overrides[$"{Shortcuts.Menus}:Save"] = new("K", ShortcutModifiers.Control);
            window.ShowKeys();
            Key(Avalonia.Input.Key.K, KeyModifiers.Control);
            Check(saves == 3, "Saving stopped working during inline text editing.");

            Key(Avalonia.Input.Key.Home);
            Check(session.CaretIndex == 0 && window.LastDelivered is null, "Home reached a window command.");
            Key(Avalonia.Input.Key.Delete, KeyModifiers.Control);
            Check(session.Content == "BCD" && window.LastDelivered is null,
                "Ctrl+Delete filled pixels instead of reaching the canvas delete operation.");
            Key(Avalonia.Input.Key.Delete, KeyModifiers.Alt);
            Check(session.Content == "CD" && window.LastDelivered is null,
                "Alt+Delete filled pixels instead of reaching the canvas delete operation.");
            Key(Avalonia.Input.Key.Right);
            Check(session.CaretIndex == 1, "The text shortcut guard swallowed caret movement.");
            Key(Avalonia.Input.Key.Back);
            Check(session.Content == "D", "The text shortcut guard swallowed Backspace.");

            var request = new TextInputMethodClientRequestedEventArgs
            {
                RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
            };
            window._canvas.RaiseEvent(request);
            var client = request.Client ?? throw new InvalidOperationException("The text shortcut fixture has no IME client.");
            client.SetPreeditText("nihao", 2);
            foreach (var key in new[] { Avalonia.Input.Key.Enter, Avalonia.Input.Key.Escape, Avalonia.Input.Key.Delete, Avalonia.Input.Key.V, Avalonia.Input.Key.K })
            {
                Key(key, KeyModifiers.Control);
                Check(ReferenceEquals(session, window._text) && session.Content == "D" && window.LastDelivered is null && saves == 3,
                    "A composition key reached a text or image command.");
            }
            client.SetPreeditText(null, null);
            Key(Avalonia.Input.Key.ImeProcessed);
            Check(ReferenceEquals(session, window._text) && session.Content == "D", "ImeProcessed changed the text session.");
            Key(Avalonia.Input.Key.Enter, KeyModifiers.Control);
            Check(window._text is null && !window._canvas.TextEditing, "Ctrl+Enter did not commit inline text.");
            Begin();
            Key(Avalonia.Input.Key.Escape);
            Check(window._text is null && !window._canvas.TextEditing, "Escape did not cancel inline text.");
            Key(Avalonia.Input.Key.B, KeyModifiers.Control);
            Check(window.LastDelivered == $"{Shortcuts.Menus}:Copy", "Image shortcuts remained blocked after inline text editing ended.");
            return $"canvas text shortcuts: {passed} checks passed; image commands blocked, saves and editing keys retained, IME composition isolated";
        }
        finally { window.CloseForHeadlessCheck(); }
    }
}
