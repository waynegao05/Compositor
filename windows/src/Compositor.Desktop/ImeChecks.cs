using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media.Imaging;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

/// <summary>
/// Exercises the canvas/client contract without a native IME. Windows 11 candidate-window placement,
/// Microsoft Pinyin key routing and DPI still need to be checked with the real input method.
/// </summary>
internal static class ImeChecks
{
    internal static int Run()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 480, 320);
        var canvas = new CanvasView { Document = document };
        var window = new Window { Width = 480, Height = 320, Content = canvas };
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }

        TextInputMethodClient? Client()
        {
            var request = new TextInputMethodClientRequestedEventArgs
            {
                RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
            };
            canvas.RaiseEvent(request);
            return request.Client;
        }

        KeyEventArgs Press(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
            canvas.RaiseEvent(e);
            return e;
        }

        var inserted = new List<string>();
        var actions = 0;
        canvas.TextTyped = inserted.Add;
        canvas.TextCommitted = () => actions++;
        canvas.TextCancelled = () => actions++;
        canvas.TextDeleted = () => actions++;
        canvas.TextBackspaced = () => actions++;
        canvas.TextMoved = _ => actions++;

        try
        {
            window.Show();
            Check(Client() is null && !InputMethod.GetIsInputMethodEnabled(canvas), "Canvas enables IME outside text editing.");
            canvas.BeginText();
            var client = Client() ?? throw new InvalidOperationException("Text editing did not expose an IME client.");
            Check(InputMethod.GetIsInputMethodEnabled(canvas) && client.SupportsPreedit,
                "Canvas did not enable native preedit input.");
            Check(!client.SupportsSurroundingText, "Canvas advertises unsupported text reconversion.");
            canvas.RestoreViewport((2, 10, 20));
            canvas.TextCaret = SKRect.Create(50, 60, 2, 18);
            Check(client.CursorRectangle == new Rect(80, 80, 4, 36), "IME caret does not account for zoom and pan.");
            var moved = 0;
            client.CursorRectangleChanged += (_, _) => moved++;
            canvas.TextCaret = SKRect.Create(60, 60, 2, 18);
            Check(moved == 1 && client.CursorRectangle.X == 100, "A text caret change does not reposition the IME.");
            canvas.RestoreViewport((1, 10, 20));
            Check(moved == 2 && client.CursorRectangle.X == 50, "A viewport change does not reposition the IME.");

            client.SetPreeditText("nihao", 2);
            Check(canvas.TextComposing && inserted.Count == 0, "Preedit text was committed to the document.");
            var offset = client.CursorRectangle.X;
            client.SetPreeditText("nihao", 0);
            Check(client.CursorRectangle.X == 50 && offset > 50, "The preedit cursor position is ignored.");
            foreach (var key in new[] { Key.Enter, Key.Escape, Key.Back, Key.Delete, Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End })
            {
                Check(Press(key).Handled, $"Composition key {key} reached a document action.");
            }
            Check(Press(Key.Enter, KeyModifiers.Control).Handled && actions == 0 && inserted.Count == 0,
                "An IME editing key modified or ended the text session.");

            // The platform clears the preview before delivering a committed result.
            client.SetPreeditText(null, null);
            canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "你好" });
            Check(inserted.SequenceEqual(new[] { "你好" }) && !canvas.TextComposing,
                "Committed CJK input was dropped or inserted more than once.");
            Press(Key.Enter);
            Check(inserted.SequenceEqual(new[] { "你好", "\n" }), "Enter no longer inserts a newline after composition.");
            Press(Key.Escape);
            Check(actions == 1, "Escape no longer cancels a text session after composition.");
            Check(Press(Key.ImeProcessed).Handled && actions == 1, "An IME-processed key reached the canvas.");

            client.SetPreeditText("中文输入", 2);
            using (var rendered = new RenderTargetBitmap(new PixelSize(480, 320))) rendered.Render(canvas);
            Check(inserted.Count == 2, "Rendering a preedit changed committed text.");
            canvas.EndText();
            Check(!canvas.TextComposing && Client() is null && !InputMethod.GetIsInputMethodEnabled(canvas),
                "Ending text editing left the input method active.");
            client.SetPreeditText("late callback", 2);
            Check(!canvas.TextComposing, "A late IME callback restored a stale composition.");
            canvas.BeginText();
            Check(!canvas.TextComposing && Client() is not null, "Starting another text session kept the old preview.");

            var dialog = new ProbeDialog();
            try
            {
                dialog.Show();
                dialog.Box.Focus();
                var request = new TextInputMethodClientRequestedEventArgs
                {
                    RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
                };
                dialog.Box.RaiseEvent(request);
                var dialogClient = request.Client ?? throw new InvalidOperationException("Dialog text box has no IME client.");
                dialogClient.SetPreeditText("ni", 2);
                foreach (var key in new[] { Key.Enter, Key.Escape })
                {
                    dialog.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
                }
                Check(dialog.Accepted == 0 && dialog.Cancelled == 0, "IME keys triggered a dialog's default/cancel button.");
                dialogClient.SetPreeditText(null, null);
                dialog.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Check(dialog.Accepted == 1, "Dialog Enter stopped working after composition.");
                dialog.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Check(dialog.Cancelled == 1, "Dialog Escape stopped working after composition.");
            }
            finally
            {
                dialog.Close();
            }
            Console.WriteLine($"IME contract: {passed} checks passed. Native Windows 11 IME validation remains required.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: IME contract after {passed} checks: {ex.Message}");
            return 1;
        }
        finally
        {
            canvas.EndText();
            window.Close();
        }
    }

    private sealed class ProbeDialog : DialogWindow
    {
        internal readonly TextBox Box = new();
        internal int Accepted;
        internal int Cancelled;

        internal ProbeDialog()
        {
            Width = 320;
            Height = 180;
            var ok = new Button { Content = "OK", IsDefault = true };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            ok.Click += (_, _) => Accepted++;
            cancel.Click += (_, _) => Cancelled++;
            Content = new StackPanel { Children = { Box, ok, cancel } };
        }
    }
}
