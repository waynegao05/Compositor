using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>How the geometry group warps the picture: in perspective, or the gentler rectilinear way.</summary>
public enum GeometryProjection
{
    Perspective,
    Rectilinear,
}

/// <summary>
/// The Camera Raw filter's Geometry group: the picture turned, keystoned and zoomed inside its own pixels.
/// The Mac build works it out as Core Image's perspective transform over four corners, which is what this does.
/// <para>
/// Guided upright is part of it: lines drawn on the picture are read into a turn and, when one of them is
/// steep, a keystone, and those are added to whatever the sliders say.
/// </para>
/// </summary>
public sealed class CameraRawGeometrySettings
{
    /// <summary>How far each slider reaches; the turn is gentler than the rest.</summary>
    public const double ToneRange = 100;
    public const double RotateRange = 45;

    public GeometryProjection Projection { get; set; }

    /// <summary>Keystone: positive pulls the top of the picture wider and the bottom narrower.</summary>
    public double Vertical { get; set; }
    /// <summary>Keystone the other way round, over the sides.</summary>
    public double Horizontal { get; set; }
    /// <summary>−45 to 45 degrees.</summary>
    public double Rotate { get; set; }
    /// <summary>Stretches the picture one way and squeezes it the other, around its middle.</summary>
    public double Aspect { get; set; }
    /// <summary>Zoom in, so the edges are lost rather than left empty.</summary>
    public double Scale { get; set; }
    public double OffsetX { get; set; }
    /// <summary>Positive spreads the picture's top up and its bottom down, as the Mac build's does.</summary>
    public double OffsetY { get; set; }
    /// <summary>Trims the empty wedges a turn or keystone leaves, fitting what is left to the same size.</summary>
    public bool ConstrainCrop { get; set; }

    /// <summary>Whether the lines drawn on the picture are read, and the lines themselves.</summary>
    public CameraRawUprightMode Upright { get; set; }
    public List<CameraRawGeometryGuide> Guides { get; } = [];

    /// <summary>Whether the drawn lines ask for anything: a Guided choice with no usable line must not warp.</summary>
    private bool UsesGuides => Upright == CameraRawUprightMode.Guided && Guides.Any(guide => guide.IsUsable);

    /// <summary>Whether anything here would move the picture.</summary>
    public bool Adjusts =>
        UsesGuides || Vertical != 0 || Horizontal != 0 || Rotate != 0 || Aspect != 0 || Scale != 0
        || OffsetX != 0 || OffsetY != 0;

    public bool IsValid =>
        Within(Vertical, ToneRange) && Within(Horizontal, ToneRange) && Within(Aspect, ToneRange)
        && Within(Scale, ToneRange) && Within(OffsetX, ToneRange) && Within(OffsetY, ToneRange)
        && Within(Rotate, RotateRange) && Projection is >= GeometryProjection.Perspective and <= GeometryProjection.Rectilinear
        && Upright is >= CameraRawUprightMode.Off and <= CameraRawUprightMode.Guided
        && Guides.All(guide => Fraction(guide.StartX) && Fraction(guide.StartY)
            && Fraction(guide.EndX) && Fraction(guide.EndY));

