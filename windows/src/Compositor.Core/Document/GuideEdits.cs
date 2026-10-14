using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Core.Document;

/// <summary>
/// The alignment guides: the lines put on the canvas to line things up against. A guide is one position on
/// one axis and nothing else, so adding, moving and taking one away is all there is to it.
/// </summary>
public static class GuideEdits
{
    /// <summary>How many guides a document may hold, as the Mac build's store allows.</summary>
    public const int MaxGuides = 1_000;

    /// <summary>A guide added at a document position, or null when the document holds as many as it may.</summary>
    public static Guid? Add(CanvasDocument document, GuideAxis axis, double position)
    {
        if (document.Guides.Count >= MaxGuides) return null;
        if (!Allowed(position)) return null;
        var guide = new CanvasGuide { ID = Guid.NewGuid(), Axis = axis, Position = position };
        document.Guides.Add(guide);
        return guide.ID;
    }

    /// <summary>Puts a guide at a new position. False when there is no such guide, or it is already there.</summary>
    public static bool Move(CanvasDocument document, Guid id, double position)
    {
        if (document.Guides.FirstOrDefault(entry => entry.ID == id) is not { } guide) return false;
        if (!Allowed(position) || guide.Position == position) return false;
        guide.Position = position;
        return true;
    }

    public static bool Remove(CanvasDocument document, Guid id) =>
        document.Guides.RemoveAll(entry => entry.ID == id) > 0;

    /// <summary>Takes every guide away, and says how many there were.</summary>
    public static int Clear(CanvasDocument document)
    {
        var count = document.Guides.Count;
        document.Guides.Clear();
        return count;
    }

    /// <summary>
    /// Whether a guide is still over the canvas, along the axis it is measured on. A guide left off the
    /// canvas is one that has been dragged away, which is how one is got rid of without a menu.
    /// </summary>
    public static bool OnCanvas(CanvasDocument document, CanvasGuide guide)
    {
        var extent = guide.Axis == GuideAxis.Vertical ? document.Width : document.Height;
        return guide.Position >= 0 && guide.Position <= extent;
    }

    /// <summary>
    /// The guide on this axis nearest to a position and no further off than <paramref name="tolerance"/>
    /// document units, or null — how a click finds the guide it is on.
    /// </summary>
    public static Guid? At(CanvasDocument document, GuideAxis axis, double position, double tolerance)
    {
        Guid? found = null;
        var nearest = tolerance;
        foreach (var guide in document.Guides)
        {
            if (guide.Axis != axis) continue;
            var distance = Math.Abs(guide.Position - position);
            if (distance > nearest) continue;
            nearest = distance;
            found = guide.ID;
        }
        return found;
    }

    /// <summary>
    /// Where a guide's line runs on the screen: the whole canvas across, at the guide's own place. The canvas
    /// is drawn at <paramref name="zoom"/> with <paramref name="originX"/>/<paramref name="originY"/> the
    /// document point in the view's top-left corner.
    /// </summary>
    public static (double X1, double Y1, double X2, double Y2) ScreenLine(CanvasGuide guide,
        int documentWidth, int documentHeight, double zoom, double originX, double originY)
    {
        var at = (guide.Position - (guide.Axis == GuideAxis.Vertical ? originX : originY)) * zoom;
        return guide.Axis == GuideAxis.Vertical
            ? (at, -originY * zoom, at, (documentHeight - originY) * zoom)
            : (-originX * zoom, at, (documentWidth - originX) * zoom, at);
    }

    /// <summary>
    /// A guide may sit anywhere the store would read back, which is well past the canvas on either side: a
    /// guide out in the pasteboard is where something is being lined up to, not a mistake.
    /// </summary>
    public static bool Allowed(double position) => double.IsFinite(position) && Math.Abs(position) <= 1_000_000;
}
