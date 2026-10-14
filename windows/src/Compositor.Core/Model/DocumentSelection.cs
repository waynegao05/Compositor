using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>
/// What an edit is allowed to touch: a path over the document, with a feather, or nothing at all.
/// <para>
/// The path is in document pixels with a top-left origin, and coverage is its gray level — white where a
/// pixel is selected, a partial value on an antialiased or feathered edge, black outside. A history
/// snapshot shares the path rather than copying it, so nothing here disposes it; the garbage collector
/// releases the ones an edit replaces, as the Mac build lets its CGPath go out of scope.
/// </para>
/// </summary>
public sealed class DocumentSelection
{
    private DocumentSelection(SKPath? path, bool antialiased, double feather)
    {
        Path = path;
        Antialiased = antialiased;
        Feather = feather;
    }

    /// <summary>The outline, or null when there is no selection at all.</summary>
    public SKPath? Path { get; }

    /// <summary>Whether the outline is drawn with a soft edge of its own, before the feather.</summary>
    public bool Antialiased { get; }

    /// <summary>How far the edge fades, in document pixels. Zero is a hard edge.</summary>
    public double Feather { get; }

    /// <summary>Nothing is selected, so an edit may touch the whole document.</summary>
    public static DocumentSelection All { get; } = new(null, true, 0);

    /// <summary>A selection around a shape, which may enclose nothing at all.</summary>
    public static DocumentSelection FromPath(SKPath path, bool antialiased = true, double feather = 0) =>
        new(path, antialiased, feather);

    /// <summary>A rectangle of the document, held to its bounds. An empty rectangle selects nothing.</summary>
    public static DocumentSelection Rectangular(SKRectI rect, int width, int height)
    {
        var held = SKRectI.Intersect(rect, SKRectI.Create(0, 0, width, height));
        if (held.Width <= 0 || held.Height <= 0) return FromPath(new SKPath());
        using var builder = new SKPathBuilder();
        builder.AddRect(SKRect.Create(held.Left, held.Top, held.Width, held.Height), SKPathDirection.Clockwise);
        return FromPath(builder.Detach());
    }

    /// <summary>Whether the outline encloses nothing.</summary>
    public bool IsEmpty => Path is { } path
        && (path.IsEmpty || path.Bounds.Width <= 0 || path.Bounds.Height <= 0);

    /// <summary>Whether a whole pixel is inside the selection. A softened edge is not accounted for here.</summary>
    public bool Contains(int x, int y) => Path is not { } path || path.Contains(x + 0.5f, y + 0.5f);

    /// <summary>The same outline with a softer edge.</summary>
    public DocumentSelection WithFeather(double feather) => new(Path, Antialiased, feather);

    /// <summary>The same softening around a different outline, as Modify and Inverse keep what they were given.</summary>
    public DocumentSelection WithPath(SKPath path) => new(path, Antialiased, Feather);

    /// <summary>
    /// The same outline moved by whole pixels. It is not held to the canvas, so it can be dragged off the
    /// edge and back intact, as the Mac build allows.
    /// </summary>
    public DocumentSelection Translated(double dx, double dy)
    {
        if (Path is not { } path) return this;
        using var builder = new SKPathBuilder();
        builder.AddPath(path, SKPathAddMode.Append);
        var moved = builder.Detach();
        moved.Transform(SKMatrix.CreateTranslation((float)dx, (float)dy));
        return new DocumentSelection(moved, Antialiased, Feather);
    }

    /// <summary>The outline scaled with an image-size change. Feather remains a distance in document pixels.</summary>
    public DocumentSelection Scaled(double sx, double sy)
    {
        if (Path is not { } path) return this;
        using var builder = new SKPathBuilder();
        builder.AddPath(path, SKPathAddMode.Append);
        var scaled = builder.Detach();
        scaled.Transform(SKMatrix.CreateScale((float)sx, (float)sy));
        return new DocumentSelection(scaled, Antialiased, Feather);
    }

    /// <summary>
    /// The rectangle of the canvas a selection can affect: its own bounds, grown by what a feather reaches
    /// and by a pixel for the antialiased edge. An empty rectangle means the selection affects nothing.
    /// </summary>
    public SKRectI CoverageRect(int canvasWidth, int canvasHeight)
    {
        if (Path is not { } path || IsEmpty) return SKRectI.Create(0, 0, 0, 0);
        var reach = (float)Math.Ceiling(Feather * 2) + 1;
        var bounds = path.Bounds;
        var needed = SKRect.Create((float)Math.Floor(bounds.Left - reach), (float)Math.Floor(bounds.Top - reach),
            (float)Math.Ceiling(bounds.Right + reach) - (float)Math.Floor(bounds.Left - reach),
            (float)Math.Ceiling(bounds.Bottom + reach) - (float)Math.Floor(bounds.Top - reach));
        var held = SKRectI.Intersect(SKRectI.Create((int)needed.Left, (int)needed.Top,
            (int)needed.Width, (int)needed.Height), SKRectI.Create(0, 0, canvasWidth, canvasHeight));
        return held.Width <= 0 || held.Height <= 0 ? SKRectI.Create(0, 0, 0, 0) : held;
    }

    /// <summary>
    /// Gray coverage over a region of the document, white where selected. Null when there is no selection at
    /// all, which means nothing is being held back.
    /// </summary>
    public SKBitmap? Coverage(SKRectI region)
    {
        if (Path is not { } path) return null;
        var coverage = Bitmaps.Allocate(Bitmaps.MaskInfo(Math.Max(1, region.Width), Math.Max(1, region.Height)));
        coverage.Erase(SKColors.Black);
        if (region.Width <= 0 || region.Height <= 0 || path.IsEmpty) return coverage;
        using (var canvas = new SKCanvas(coverage))
        {
            canvas.Translate(-region.Left, -region.Top);
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Fill,
                IsAntialias = Antialiased || Feather > 0,
            };
            canvas.DrawPath(path, paint);
        }
        if (Feather > 0)
        {
            // A feathered edge fades either side of the outline, as Photoshop's does. The fade is worked out
            // here, in gray levels, rather than by a paint filter, so a feathered edge always fades the way a
            // mask's coverage has to.
            Pixels.GaussianBlur.Clamped(coverage.GetPixelSpan(), coverage.Width, coverage.Height, 1,
                coverage.RowBytes, Feather / 2);
        }
        return coverage;
    }

    /// <summary>Whether two selections would let an edit touch the same pixels, compared by outline.</summary>
    public bool Matches(DocumentSelection other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (Path is null || other.Path is null) return Path is null && other.Path is null;
        if (Antialiased != other.Antialiased || Feather != other.Feather) return false;
        if (Path.Bounds != other.Path.Bounds || Path.PointCount != other.Path.PointCount) return false;
        var points = Path.Points;
        var mine = other.Path.Points;
        for (var index = 0; index < points.Length; index++)
        {
            if (!points[index].Equals(mine[index])) return false;
        }
        return true;
    }
}
