using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>A colour the canvas is extended with, as the canvas-size sheet offers.</summary>
public readonly record struct CanvasFill(double Red, double Green, double Blue);

/// <summary>
/// Changing the canvas. Crop and canvas size only move content — a layer keeps its own pixels and gains an
/// offset — which is why the Mac build shares one implementation between them, and so does this. Image size,
/// which resamples, is not here.
/// </summary>
public static class CanvasEdits
{
    /// <summary>The nine anchors, row-major from the top left, as the sheet lays them out.</summary>
    public const int CentreAnchor = 4;

    /// <summary>
    /// Where the old canvas lands in the new one. Flooring puts the extra pixel on the right and bottom when
    /// growing, and takes it from the left and top when shrinking around the middle.
    /// </summary>
    public static (int X, int Y) AnchorOffset(int anchor, int oldWidth, int oldHeight, int width, int height) =>
        ((int)Math.Floor((width - oldWidth) * (anchor % 3) / 2.0),
            (int)Math.Floor((height - oldHeight) * (anchor / 3) / 2.0));

    /// <summary>Resizes the canvas around an anchor, moving layers, placed masks and guides with it.</summary>
    public static bool Resize(CanvasDocument document, int width, int height, int anchor = CentreAnchor,
        CanvasFill? fill = null)
    {
        if (anchor is < 0 or > 8) return false;
        var (x, y) = AnchorOffset(anchor, document.Width, document.Height, width, height);
        return Apply(document, width, height, x, y, fill);
    }

    /// <summary>Crops: the rectangle becomes the canvas, and everything moves by its corner.</summary>
    public static bool Crop(CanvasDocument document, SKRectI rect) =>
        rect.Width > 0 && rect.Height > 0
        && Apply(document, rect.Width, rect.Height, -rect.Left, -rect.Top, null);

    private static bool Apply(CanvasDocument document, int width, int height, int dx, int dy, CanvasFill? fill)
    {
        if (width is < 1 or > DocumentLimits.MaxSide || height is < 1 or > DocumentLimits.MaxSide) return false;
        if (fill is { } colour && !(colour.Red is >= 0 and <= 1 && colour.Green is >= 0 and <= 1 && colour.Blue is >= 0 and <= 1))
        {
            return false;
        }
        var oldWidth = document.Width;
        var oldHeight = document.Height;
        if (width == oldWidth && height == oldHeight && dx == 0 && dy == 0) return false;

        // Every new place is worked out first: a transform that would leave the document's range is a
        // refusal, and a refusal has to leave the document exactly as it was.
        var moved = new List<Model.LayerTransform>(document.Layers.Count);
        foreach (var layer in document.Layers)
        {
            var transform = layer.Transform with { X = layer.Transform.X + dx, Y = layer.Transform.Y + dy };
            if (!transform.IsValid) return false;
            moved.Add(transform);
        }

        for (var index = 0; index < document.Layers.Count; index++)
        {
            var layer = document.Layers[index];
            layer.Transform = moved[index];
            if (layer.Mask?.Placement is { } placement)
            {
                layer.Mask.Placement = placement with { X = placement.X + dx, Y = placement.Y + dy };
            }
        }
        foreach (var guide in document.Guides)
        {
            if (guide.Axis == GuideAxis.Vertical) guide.Position += dx;
            else guide.Position += dy;
        }

        if (fill is { } extension && (width > oldWidth || height > oldHeight))
        {
            document.Layers.Insert(0, Extension(width, height, dx, dy, oldWidth, oldHeight, extension));
        }

        document.Selection = document.Selection.Translated(dx, dy);
        document.Width = width;
        document.Height = height;
        return true;
    }

    /// <summary>
    /// The bottom layer a grown canvas is filled with: the whole of the new canvas in the colour, with the
    /// old canvas's place left transparent so the artwork above shows through.
    /// </summary>
    private static ImageLayer Extension(int width, int height, int dx, int dy, int oldWidth, int oldHeight,
        CanvasFill colour)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(new SKColor((byte)Math.Round(colour.Red * 255), (byte)Math.Round(colour.Green * 255),
            (byte)Math.Round(colour.Blue * 255)));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.ClipRect(SKRect.Create(dx, dy, oldWidth, oldHeight));
            canvas.Clear(SKColors.Transparent);
        }
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Canvas Extension"),
            new Model.LayerTransform(0, 0, width, height), "Canvas Extension");
    }
}
