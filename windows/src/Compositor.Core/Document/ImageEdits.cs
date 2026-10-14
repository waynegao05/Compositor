using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Image size: the canvas and the pixels of every layer are resampled to a new size. Each layer is drawn
/// again into an upright box, because scaling a rotated rectangle by different amounts across and down
/// leans it, and a rectangle with an angle cannot say that — as the Mac build also concludes.
/// </summary>
public static class ImageEdits
{
    public static bool Resize(CanvasDocument document, int width, int height, double resolution,
        LayerSampling sampling = LayerSampling.HighQuality)
    {
        if (width is < 1 or > DocumentLimits.MaxSide || height is < 1 or > DocumentLimits.MaxSide) return false;
        if (!double.IsFinite(resolution) || resolution is < 1 or > 9600) return false;
        var sameSize = width == document.Width && height == document.Height;
        if (sameSize && resolution == document.Resolution) return false;
        if (sameSize)
        {
            // Only the resolution changed, so nothing is resampled.
            document.Resolution = resolution;
            return true;
        }
        if ((long)width * height > DocumentLimits.MaxSurfacePixels) return false;

        var sx = width / (double)document.Width;
        var sy = height / (double)document.Height;
        // Every new picture is made before anything is replaced, so a refusal leaves the document as it was.
        var made = new List<(ImageLayer Layer, ImportedImage? Asset, Model.LayerMask? Mask, Model.LayerTransform Transform,
            Model.LayerTransform? Placement, Model.LayerShape? Shape)>();
        long used = 0;
        foreach (var layer in document.Layers)
        {
            var box = Box(layer.Transform, layer.Asset?.Width ?? 1, layer.Asset?.Height ?? 1, sx, sy);
            if (box.Width < 1 || box.Height < 1
                || box.Width > DocumentLimits.MaxSide || box.Height > DocumentLimits.MaxSide) return false;
            if (layer.Asset is { } asset)
            {
                if ((long)box.Width * box.Height > DocumentLimits.DocumentPixelBudget - used) return false;
                used += (long)box.Width * box.Height;
            }
            var transform = new Model.LayerTransform(box.Left, box.Top, box.Width, box.Height, 0, false, false, sampling);
            // An upright live shape can be redrawn. Rotation and flips must be baked into its pixels;
            // redrawing its untransformed style would replace the visible shape with a different one.
            var drawn = layer.Transform.Rotation == 0 && !layer.Transform.FlipX && !layer.Transform.FlipY
                ? ShapeEdits.Scaled(layer, box.Width, box.Height) : null;
            var replacement = drawn?.Asset;
            if (replacement is null && layer.Asset is { } pixels)
            {
                var resampled = Resample(pixels.Image, layer.Transform, sx, sy, box, sampling, gray: false);
                if (resampled is null) return false;
                replacement = ImportedImage.Create(resampled, pixels.Name);
            }
            var mask = layer.Mask;
            Model.LayerMask? maskResult = null;
            if (mask is { } carried)
            {
                // A uniform mask says the same thing at any size, and one with a placement of its own keeps
                // its pixels; the placement itself scales with the canvas.
                if ((carried.Asset.Width == 1 && carried.Asset.Height == 1) || carried.Placement is not null)
                {
                    maskResult = carried.Replacing(carried.Asset);
                }
                else
                {
                    if ((long)box.Width * box.Height > DocumentLimits.DocumentPixelBudget - used) return false;
                    used += (long)box.Width * box.Height;
                    var coverage = Resample(carried.Asset.Image, layer.Transform, sx, sy, box, sampling, gray: true);
                    if (coverage is null) return false;
                    maskResult = carried.Replacing(ImportedImage.Create(coverage, carried.Asset.Name));
                }
            }
            Model.LayerTransform? placement = layer.Mask?.Placement is { } placed
                ? placed with { X = placed.X * sx, Y = placed.Y * sy, Width = placed.Width * sx, Height = placed.Height * sy }
                : null;
            made.Add((layer, replacement, maskResult, transform, placement, drawn?.Shape));
        }

        foreach (var guide in document.Guides)
        {
            guide.Position *= guide.Axis == GuideAxis.Vertical ? sx : sy;
        }
        foreach (var (layer, asset, mask, transform, placement, shape) in made)
        {
            if (asset is not null) layer.Asset = asset;
            layer.Shape = shape;
            if (mask is not null)
            {
                layer.Mask = mask;
                mask.Placement = placement;
            }
            layer.Transform = transform;
        }
        document.Selection = document.Selection.Scaled(sx, sy);
        document.Width = width;
        document.Height = height;
        document.Resolution = resolution;
        return true;
    }