    /// <summary>Whether a drawn guide's number is a place on the picture rather than off it.</summary>
    private static bool Fraction(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    /// <summary>The same settings with every slider brought inside its range, and every stub of a line dropped.</summary>
    public CameraRawGeometrySettings Normalized()
    {
        var result = new CameraRawGeometrySettings
        {
            Projection = Projection,
            Vertical = Clamp(Vertical, ToneRange),
            Horizontal = Clamp(Horizontal, ToneRange),
            Rotate = Clamp(Rotate, RotateRange),
            Aspect = Clamp(Aspect, ToneRange),
            Scale = Clamp(Scale, ToneRange),
            OffsetX = Clamp(OffsetX, ToneRange),
            OffsetY = Clamp(OffsetY, ToneRange),
            ConstrainCrop = ConstrainCrop,
            // Carried through rather than dropped: Corners works from what this returns, so a field left out
            // here is a field the geometry silently ignores.
            Upright = Upright,
        };
        result.Guides.AddRange(Guides.Where(guide => guide.IsUsable));
        return result;
    }

    /// <summary>
    /// The amounts the geometry really applies: the sliders, plus whatever the drawn lines ask for. The lines
    /// are read here rather than in the sliders, so turning Upright off puts the picture back to the sliders'
    /// own amounts without clearing them.
    /// </summary>
    private (double Vertical, double Horizontal, double Rotate) Effective(int width, int height)
    {
        if (Upright != CameraRawUprightMode.Guided) return (Vertical, Horizontal, Rotate);
        var (vertical, horizontal, rotate) = GuidedUpright.Corrections(Guides, width, height);
        return (Vertical + vertical, Horizontal + horizontal, Rotate + rotate);
    }

    /// <summary>
    /// Where the picture's four corners are taken to, in the picture's own pixels, top left first and round
    /// clockwise. Core Image measures up from the bottom left, so the corners are worked out on that grid and
    /// turned over at the end; the turn is about the picture's middle, and aspect and zoom squeeze towards it.
    /// </summary>
    public SKPoint[] Corners(int width, int height)
    {
        var settings = Normalized();
        double w = width, h = height;
        // What the drawn lines ask for is added to the sliders here, so a guided picture is worked out on the
        // same corners as one the sliders were moved on.
        var (down, across, turn) = settings.Effective(width, height);
        var strength = settings.Projection == GeometryProjection.Perspective ? 1.0 : 0.55;
        var keystone = down / 100 * w * 0.18 * strength;
        var sides = across / 100 * h * 0.18 * strength;
        var aspect = 1 + settings.Aspect / 200;
        var zoom = 1 + settings.Scale / 100;
        var shiftX = settings.OffsetX / 100 * w * 0.15;
        var shiftY = settings.OffsetY / 100 * h * 0.15;
        var (topLeft, topRight, bottomRight, bottomLeft) = (
            (X: -keystone + shiftX, Y: h + shiftY),
            (X: w + keystone + shiftX, Y: h + shiftY),
            (X: w + sides + shiftX, Y: -shiftY),
            (X: -sides + shiftX, Y: -shiftY));
        var centre = (X: w / 2 + shiftX, Y: h / 2 + shiftY);
        var radians = turn * Math.PI / 180;

        (double X, double Y) Turn((double X, double Y) point)
        {
            var dx = point.X - centre.X;
            var dy = point.Y - centre.Y;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            return (centre.X + dx * cosine - dy * sine, centre.Y + dx * sine + dy * cosine);
        }

        (double X, double Y) Squeeze((double X, double Y) point) =>
            (centre.X + (point.X - centre.X) * aspect, centre.Y + (point.Y - centre.Y) / aspect);

        (double X, double Y) Enlarge((double X, double Y) point) =>
            (centre.X + (point.X - centre.X) * zoom, centre.Y + (point.Y - centre.Y) * zoom);

        (double X, double Y) Place((double X, double Y) point)
        {
            var turned = Turn(point);
            var squeezed = aspect == 1 ? turned : Squeeze(turned);
            var enlarged = zoom == 1 ? squeezed : Enlarge(squeezed);
            return (enlarged.X, h - enlarged.Y);
        }

        return [Flat(Place(topLeft)), Flat(Place(topRight)), Flat(Place(bottomRight)), Flat(Place(bottomLeft))];
    }

    private static SKPoint Flat((double X, double Y) point) => new((float)point.X, (float)point.Y);

    private static bool Within(double value, double range) => double.IsFinite(value) && Math.Abs(value) <= range;

    private static double Clamp(double value, double range) =>
        double.IsFinite(value) ? Math.Clamp(value, -range, range) : 0;
}

/// <summary>
/// The geometry group as one edit: a layer's own pixels are warped into the shape the corners make, clipped to
/// the layer's own box, and — with Constrain Crop — trimmed to what is left and fitted to the size it was.
/// The layer's place on the document does not move: the picture turns inside its own rectangle.
/// </summary>
public static class GeometryEdits
{
    /// <summary>
    /// Warps a layer by <paramref name="geometry"/>. False when there is no such layer, when the settings ask
    /// for nothing, or when the shape cannot be made — in which case the layer is left as it was.
    /// </summary>
    public static bool Apply(CanvasDocument document, Guid layerID, CameraRawGeometrySettings geometry)
    {
        var settings = geometry.Normalized();
        if (!settings.Adjusts) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: { } asset } layer) return false;
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;

