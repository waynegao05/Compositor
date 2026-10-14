using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Commands following structural edits and unfinished strokes keep their intended layer.</summary>
    internal static string TransactionSelfCheck()
    {
        var windows = new List<MainWindow>();
        try
        {
            var undo = Fixture("Base", "Middle", "Top");
            var middle = undo._document!.Layers[1].ID;
            var top = undo._document.Layers[2].ID;
            ClickLayer(undo, middle);
            Key(undo, Avalonia.Input.Key.Delete);
            Key(undo, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Require(undo.Selected == middle, "Undo did not restore the deleted layer's selection");
            Key(undo, Avalonia.Input.Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
            Require(undo.Selected == top, "Redo did not restore the post-delete selection");
            Key(undo, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Key(undo, Avalonia.Input.Key.Delete);
            Require(undo._document.Layers.Any(layer => layer.ID == top)
                && undo._document.Layers.All(layer => layer.ID != middle), "the next Delete targeted another layer");

            foreach (var action in new[] { "blank", "folder", "duplicate", "group", "copy layer", "paste" })
            {
                var window = Fixture("Original");
                var original = window.Selected;
                window._document!.Selection = DocumentSelection.Rectangular(SKRectI.Create(0, 0, 64, 64), 64, 64);
                switch (action)
                {
                    case "blank": window.NewBlankLayer(); break;
                    case "folder": window.NewFolder(); break;
                    case "duplicate": window.DuplicateLayer(); break;
                    case "group": window.GroupSelected(); break;
                    case "copy layer": window.LayerViaCopy(); break;
                    case "paste": window.Copy(); window.Paste(); break;
                }
                var made = window.Selected;
                Require(made != original, $"{action} did not select its new layer");
                window.Undo();
                Require(window.Selected == original, $"{action} Undo did not restore the original selection");
                window.Redo();
                Require(window.Selected == made, $"{action} Redo did not restore the new selection");
            }

            foreach (var baseFirst in new[] { true, false })
            {
                var window = Fixture("Base", "Clipped");
                var document = window._document!;
                document.Layers[1].MaskSourceID = document.Layers[0].ID;
                var source = document.Layers[0].ID;
                var clipped = document.Layers[1].ID;
                ClickLayer(window, baseFirst ? source : clipped);
                ClickLayer(window, baseFirst ? clipped : source, control: true);
                Key(window, Avalonia.Input.Key.Delete);
                Require(document.Layers.Count == 0, "multi-selection deletion depends on selection order");
                window.Undo();
                Require(document.Layers.Count == 2 && document.Layers[1].MaskSourceID == source,
                    "batch deletion Undo lost a clipping dependency");
            }

            foreach (var command in new[] { "new", "delete", "undo", "copy", "duplicate" })
            {
                var window = Fixture("Base");
                var document = window._document!;
                var original = document.Layers[0].ID;
                document.Selection = DocumentSelection.Rectangular(SKRectI.Create(0, 0, 64, 64), 64, 64);
                BeginStroke(window);
                switch (command)
                {
                    case "new": Key(window, Avalonia.Input.Key.N, RawInputModifiers.Control | RawInputModifiers.Shift); break;
                    case "delete": Key(window, Avalonia.Input.Key.Delete); break;
                    case "undo": Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control); break;
                    case "copy": Key(window, Avalonia.Input.Key.C, RawInputModifiers.Control); break;
                    case "duplicate": window.DuplicateLayer(); break;
                }
                Require(!window._canvas.HasPendingInteraction && !window._history.HasPendingEdit,
                    $"{command} left an unfinished stroke or history");
                var afterCommand = document.Clone();
                window.MouseUp(Aim(window, 30, 25), MouseButton.Left);
                Require(document.SameAs(afterCommand), $"{command} allowed the later mouse release to paint again");
                if (command == "delete") window.Undo();
                var source = document.Layers.Single(layer => layer.ID == original);
                Require(command == "undo" ? Blue(source.Asset!.Image) == 0 : Blue(source.Asset!.Image) > 0,
                    $"{command} did not preserve the stroke on its original layer");
                if (command == "new") Require(document.Layers.Single(layer => layer.ID != original).Asset is null,
                    "the new layer received the old stroke");
                if (command == "copy") Require(window._clipboard is { } copied && Blue(copied.Image) > 0,
                    "Copy omitted the unfinished stroke");
            }
            return "Transaction checks passed: undo/redo layer identity, 6 insertion histories, 2 selection orders and 5 unfinished-stroke commands.";
        }
        finally
        {
            foreach (var window in windows) window.CloseForHeadlessCheck();
            Dispatcher.UIThread.RunJobs();
        }

        MainWindow Fixture(params string[] names)
        {
            var window = new MainWindow();
            windows.Add(window);
            var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
            foreach (var name in names)
            {
                var image = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
                image.Erase(SKColors.Red);
                LayerPlacement.AddImage(document, image, SKRectI.Create(0, 0, 64, 64), name,
                    document.Layers.LastOrDefault()?.ID);
            }
            window._document = document;
            window.Show(window._open);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window._canvas.Fit();
            return window;
        }

        static void ClickLayer(MainWindow window, Guid id, bool control = false)
        {
            window.UpdateLayout();
            var item = window._layers.Items.OfType<ListBoxItem>().Single(row => Equals(row.Tag, id));
            var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
            var modifiers = control ? RawInputModifiers.Control : RawInputModifiers.None;
            window.MouseDown(point, MouseButton.Left, modifiers);
            window.MouseUp(point, MouseButton.Left, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        static Point Aim(MainWindow window, float x, float y) =>
            window._canvas.TranslatePoint(window._canvas.InView(new SKPoint(x, y)), window)!.Value;

        static void BeginStroke(MainWindow window)
        {
            window.SetTool(Tool.Brush);
            window._options.PaintOnMask = false;
            window._options.Erase = false;
            window._options.Brush = window._options.Brush with { Diameter = 8, Red = 0, Green = 0, Blue = 1, Opacity = 1 };
            window.PushBrush();
            window.MouseDown(Aim(window, 20, 20), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(Aim(window, 30, 25), RawInputModifiers.LeftMouseButton);
            Require(window._canvas.HasPendingInteraction, "the real brush gesture did not begin");
        }

        static void Key(MainWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None) =>
            window.KeyPress(key, modifiers, PhysicalKey.None, null);

        static int Blue(SKBitmap image) => image.Pixels.Count(color => color.Blue > 150 && color.Red < 100);

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
