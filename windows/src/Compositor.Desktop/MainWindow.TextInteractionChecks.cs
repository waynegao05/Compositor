using Avalonia.Threading;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Check text targets and transformed insertion through the canvas's real editing callbacks.</summary>
    internal static string TextInteractionSelfCheck()
    {
        var windows = new List<MainWindow>();
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        MainWindow Fixture(CanvasDocument document)
        {
            var window = new MainWindow { _document = document };
            windows.Add(window);
            window.Show(window._open);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.SetTool(Tool.Type);
            return window;
        }
        static ImageLayer Add(CanvasDocument document, string content)
        {
            var id = TextEdits.Add(document, new LayerTextStyle { Content = content, FontName = "Arial", FontSize = 24 },
                new SKPoint(100, 100)) ?? throw new InvalidOperationException("Could not create the text fixture.");
            return document.Layers.Single(layer => layer.ID == id);
        }
        try
        {
            foreach (var hideBottom in new[] { false, true })
            {
                var document = new CanvasDocument(Guid.NewGuid(), 500, 500);
                var bottom = Add(document, "BOTTOM");
                var top = Add(document, "TOPTOP");
                bottom.IsVisible = !hideBottom;
                var window = Fixture(document);
                window._canvas.TextClicked!(new SKPoint(120, 120));
                Check(window._text?.LayerID == top.ID, "Clicking overlapping text edited a lower or hidden layer.");
                window._canvas.TextTyped!("X");
                Check(bottom.LiveText!.Content == "BOTTOM" && top.LiveText!.Content != "TOPTOP",
                    "Typing changed the wrong text layer.");
                window._canvas.TextCancelled!();
                Check(top.LiveText!.Content == "TOPTOP" && !window._history.IsModified,
                    "Cancel did not restore the selected text layer.");
            }
            foreach (var angle in new[] { 0d, 45d, 90d })
            {
                var document = new CanvasDocument(Guid.NewGuid(), 500, 500);
                var layer = Add(document, "ABCDEFGHIJ");
                layer.Transform = layer.Transform with
                { Width = layer.Asset!.Width * 2, Height = layer.Asset.Height * 1.5, Rotation = angle, FlipX = true };
                var window = Fixture(document);
                var local = TextEdits.Caret(layer.LiveText!, 1);
                var click = layer.Transform.Point(1 - local.X / layer.Asset.Width, local.Y / layer.Asset.Height);
                window._canvas.TextClicked!(click);
                Check(window._text?.CaretIndex == 1, "The first click did not place the caret in transformed text.");
                window._canvas.TextClicked!(click);
                Check(window._text?.CaretIndex == 1 && window._canvas.TextCaretLine == window._text.CaretLine(document),
                    "A repeated click or the canvas caret ignored the text transform.");
                window._canvas.TextTyped!("中");
                window._canvas.TextCommitted!();
                Check(layer.LiveText!.Content == "A中BCDEFGHIJ"
                    && Math.Abs(layer.Transform.Width / layer.Asset!.Width - 2) < 0.000001
                    && Math.Abs(layer.Transform.Height / layer.Asset.Height - 1.5) < 0.000001,
                    "Typing moved the insertion point or lost the text layer's scale.");
                window.Undo();
                Check(document.Layers.Single().LiveText!.Content == "ABCDEFGHIJ", "Text edit did not undo.");
                window.Redo();
                Check(document.Layers.Single().LiveText!.Content == "A中BCDEFGHIJ", "Text edit did not redo.");
            }
            return $"text targets and transforms: {checks} checks passed; visible frontmost target, transformed caret, preserved scale, cancel and undo/redo";
        }
        finally
        {
            foreach (var window in windows) window.CloseForHeadlessCheck();
        }
    }
}