        using var source = Premultiplied(asset.Image);
        var corners = settings.Corners(width, height);
        if (DistortWarp.Warp(source, new LayerTransform(0, 0, width, height), corners) is not { } warped) return false;
        using (warped.Image)
        {
            // The Mac build renders the warped picture into a box the size it started at, so what a turn or a
            // keystone takes outside the picture is simply lost.
            using var boxed = FilterSurface.Allocate(width, height);
            using (var canvas = new SKCanvas(boxed))
            {
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                // The warped picture is drawn where it belongs on the layer's own grid, so what a turn or a
                // keystone takes outside that grid is simply lost.
                canvas.DrawBitmap(warped.Image, (float)warped.Transform.X, (float)warped.Transform.Y,
                    new SKSamplingOptions(SKFilterMode.Linear), paint);
                canvas.Flush();
            }
            using var fitted = settings.ConstrainCrop ? Fitted(boxed, width, height) : null;
            var stored = Stored(fitted ?? boxed);
            layer.Asset = ImportedImage.Create(stored, asset.Name);
            return true;
        }
    }

    /// <summary>
    /// The warped picture cut back to what it covers and fitted into a box the size it was, which is what
    /// Constrain Crop leaves: the empty wedges a turn or a keystone makes are not kept.
    /// </summary>
    private static SKBitmap? Fitted(SKBitmap boxed, int width, int height)
    {
        Span<int> edges = stackalloc int[4];
        BrushPixels.AlphaBounds(boxed.GetPixelSpan(), boxed.Width, boxed.Height, boxed.RowBytes, edges);
        var crop = SKRectI.Create(edges[0], edges[1], edges[2] - edges[0], edges[3] - edges[1]);
        if (crop.Width < 1 || crop.Height < 1) return null;
        if (crop.Width >= width && crop.Height >= height) return null;
        var fitted = FilterSurface.Allocate(width, height);
        using (var canvas = new SKCanvas(fitted))
        {
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(width / (double)crop.Width, height / (double)crop.Height);
            var draw = SKRect.Create(
                (float)((width - crop.Width * scale) / 2), (float)((height - crop.Height * scale) / 2),
                (float)(crop.Width * scale), (float)(crop.Height * scale));
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
            canvas.DrawBitmap(boxed, SKRect.Create(crop.Left, crop.Top, crop.Width, crop.Height), draw,
                new SKSamplingOptions(SKFilterMode.Linear), paint);
            canvas.Flush();
        }
        return fitted;
    }

    /// <summary>The layer's pixels in the premultiplied form a resample wants, so a soft edge blends right.</summary>
    private static SKBitmap Premultiplied(SKBitmap image) => Bitmaps.Premultiplied(image);

    /// <summary>Warped pixels as the straight-alpha ones a layer is held in.</summary>
    private static SKBitmap Stored(SKBitmap warped)
    {
        var result = FilterSurface.Stored(warped.Width, warped.Height);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(warped, SKRect.Create(0, 0, warped.Width, warped.Height),
            new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return result;
    }
}