    /// <summary>The upright box a layer's picture covers once the canvas has been scaled.</summary>
    private static SKRectI Box(Model.LayerTransform transform, int width, int height, double sx, double sy)
    {
        if (width <= 0 || height <= 0) return SKRectI.Create(0, 0, 1, 1);
        var scaled = Multiply(SKMatrix.CreateScale((float)sx, (float)sy), BrushEdits.PixelToDocument(transform, width, height));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[] { (0f, 0f), ((float)width, 0f), ((float)width, (float)height), (0f, (float)height) })
        {
            var corner = scaled.MapPoint(x, y);
            minX = Math.Min(minX, corner.X);
            minY = Math.Min(minY, corner.Y);
            maxX = Math.Max(maxX, corner.X);
            maxY = Math.Max(maxY, corner.Y);
        }
        var left = (int)Math.Floor(minX);
        var top = (int)Math.Floor(minY);
        return SKRectI.Create(left, top, (int)Math.Ceiling(maxX) - left, (int)Math.Ceiling(maxY) - top);
    }

    /// <summary>
    /// A layer's picture drawn again at the new size: the same transform, scaled, and moved so the new box's
    /// corner is the origin. A mask is gray coverage rather than color, so it keeps its own channels.
    /// </summary>
    private static SKBitmap? Resample(SKBitmap source, Model.LayerTransform transform, double sx, double sy,
        SKRectI box, LayerSampling sampling, bool gray)
    {
        if (box.Width <= 0 || box.Height <= 0) return null;
        var info = gray
            ? new SKImageInfo(box.Width, box.Height, SKColorType.Gray8, SKAlphaType.Opaque)
            : Bitmaps.ColorInfo(box.Width, box.Height);
        var bitmap = new SKBitmap(info);
        if (!bitmap.ReadyToDraw)
        {
            bitmap.Dispose();
            return null;
        }
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        {
            var matrix = Multiply(SKMatrix.CreateTranslation(-box.Left, -box.Top),
                Multiply(SKMatrix.CreateScale((float)sx, (float)sy),
                    BrushEdits.PixelToDocument(transform, source.Width, source.Height)));
            canvas.SetMatrix(matrix);
            using var image = SKImage.FromBitmap(source);
            using var paint = new SKPaint();
            canvas.DrawImage(image, SKRect.Create(0, 0, source.Width, source.Height), Sampling(sampling), paint);
        }
        return bitmap;
    }

    private static SKSamplingOptions Sampling(LayerSampling sampling) => sampling switch
    {
        LayerSampling.Nearest => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
        LayerSampling.Smooth => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        _ => new SKSamplingOptions(SKCubicResampler.Mitchell),
    };

    /// <summary>Two transforms one after the other: <paramref name="left"/> applied to <paramref name="right"/>'s result.</summary>
    private static SKMatrix Multiply(SKMatrix left, SKMatrix right) => new()
    {
        ScaleX = left.ScaleX * right.ScaleX + left.SkewX * right.SkewY,
        SkewX = left.ScaleX * right.SkewX + left.SkewX * right.ScaleY,
        TransX = left.ScaleX * right.TransX + left.SkewX * right.TransY + left.TransX,
        SkewY = left.SkewY * right.ScaleX + left.ScaleY * right.SkewY,
        ScaleY = left.SkewY * right.SkewX + left.ScaleY * right.ScaleY,
        TransY = left.SkewY * right.TransX + left.ScaleY * right.TransY + left.TransY,
        Persp2 = 1,
    };
}
