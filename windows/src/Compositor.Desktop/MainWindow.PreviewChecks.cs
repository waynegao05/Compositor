using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Keep a filter's displayed pixels aligned with its controls and the document it will edit.</summary>
    internal static string PreviewBoundarySelfCheck()
    {
        var windows = new List<MainWindow>();
        try
        {
            foreach (var kind in new[] { FilterKind.Vignette, FilterKind.BloomGlow,
                         FilterKind.LensCorrection, FilterKind.TonalContrast })
            {
                var window = Fixture();
                var original = Pixels(window._document!);
                var task = window.ApplyFilter(kind);
                Pump();
                var panel = window.OwnedWindows.OfType<FilterDialog>().Single();
                var amount = panel.GetVisualDescendants().OfType<Slider>().First();
                amount.Value = 80;
                window.ShowPreviewOnce(null, EventArgs.Empty);
                var changed = Shown(window);
                Require(changed != original, kind + " did not preview its changed amount");
                amount.Value = 0;
                window.ShowPreviewOnce(null, EventArgs.Empty);
                Require(Shown(window) == original, kind + " kept the previous preview at zero");
                amount.Value = 80;
                window.ShowPreviewOnce(null, EventArgs.Empty);
                Require(Shown(window) == changed, kind + " could not resume preview after zero");
                var preview = panel.GetVisualDescendants().OfType<CheckBox>()
                    .Single(box => Equals(box.Content, L10n.T("Preview")));
                preview.IsChecked = false;
                Require(Shown(window) == original, kind + " ignored Preview being turned off");
                preview.IsChecked = true;
                window.ShowPreviewOnce(null, EventArgs.Empty);
                Require(Shown(window) == changed, kind + " ignored Preview being turned on");
                if (kind == FilterKind.LensCorrection) Press(panel, "Reset");
                else amount.Value = 0;
                window.ShowPreviewOnce(null, EventArgs.Empty);
                Require(Shown(window) == original, kind + " did not restore the original before Apply");
                Press(panel, "Apply");
                Require(task.IsCompletedSuccessfully && Pixels(window._document!) == original
                    && !window._history.IsModified && window._preview is null,
                    kind + " changed the document or left a preview after applying zero");
            }

            foreach (var kind in new[] { AdjustmentKind.BlackWhite, AdjustmentKind.Invert, AdjustmentKind.GaussianBlur })
            {
                var window = Fixture();
                var original = Pixels(window._document!);
                var task = window.ImageAdjustment(kind);
                Pump();
                window.ShowPreviewOnce(null, EventArgs.Empty);
                var shown = Shown(window);
                Require(shown != original && Pixels(window._document!) == original,
                    kind + " did not preview its initial settings independently of the document");
                Press(window.OwnedWindows.OfType<AdjustmentDialog>().Single(), "Apply");
                Require(task.IsCompletedSuccessfully && Pixels(window._document!) == shown,
                    kind + " initially previewed different pixels from Apply");
            }

            foreach (var kind in new[] { FilterKind.Vignette, FilterKind.BloomGlow, FilterKind.TonalContrast })
            {
                var window = Fixture();
                var original = Pixels(window._document!);
                var task = window.ApplyFilter(kind);
                Pump();
                window.ShowPreviewOnce(null, EventArgs.Empty);
                var shown = Shown(window);
                Require(shown != original && Pixels(window._document!) == original,
                    kind + " did not preview its nonzero defaults");
                Press(window.OwnedWindows.OfType<FilterDialog>().Single(), "Cancel");
                Require(task.IsCompletedSuccessfully && Pixels(window._document!) == original
                    && !window._history.IsModified && window._preview is null,
                    kind + " initial preview did not cancel cleanly");
            }

            var dither = Fixture();
            var ditherOriginal = Pixels(dither._document!);
            var ditherTask = dither.DitherFilter();
            Pump();
            dither.ShowPreviewOnce(null, EventArgs.Empty);
            var ditherPreview = Shown(dither);
            Require(ditherPreview != ditherOriginal, "Dither did not preview its initial settings");
            Press(dither.OwnedWindows.OfType<DitherDialog>().Single(), "Apply");
            Require(ditherTask.IsCompletedSuccessfully && Pixels(dither._document!) == ditherPreview,
                "Dither's initial preview differed from Apply");

            var camera = Fixture();
            var document = camera._document!;
            var id = document.Layers[0].ID;
            var beforeEdit = Pixels(document);
            Require(camera.Edit("Exposure", () => FilterEdits.ApplyAdjustment(document, id,
                    new LayerAdjustment { Kind = AdjustmentKind.Exposure,
                        ExposureSettings = new ExposureSettings { Exposure = 1 } })),
                "could not prepare a reversible edit");
            var afterEdit = Pixels(document);
            camera.CameraRawFilter();
            camera._cameraRaw!.Move("Exposure, stops", 1);
            camera.ShowPreviewOnce(null, EventArgs.Empty);
            Require(Shown(camera) != afterEdit, "Camera Raw did not establish a preview");
            camera._canvas.Focus();
            camera.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
            Pump();
            Require(camera._cameraRaw is null && camera._preview is null
                && camera._canvas.PreviewDocument is null && Pixels(document) == beforeEdit,
                "Ctrl+Z changed the document while Camera Raw retained its old preview");
            camera.CameraRawFilter();
            camera._cameraRaw!.Move("Exposure, stops", 1);
            camera.ShowPreviewOnce(null, EventArgs.Empty);
            camera._canvas.Focus();
            camera.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, null);
            Pump();
            Require(camera._cameraRaw is null && camera._preview is null && Pixels(document) == afterEdit,
                "Redo changed the document while Camera Raw retained its old preview");
            camera.CameraRawFilter();
            camera._cameraRaw!.Move("Exposure, stops", 1.5);
            camera.ShowPreviewOnce(null, EventArgs.Empty);
            var graded = Shown(camera);
            Press(camera._cameraRaw!.View, "Apply");
            Require(camera._cameraRaw is null && camera._preview is null && Pixels(document) == graded,
                "Camera Raw did not apply the pixels its reopened preview showed");

            return "filter zero/reset and preview toggles show the correct original; initial adjustment/filter/dither "
                + "previews match Apply or cancel cleanly; Camera Raw yields to Undo/Redo before the document changes";
        }
        finally
        {
            foreach (var window in windows)
            {
                foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
                Pump();
                window.CloseForHeadlessCheck();
                window._document?.Dispose();
            }
        }

        MainWindow Fixture()
        {
            var window = new MainWindow();
            windows.Add(window);
            var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
            var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(30 + x * 2), (byte)(20 + y * 2),
                    (byte)(80 + x / 8 % 2 * 40), 255));
            LayerPlacement.AddImage(document, bitmap, SKRectI.Create(0, 0, 64, 64), "Preview fixture", null);
            window._document = document;
            window.Show(window._open);
            window.Show();
            Pump();
            window.UpdateLayout();
            return window;
        }

        static string Pixels(CanvasDocument document) =>
            Convert.ToHexString(SHA256.HashData(document.Layers[0].Asset!.Image.GetPixelSpan()));
        static string Shown(MainWindow window) => Pixels(window._canvas.PreviewDocument ?? window._document!);
        static void Pump() => Dispatcher.UIThread.RunJobs();
        static void Press(Control control, string label)
        {
            control.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, L10n.T(label)))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
