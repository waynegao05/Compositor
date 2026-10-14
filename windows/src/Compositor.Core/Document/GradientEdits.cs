using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Document;

/// <summary>The two shapes a gradient runs in: along the drag, or out from where it began.</summary>
public enum GradientShape
{
    /// <summary>The colour runs from one end of the drag to the other.</summary>
    Linear,

    /// <summary>The colour runs out from where the drag began, the end being on its rim.</summary>
    Radial,
}

/// <summary>
/// Filling a layer's pixels — or its mask — with a gradient. The colour runs from one end of the drag to the
/// other, and blends over the pixels already there at the given opacity, as Photoshop's does: at full
/// opacity it replaces what it covers, and a gradient that fades to transparent clears what it reaches.
/// </summary>
public static class GradientEdits
{
    /// <summary>Whether a drag is long enough to be a gradient; a click without one paints nothing.</summary>
    public static bool HasLine(SKPoint start, SKPoint end) =>
        Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) >= 0.5;

    /// <summary>
    /// The gradient a drag makes, over the layer's pixels or its mask. <paramref name="from"/> and
    /// <paramref name="to"/> are the two ends of the colour run, alpha included, so fading to transparent is
    /// simply an end with no alpha. False when there is nothing to fill or the drag is too short to paint.
    /// </summary>
    public static bool Fill(CanvasDocument document, Guid layerID, bool mask, SKPoint start, SKPoint end,
        SKColor from, SKColor to, double opacity, GradientShape shape)
    {
        if (opacity <= 0 || !HasLine(start, end) || document.Selection.IsEmpty) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { } layer) return false;

        SKBitmap pixels;
        Model.LayerTransform placement;
        if (mask)
        {
            if (layer.Mask is not { } held) return false;
            if (held.Asset.Width <= 1 && held.Asset.Height <= 1) BrushEdits.GrowMask(document, layerID);
            if (layer.Mask is not { } grown) return false;
            pixels = Bitmaps.Allocate(Bitmaps.MaskInfo(grown.Asset.Width, grown.Asset.Height));
            using (var canvas = new SKCanvas(pixels))
            {
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                using var source = SKImage.FromBitmap(grown.Asset.Image);
                canvas.DrawImage(source, SKRect.Create(0, 0, grown.Asset.Width, grown.Asset.Height),
                    new SKSamplingOptions(SKFilterMode.Nearest), paint);
            }
            placement = layer.MaskTransform;
        }
        else
        {
            if (!FillEdits.PixelSize(layer, out var width, out var height)) return false;
            pixels = new SKBitmap(Bitmaps.ColorInfo(width, height));
            pixels.Erase(SKColors.Transparent);
            if (layer.Asset is { } asset)
            {
                using var canvas = new SKCanvas(pixels);
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                using var source = SKImage.FromBitmap(asset.Image);
                canvas.DrawImage(source, SKRect.Create(0, 0, asset.Width, asset.Height),
                    new SKSamplingOptions(SKFilterMode.Nearest), paint);
            }
            placement = layer.Transform;
        }

        // The bitmap is handed to the layer — it must not be disposed here, or the layer would be left
        // holding freed pixels.
        var toDocument = BrushEdits.PixelToDocument(placement, pixels.Width, pixels.Height);
        if (!toDocument.TryInvert(out var toPixel))
        {
            pixels.Dispose();
            return false;
        }
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        SKBitmap? clip;
        try
        {
            clip = document.Selection.Coverage(region);
        }
        catch (InvalidOperationException)
        {
            pixels.Dispose();
            return false;
        }
        using (var _ = clip)
        {
            var span = clip is null ? default : clip.GetPixelSpan();
            if (!Paint(pixels, start, end, from, to, opacity, shape, toDocument, span, clip?.RowBytes ?? 0, region))
            {
                pixels.Dispose();
                return false;
            }
        }
        if (mask) layer.Mask = layer.Mask!.Replacing(ImportedImage.Create(pixels, layer.Mask.Asset.Name));
        else layer.Asset = ImportedImage.Create(pixels, layer.Asset?.Name ?? layer.Name);
        return true;
    }

    /// <summary>How far along the gradient a document point is: nothing along it, or nothing out from it.</summary>
    public static double Parameter(SKPoint start, SKPoint end, SKPoint point, GradientShape shape)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (double)dx * dx + (double)dy * dy;
        if (lengthSquared <= 0) return 0;
        if (shape == GradientShape.Radial)
        {
            var radius = Math.Sqrt(lengthSquared);
            var distance = Math.Sqrt(Math.Pow(point.X - start.X, 2) + Math.Pow(point.Y - start.Y, 2));
            return Math.Clamp(distance / radius, 0, 1);
        }
        // The point projected onto the run, so a colour band is square to it however the drag is angled.
        var along = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        return Math.Clamp(along, 0, 1);
    }

    /// <summary>The colour part of the way along: straight alpha, lerped so a faded end fades out.</summary>
    public static SKColor Blend(SKColor from, SKColor to, double parameter)
    {
        var t = Math.Clamp(parameter, 0, 1);
        byte Channel(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);
        return new SKColor(Channel(from.Red, to.Red), Channel(from.Green, to.Green), Channel(from.Blue, to.Blue),
            Channel(from.Alpha, to.Alpha));
    }

    private static bool Paint(SKBitmap pixels, SKPoint start, SKPoint end, SKColor from, SKColor to,
        double opacity, GradientShape shape, SKMatrix toDocument, ReadOnlySpan<byte> clip, int clipStride,
        SKRectI region)
    {
        var grayscale = pixels.ColorType == SKColorType.Gray8;
        var span = pixels.GetPixelSpan();
        var touched = false;
        for (var y = 0; y < pixels.Height; y++)
        {
            for (var x = 0; x < pixels.Width; x++)
            {
                var at = toDocument.MapPoint(x + 0.5f, y + 0.5f);
                var coverage = opacity;
                if (!clip.IsEmpty)
                {
                    var column = (int)Math.Floor(at.X) - region.Left;
                    var row = (int)Math.Floor(at.Y) - region.Top;
                    if (column < 0 || row < 0 || column >= region.Width || row >= region.Height) continue;
                    coverage *= clip[row * clipStride + column] / 255.0;
                }
                if (coverage <= 0) continue;
                var colour = Blend(from, to, Parameter(start, end, at, shape));
                var alpha = colour.Alpha / 255.0 * coverage;
                if (alpha <= 0) continue;
                touched = true;
                if (grayscale)
                {
                    // A mask holds one channel, and the gradient's own alpha fades it towards what was there.
                    var index = y * pixels.RowBytes + x;
                    var value = (colour.Red + colour.Green + colour.Blue) / 3.0;
                    span[index] = (byte)Math.Clamp(Math.Round(span[index] + (value - span[index]) * alpha), 0, 255);
                    continue;
                }
                var offset = y * pixels.RowBytes + x * 4;
                var under = span[offset + 3] / 255.0;
                var outAlpha = alpha + under * (1 - alpha);
                if (outAlpha <= 0) continue;
                var red = colour.Red / 255.0;
                var green = colour.Green / 255.0;
                var blue = colour.Blue / 255.0;
                for (var channel = 0; channel < 3; channel++)
                {
                    var straight = channel == 0 ? red : channel == 1 ? green : blue;
                    var behind = span[offset + channel] / 255.0 * under * (1 - alpha);
                    span[offset + channel] =
                        (byte)Math.Clamp(Math.Round((straight * alpha + behind) / outAlpha * 255), 0, 255);
                }
                span[offset + 3] = (byte)Math.Round(outAlpha * 255);
            }
        }
        return touched;
    }
}
