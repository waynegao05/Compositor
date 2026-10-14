using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Desktop;

/// <summary>Exercises real pointer capture, explicit finalization and ordinary release for each drag type.</summary>
internal static class CanvasInteractionChecks
{
    internal static string Run()
    {
        var count = 0;
        foreach (var gesture in new[]
                 { "stroke", "gradient", "shape", "crop", "transform", "distort", "guide", "pixels", "upright", "sample", "rectangle", "lasso", "pan" })
        foreach (var ending in new[] { "capture loss", "explicit finish", "release" })
        {
            Check(gesture, ending);
            count++;
        }
        return $"Canvas interaction checks passed: {count} pointer gestures, balanced history and idempotent finalization.";
    }

    private static void Check(string gesture, string ending)
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var view = new CanvasView { Document = document };
        var window = new Window { Width = 360, Height = 360, Content = view };
        var history = new DocumentHistory();
        var finished = 0;
        var changes = 0;
        IPointer? pointer = null;
        view.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);

        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"{gesture}, {ending}: {message}");
        }
        void Begin() => history.Begin(gesture, document, null);
        void Changed() => changes++;
        void Finish()
        {
            Require(!view.HasPendingInteraction, "callback ran before the gesture was cleared");
            finished++;
            if (!history.HasPendingEdit) Begin();
            document.Resolution += 1;
            history.End(document, null);
            // Owners can refresh or save here, which must not finish the same gesture twice.
            view.FinishInteraction();
        }

        var start = new SKPoint(30, 35);
        var modifiers = RawInputModifiers.None;
        var hasCompletion = true;
        switch (gesture)
        {
            case "stroke":
                view.PaintEnabled = true;
                view.StrokeFinished = points => { Require(points.Count >= 2, "stroke samples were lost"); Finish(); };
                break;
            case "gradient":
                view.GradientEnabled = true;
                view.GradientStarted = Begin;
                view.GradientChanged = (_, _) => Changed();
                view.GradientFinished = (_, _) => Finish();
                break;
            case "shape":
                view.ShapeEnabled = true;
                view.ShapeFinished = (_, _, _) => Finish();
                break;
            case "crop":
                view.CropEnabled = true;
                view.CropBox = SKRectI.Create(20, 20, 60, 60);
                view.CropChanged = (_, _) => Changed();
                start = new SKPoint(50, 50);
                hasCompletion = false;
                break;
            case "transform":
            case "distort":
                view.TransformEnabled = true;
                view.TransformBox = new LayerTransform(20, 20, 60, 50);
                view.TransformStarted = Begin;
                view.TransformChanged = _ => Changed();
                view.TransformFinished = Finish;
                if (gesture == "distort")
                {
                    view.DistortEnabled = true;
                    view.DistortStarted = Begin;
                    view.DistortChanged = _ => Changed();
                    view.DistortFinished = _ => Finish();
                    modifiers = RawInputModifiers.Control;
                    start = new SKPoint(20, 20);
                }
                else start = new SKPoint(50, 45);
                break;
            case "guide":
                var guide = new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 50 };
                document.Guides.Add(guide);
                view.GuidesDraggable = true;
                view.GuideDragStarted = Begin;
                view.GuideMoved = (_, at) => { guide.Position = at; Changed(); };
                view.GuideDragFinished = Finish;
                start = new SKPoint(50, 50);
                break;
            case "pixels":
                view.Selection = SelectionTool.Rectangle;
                view.PixelsGrabbed = _ => { Begin(); return true; };
                view.PixelsMoved = (_, _) => Changed();
                view.PixelsDropped = Finish;
                modifiers = RawInputModifiers.Control;
                break;
            case "upright":
                view.UprightDrawing = true;
                view.UprightDrawn = (_, _) => Finish();
                break;
            case "sample":
                view.EyedropperOnClick = true;
                view.EyedropperClicked = (_, _) => Changed();
                hasCompletion = false;
                break;
            case "rectangle":
                view.Selection = SelectionTool.Rectangle;
                view.MarqueeFinished = (_, _) => Finish();
                break;
            case "lasso":
                view.Selection = SelectionTool.Lasso;
                view.LassoFinished = (_, _) => Finish();
                break;
            default:
                hasCompletion = false;
                view.ViewportChanged = Changed;
                break;
        }

        try
        {
            window.Show();
            window.UpdateLayout();
            view.Fit();
            Point At(SKPoint point) => view.TranslatePoint(view.InView(point), window)!.Value;
            window.MouseDown(At(start), MouseButton.Left, modifiers | RawInputModifiers.LeftMouseButton);
            window.MouseMove(At(new SKPoint(start.X + 8, start.Y + 6)), modifiers | RawInputModifiers.LeftMouseButton);
            window.MouseMove(At(new SKPoint(start.X + 16, start.Y + 13)), modifiers | RawInputModifiers.LeftMouseButton);
            Require(view.HasPendingInteraction, "press did not start a gesture");
            Require(pointer is not null, "pointer was not captured");
            if (ending == "capture loss") pointer!.Capture(null);
            else if (ending == "explicit finish") view.FinishInteraction();
            else window.MouseUp(At(new SKPoint(start.X + 16, start.Y + 13)), MouseButton.Left);
            Require(!view.HasPendingInteraction, "gesture remained active after finalization");
            Require(!history.HasPendingEdit, "history transaction remained open");
            Require(finished == (hasCompletion ? 1 : 0), $"unexpected finish count {finished}");
            Require(history.UndoCount == (hasCompletion ? 1 : 0), "finish was not exactly one history entry");
            var after = changes;
            window.MouseMove(At(new SKPoint(90, 90)), RawInputModifiers.None);
            window.MouseUp(At(new SKPoint(90, 90)), MouseButton.Left);
            view.FinishInteraction();
            Require(changes == after, "an unpressed move continued the old gesture");
            Require(finished == (hasCompletion ? 1 : 0), "gesture finished twice");
            Require(!history.HasPendingEdit, "repeated finalization left history open");
        }
        finally
        {
            view.FinishInteraction();
            window.Close();
        }
    }
}
