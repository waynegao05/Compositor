using Avalonia.Threading;
using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Exercise mask color controls and first fills through the window's actual editing handlers.</summary>
    internal static string CanvasRegressionSelfCheck()
    {
        var window = new MainWindow();
        using var document = LayerPlacement.NewDocument(32, 32)!;
        try
        {
            var layer = document.Layers[0];
            Require(FillEdits.Fill(document, layer.ID, SKColors.Red), "fixture did not fill");
            layer.Mask = LayerMask.Solid(true);
            var original = layer.Asset!.Image;
            window._document = document;
            window.Show(window._open);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Reselect(layer.ID);
            window.SetTool(Tool.Brush);
            window._options.Brush = new BrushSettings(Diameter: 8, Red: 0, Green: 1, Blue: 0);
            window.SetErasing(false);
            window.SetPaintingMask(true);
            Require(window._optionsBar.Shows("mask"), "mask color menu is missing");

            window._optionsBar.ChooseMaskPaint(false);
            Require(window._options.PaintOnMask && window._options.Erase && window._canvas.Brush.Erasing,
                "Hide changed the target or disagreed with Erase");
            window.Painted([new SKPoint(16.5f, 16.5f)]);
            Require(ReferenceEquals(original, layer.Asset!.Image) && layer.Mask!.Asset.Image.GetPixel(16, 16).Red == 0,
                "Hide changed original pixels or failed to paint black on the mask");

            window._optionsBar.ChooseMaskPaint(true);
            Require(window._options.PaintOnMask && !window._options.Erase && !window._canvas.Brush.Erasing,
                "Reveal changed the target or disagreed with Paint");
            window.Painted([new SKPoint(16.5f, 16.5f)]);
            Require(ReferenceEquals(original, layer.Asset!.Image) && layer.Mask!.Asset.Image.GetPixel(16, 16).Red == 255,
                "Reveal changed original pixels or failed to paint white on the mask");

            window.SetErasing(true);
            Require(!window._optionsBar.MaskPaintReveals && window._options.PaintOnMask,
                "Erase did not update the mask color menu");
            window.SetErasing(false);
            Require(window._optionsBar.MaskPaintReveals && window._options.PaintOnMask,
                "Paint did not update the mask color menu");

            window.SetPaintingMask(false);
            Require(!window._optionsBar.Shows("mask"), "mask color menu is shown while painting image pixels");
            window.NewBlankLayer();
            var filled = document.Layers.Single(one => one.ID == window.Selected);
            window.FillPixels(SKColors.Blue, "Fill Foreground");
            Require(filled.Asset?.Image.GetPixel(16, 16) == SKColors.Blue, "new blank layer could not be filled");
            window.NewBlankLayer();
            var gradient = document.Layers.Single(one => one.ID == window.Selected);
            window._options.GradientToBackground = true;
            window.GradientFinished(new SKPoint(0, 0), new SKPoint(32, 0));
            Require(gradient.Asset is { } asset && asset.Image.GetPixel(0, 16) != asset.Image.GetPixel(31, 16),
                "new blank layer did not take the gradient");
            return "A01/A04: mask Hide and Reveal preserve original pixels and agree with Erase; blank-layer Fill and Gradient work";
        }
        finally
        {
            window.CloseForHeadlessCheck();
            window._canvas.Document = null;
            Dispatcher.UIThread.RunJobs();
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
