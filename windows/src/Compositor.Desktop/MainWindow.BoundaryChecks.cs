using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Core.Document;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Exercise document ownership and unfinished gestures through real headless windows.</summary>
    internal static string EditBoundarySelfCheck(string root)
    {
        Directory.CreateDirectory(root);
        var windows = new List<MainWindow>();
        var report = new List<string>();
        try
        {
            var pixels = Fixture();
            var doc = pixels._document!;
            doc.Selection = DocumentSelection.Rectangular(SKRectI.Create(8, 8, 16, 16), doc.Width, doc.Height);
            var image = doc.Layers[0].Asset!.Image;
            var before = InkOf(image);
            Require(pixels.PixelsTaken(new SKPoint(12, 12)), "selection pixels were not lifted");
            pixels.PixelsDragged(10, 0);
            pixels.PixelsLetGo();
            Require(pixels._history.CanUndo && !pixels._history.HasPendingEdit, "pixel drag history did not finish");
            pixels.Undo();
            Require(InkOf(doc.Layers[0].Asset!.Image) == before, "undo erased the lifted pixels");
            var revision = pixels._history.CurrentRevision;
            Require(pixels._history.CanRedo, "moved pixels did not leave redo");
            Require(pixels.PixelsTaken(new SKPoint(12, 12)), "zero-distance lift did not start");
            pixels.PixelsLetGo();
            Require(ReferenceEquals(doc.Layers[0].Asset!.Image, image) && pixels._history.CurrentRevision == revision
                && pixels._history.CanRedo, "zero-distance drag replaced pixels or discarded redo");
            report.Add("B04: pixel drag undo restores all pixels; zero-distance drag preserves image identity and redo");

            var color = Fixture();
            var first = color._open;
            var original = first.Document!.Selection;
            color.ColorRange();
            color.Picked(new SKPoint(10, 10), KeyModifiers.None);
            Require(color._history.HasPendingEdit, "color range did not open a transaction");
            var panel = color._colorRangePanel!;
            var next = new Tab { Document = LayerPlacement.NewDocument(64, 64) };
            color._tabs.Add(next);
            color.Bring(next);
            Require(ReferenceEquals(first.Document.Selection, original) && !first.History.HasPendingEdit
                && !next.History.HasPendingEdit && !panel.IsVisible && color._colorRange is null,
                "switching tabs leaked the color selection or transaction");
            color.Bring(first);
            color.ColorRange();
            color.Picked(new SKPoint(10, 10), KeyModifiers.None);
            Press(color._colorRangePanel!, "OK");
            Require(!first.History.HasPendingEdit && first.History.CanUndo, "color range Apply did not finish history");
            color.Undo();
            Require(ReferenceEquals(first.Document.Selection, original), "color range did not undo to its original selection");
            report.Add("B07: Color Range cancels on tab switch, owns its original history, and Apply undoes correctly");

            var layers = Fixture();
            layers.DeleteLayer();
            Require(layers._document!.Layers.Count == 0 && layers._documentItems.All(item => item.IsEnabled),
                "empty document disabled layer creation");
            layers._documentItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(layers._document.Layers.Count == 1 && !layers._document.Layers[0].IsGroup,
                "New Blank Layer menu did not recover an empty document");
            layers.DeleteLayer();
            layers._documentItems[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(layers._document.Layers.Count == 1 && layers._document.Layers[0].IsGroup,
                "New Folder menu did not recover an empty document");
            Require(!layers._blend.IsEnabled && layers._opacity.IsEnabled && !layers.StepBlend(1),
                "group blend mode remains editable or its opacity is disabled");
            report.Add("B06/B08: both creation menus work after deleting the last layer; groups retain opacity with blend changes blocked");

            var fields = Fixture();
            var box = new TextBox { Text = "first second", Width = 240, Height = 35 };
            DockPanel.SetDock(box, Dock.Top);
            ((DockPanel)fields.Content!).Children.Insert(0, box);
            fields.UpdateLayout();
            box.Focus();
            box.CaretIndex = 0;
            var fieldImage = fields._document!.Layers[0].Asset!.Image;
            var fieldInk = InkOf(fieldImage);
            fields.LastDelivered = null;
            fields.KeyPress(Key.Delete, RawInputModifiers.Control, PhysicalKey.Delete, null);
            Require(box.IsFocused && fields.LastDelivered is null && InkOf(fieldImage) == fieldInk
                && !fields._history.IsModified && box.Text != "first second",
                "Ctrl+Delete escaped the actual text field or failed to edit it");
            report.Add("B17: Ctrl+Delete edits the focused TextBox through the window key route without filling image pixels");

            var text = Fixture();
            text.SetTool(Tool.Type);
            text.TypeHere(new SKPoint(10, 10));
            text.TypedText("live text");
            var session = text._text!;
            var textCount = text._document!.Layers.Count;
            var textImage = text._document.Layers.First(layer => layer.ID == session.LayerID).Asset!.Image;
            var cut = text._keyRows.Single(row => row.ID == MenuKey("Cut")).Item;
            cut.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(ReferenceEquals(text._text, session) && session.Content == "live text"
                && text._document.Layers.Count == textCount
                && ReferenceEquals(text._document.Layers.First(layer => layer.ID == session.LayerID).Asset!.Image, textImage),
                "menu Cut changed pixels beneath an active text session");
            text.CancelText();
            report.Add("B20: actual Cut menu leaves the live text session and its raster untouched");

            var saving = Fixture(saved: true);
            var savedDoc = saving._document!;
            var savedInk = InkOf(savedDoc.Layers[0].Asset!.Image);
            BeginStroke(saving);
            saving.Save();
            Require(!saving._canvas.HasPendingInteraction && !saving._history.HasPendingEdit
                && !saving._history.IsModified && InkOf(savedDoc.Layers[0].Asset!.Image) != savedInk,
                "Save lost an unfinished brush stroke or saved an open transaction");
            using (var loaded = ProjectStore.Load(saving._projectPath!))
                Require(InkOf(loaded.Images.Values.Single().Image) == InkOf(savedDoc.Layers[0].Asset!.Image),
                    "Save did not persist the finished stroke");
            report.Add("B02: Save commits an actual unfinished brush gesture and persists its final pixels");

            var switching = Fixture(saved: true);
            var source = switching._open;
            var sourceInk = InkOf(source.Document!.Layers[0].Asset!.Image);
            BeginStroke(switching);
            var other = new Tab { Document = LayerPlacement.NewDocument(64, 64) };
            switching._tabs.Add(other);
            switching.Bring(other);
            Require(source.History.IsModified && !source.History.HasPendingEdit
                && InkOf(source.Document.Layers[0].Asset!.Image) != sourceInk
                && other.Document!.Layers[0].Asset is null && !switching._canvas.HasPendingInteraction,
                "tab switch lost the gesture or applied it to another document");
            report.Add("B02: tab switch commits an unfinished gesture to its original document");

            var closing = Fixture(saved: true);
            var closingDoc = closing._document!;
            var closingInk = InkOf(closingDoc.Layers[0].Asset!.Image);
            BeginStroke(closing);
            closing.Close();
            Pump(() => closing.OwnedWindows.OfType<ConfirmDialog>().Any(), "unfinished stroke did not trigger close confirmation");
            Press(closing.OwnedWindows.OfType<ConfirmDialog>().Single(), "Keep");
            Pump(() => !closing._windowClosePending, "Keep did not finish close confirmation");
            Require(closing.IsVisible && closing._history.IsModified && !closing._history.HasPendingEdit
                && InkOf(closingDoc.Layers[0].Asset!.Image) != closingInk,
                "Keep discarded the unfinished stroke");
            report.Add("B02: window close finishes a stroke before checking dirty state; Keep preserves the result");

            var watched = Fixture(saved: true);
            var held = watched._document!;
            BeginStroke(watched);
            using (var changed = LayerPlacement.NewDocument(80, 48)!)
                ProjectStore.Save(ProjectSnapshot.FromDocument(changed), watched._projectPath!);
            watched.ProjectWritten(null, EventArgs.Empty);
            Require(ReferenceEquals(held, watched._document) && watched._canvas.HasPendingInteraction,
                "external reload replaced the document under an unfinished gesture");
            watched._canvas.FinishInteraction();
            watched.ProjectWritten(null, EventArgs.Empty);
            Require(ReferenceEquals(held, watched._document) && watched._history.IsModified
                && watched._message == L10n.T("The project has been written by something else — save or reopen to take the change up"),
                "deferred watch event was consumed during the gesture");
            report.Add("B02: external watcher defers an active gesture and reports the retained external change afterward");
            var guides = Fixture();
            var guideDocument = guides._document!;
            var outside = GuideEdits.Add(guideDocument, Compositor.Core.Format.GuideAxis.Horizontal, -10);
            var inside = GuideEdits.Add(guideDocument, Compositor.Core.Format.GuideAxis.Horizontal, 10)!.Value;
            guides.GuideDragStarted();
            guides.GuideMoved(inside, 20);
            guides.GuideDragFinished();
            Require(guideDocument.Guides.Count == 2 && guideDocument.Guides.Any(guide => guide.ID == outside),
                "moving one guide removed a different off-canvas guide");
            guides.GuideDragStarted();
            guides.GuideMoved(inside, 80);
            guides.GuideDragFinished();
            Require(guideDocument.Guides.Count == 1 && guideDocument.Guides[0].ID == outside,
                "dragging a guide off canvas removed the wrong guide");
            report.Add("B32 boundary: guide dragging preserves unrelated off-canvas guides and removes only the dragged guide");

            var upright = Fixture();
            var uprightLayer = upright._document!.Layers[0];
            upright._cameraRawLayer = uprightLayer.ID;
            upright._cameraRaw = new CameraRawPanel(new CameraRawSettings(), SKColors.Black);
            upright._cameraRaw.AddGuide(new CameraRawGeometryGuide(.2, .3, .8, .7));
            foreach (var rotation in new[] { 0, 30 })
            foreach (var flipX in new[] { false, true })
            foreach (var flipY in new[] { false, true })
            {
                var guideBox = uprightLayer.Transform = new LayerTransform(12, 18, 320, 96, rotation, flipX, flipY);
                var start = guideBox.Point(flipX ? .8 : .2, flipY ? .7 : .3);
                var end = guideBox.Point(flipX ? .2 : .8, flipY ? .3 : .7);
                var fraction = Fraction(uprightLayer, start);
                Require(fraction is { } at && Math.Abs(at.X - .2) < .0001 && Math.Abs(at.Y - .3) < .0001,
                    "scaled/rotated/flipped guide was normalized against source pixels instead of placement");
                upright.ShowUprightGuides();
                var drawn = upright._canvas.UprightGuides.Single();
                Require(SKPoint.Distance(drawn.Start, start) < .001 && SKPoint.Distance(drawn.End, end) < .001,
                    "guide overlay did not return to its scaled/rotated/flipped document positions");
            }
            report.Add("B16 boundary: Camera Raw guide input and overlay round trip through scale, rotation and all mirror combinations");
            return string.Join(Environment.NewLine, report);
        }
        finally
        {
            foreach (var window in windows)
            {
                window.CloseForHeadlessCheck();
                window._canvas.Document = null;
                foreach (var tab in window._tabs) tab.Document?.Dispose();
            }
            Dispatcher.UIThread.RunJobs();
        }

        MainWindow Fixture(bool saved = false)
        {
            var window = new MainWindow();
            windows.Add(window);
            var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
            var image = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
            image.Erase(SKColors.Red);
            LayerPlacement.AddImage(document, image, SKRectI.Create(0, 0, 64, 64), "Boundary", null);
            window._document = document;
            window.Show(window._open);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window._canvas.Fit();
            if (saved)
                Require(window.WriteTo(window._open, Path.Combine(root, $"boundary-{windows.Count}.comp")),
                    "could not save gesture fixture");
            window._watchTimer?.Stop();
            return window;
        }

        static void BeginStroke(MainWindow window)
        {
            window.SetTool(Tool.Brush);
            window._options.PaintOnMask = false;
            window._options.Erase = false;
            window._options.Brush = window._options.Brush with { Diameter = 8, Red = 0, Green = 0, Blue = 1, Opacity = 1 };
            window.PushBrush();
            window.UpdateLayout();
            Point Aim(float x, float y) => window._canvas.TranslatePoint(window._canvas.InView(new SKPoint(x, y)), window)
                ?? throw new InvalidOperationException("gesture canvas has no window coordinates");
            window.MouseDown(Aim(20, 20), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(Aim(30, 25), RawInputModifiers.LeftMouseButton);
            Require(window._canvas.HasPendingInteraction, "actual brush gesture did not start");
        }

        static void Press(Window window, string label)
        {
            window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, L10n.T(label)))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        static void Pump(Func<bool> done, string message)
        {
            var timer = Stopwatch.StartNew();
            while (!done() && timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
            Require(done(), message);
        }

        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
