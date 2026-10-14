using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Document;

/// <summary>The eight scale handles around a box, and the grip that turns it.</summary>
/// <summary>
/// What a moving or cropped thing lines up with, one switch each, as the View menu offers them. None of it
/// turns the snapping off; it takes away the things to snap to.
/// </summary>
[Flags]
public enum SnapTo
{
    None = 0,

    /// <summary>The canvas' own edges and middle.</summary>
    Canvas = 1,

    /// <summary>The guides put on the canvas.</summary>
    Guides = 2,

    /// <summary>The boxes of the other layers that hold pixels.</summary>
    Layers = 4,

    /// <summary>The layout grid, when one is being shown.</summary>
    Grid = 8,

    All = Canvas | Guides | Layers | Grid,
}

public enum TransformHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,

    /// <summary>The grip above the top edge, which turns the box about its middle.</summary>
    Rotate,
}

/// <summary>
/// The transform inspector's arithmetic: where the handles sit, which one a click has hold of, and the box a
/// drag works out. Every drag is measured from the transform the drag began with rather than compounding, so
/// dragging back and forth returns the layer to where it started.
/// </summary>
public static class TransformEdits
{
    /// <summary>How far above the top edge the turning grip sits, in screen pixels.</summary>
    public const double RotateGrip = 28;

    /// <summary>How close a click has to be, in screen pixels, to take hold of a handle or an edge.</summary>
    public const double Grab = 10;

    /// <summary>The eight scale handles as units of the box, in the order of <see cref="TransformHandle"/>.</summary>
    private static readonly (double X, double Y)[] Units =
    [
        (0, 0), (0.5, 0), (1, 0), (1, 0.5), (1, 1), (0.5, 1), (0, 1), (0, 0.5),
    ];

    /// <summary>Where one of the eight scale handles sits on the document.</summary>
    public static SKPoint Position(LayerTransform box, TransformHandle handle)
    {
        var (x, y) = Units[(int)handle];
        return box.Point(x, y);
    }

    /// <summary>
    /// Where the turning grip sits: <paramref name="reach"/> document pixels out from the middle of the top
    /// edge, at right angles to it, so it keeps its distance on screen at any zoom.
    /// </summary>
    public static SKPoint RotatePosition(LayerTransform box, double reach)
    {
        var top = box.Point(0.5, 0);
        return new SKPoint(top.X + (float)(Math.Sin(box.Radians) * reach),
            top.Y - (float)(Math.Cos(box.Radians) * reach));
    }

    /// <summary>
    /// Which of the eight handles a click takes hold of, ignoring the turning grip — for a frame that is
    /// dragged about but not turned, as a crop is.
    /// </summary>
    public static TransformHandle? HandleAt(LayerTransform box, SKPoint point, double tolerance)
    {
        var found = HandleAt(box, point, tolerance, double.NaN);
        return found == TransformHandle.Rotate ? null : found;
    }

    /// <summary>
    /// Which handle a click takes hold of, within <paramref name="tolerance"/> document pixels: a handle
    /// first, then the edge between two of them — which is that edge's middle handle, so the whole side can
    /// be dragged.
    /// </summary>
    public static TransformHandle? HandleAt(LayerTransform box, SKPoint point, double tolerance, double grip)
    {
        if (!double.IsNaN(grip) && Near(point, RotatePosition(box, grip), tolerance)) return TransformHandle.Rotate;
        for (var index = 0; index < Units.Length; index++)
        {
            if (Near(point, Position(box, (TransformHandle)index), tolerance)) return (TransformHandle)index;
        }
        // The edges, as pairs of corners going round the box: top, right, bottom, left.
        foreach (var (start, end, middle) in new[]
                 {
                     (TransformHandle.TopLeft, TransformHandle.TopRight, TransformHandle.Top),
                     (TransformHandle.TopRight, TransformHandle.BottomRight, TransformHandle.Right),
                     (TransformHandle.BottomRight, TransformHandle.BottomLeft, TransformHandle.Bottom),
                     (TransformHandle.BottomLeft, TransformHandle.TopLeft, TransformHandle.Left),
                 })
        {
            var a = Position(box, start);
            var b = Position(box, end);
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0) continue;
            var along = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
            if (along is < 0 or > 1) continue;
            var across = Math.Sqrt(Math.Pow(point.X - a.X - along * dx, 2) + Math.Pow(point.Y - a.Y - along * dy, 2));
            if (across <= tolerance) return middle;
        }
        return null;
    }

    /// <summary>The four corners of the box on the document, from its top left round to its bottom left.</summary>
    public static SKPoint[] Corners(LayerTransform box) =>
    [
        Position(box, TransformHandle.TopLeft),
        Position(box, TransformHandle.TopRight),
        Position(box, TransformHandle.BottomRight),
        Position(box, TransformHandle.BottomLeft),
    ];

    /// <summary>The upright box a rotated rectangle covers.</summary>
    public static SKRect Bounds(LayerTransform box)
    {
        var corners = Corners(box);
        var minX = corners.Min(corner => corner.X);
        var minY = corners.Min(corner => corner.Y);
        var maxX = corners.Max(corner => corner.X);
        var maxY = corners.Max(corner => corner.Y);
        return new SKRect(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// A move of the whole box. Shift keeps the drag on one axis, the longer one winning, as the Mac build
    /// keeps it.
    /// </summary>
    public static LayerTransform Move(LayerTransform original, double dx, double dy, bool axisLock = false)
    {
        if (axisLock)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }
        var moved = original with { X = original.X + dx, Y = original.Y + dy };
        return moved.IsValid ? moved : original;
    }

    /// <summary>
    /// The box a scale handle drags out: the handle follows the pointer while the opposite corner — or, with
    /// <paramref name="fromCentre"/>, the middle — stays put. Dragging a handle past the far side turns the
    /// layer over rather than stopping at nothing, as a negative scale would. <paramref name="lockRatio"/>
    /// keeps the sides in proportion; Shift is what asks for that, and Shift also releases it, as in the Mac
    /// build.
    /// </summary>
    public static LayerTransform Resize(LayerTransform original, TransformHandle handle, SKPoint start,
        SKPoint point, bool lockRatio, bool fromCentre)
    {
        if (handle == TransformHandle.Rotate) return original;
        var (hx, hy) = Units[(int)handle];
        var anchorX = fromCentre ? 0.5 : 1 - hx;
        var anchorY = fromCentre ? 0.5 : 1 - hy;
        var anchor = original.Point(anchorX, anchorY);
        // The handle's own place at the start of the drag, plus how far the pointer has come: a grab never
        // jumps the box, and the drag is measured from where the drag began.
        var initial = original.Point(hx, hy);
        var dx = initial.X + point.X - start.X - anchor.X;
        var dy = initial.Y + point.Y - start.Y - anchor.Y;
        // Moving towards the middle covers half the size on each side, so it counts double.
        var span = fromCentre ? 2.0 : 1.0;
        var cos = Math.Cos(original.Radians);
        var sin = Math.Sin(original.Radians);
        var localX = (dx * cos + dy * sin) * span;
        var localY = (-dx * sin + dy * cos) * span;
        // Which way this handle lies from the middle: an edge handle has one of these at zero.
        var sx = hx * 2 - 1;
        var sy = hy * 2 - 1;
        var rawWidth = sx == 0 ? original.Width : localX * sx;
        var rawHeight = sy == 0 ? original.Height : localY * sy;
        var mirroredX = rawWidth < 0;
        var mirroredY = rawHeight < 0;
        var width = Math.Max(1, Math.Abs(rawWidth));
        var height = Math.Max(1, Math.Abs(rawHeight));
        if (lockRatio)
        {
            double factor;
            if (sx == 0) factor = height / original.Height;
            else if (sy == 0) factor = width / original.Width;
            else
            {
                // The drag projected onto the box's own diagonal keeps the sides in proportion.
                factor = Math.Max(1 / Math.Min(original.Width, original.Height),
                    (localX * sx * original.Width + localY * sy * original.Height)
                    / (original.Width * original.Width + original.Height * original.Height));
            }
            width = original.Width * factor;
            height = original.Height * factor;
        }
        var sized = original with
        {
            Width = width,
            Height = height,
            FlipX = mirroredX ? !original.FlipX : original.FlipX,
            FlipY = mirroredY ? !original.FlipY : original.FlipY,
        };
        // Turned over, the box lies on the other side of the anchor.
        var offsetX = (0.5 - anchorX) * width * (mirroredX ? -1 : 1);
        var offsetY = (0.5 - anchorY) * height * (mirroredY ? -1 : 1);
        var centreX = anchor.X + offsetX * cos - offsetY * sin;
        var centreY = anchor.Y + offsetX * sin + offsetY * cos;
        var placed = sized with { X = centreX - width / 2, Y = centreY - height / 2 };
        return placed.IsValid ? placed : original;
    }

    /// <summary>
    /// The turn a drag works out, measured about the box's middle from where the drag began.
    /// <paramref name="steps"/> keeps it to whole multiples of fifteen degrees, as Shift does.
    /// </summary>
    public static LayerTransform Rotate(LayerTransform original, SKPoint start, SKPoint point, bool steps)
    {
        var centreX = original.CenterX;
        var centreY = original.CenterY;
        var delta = Math.Atan2(point.Y - centreY, point.X - centreX)
            - Math.Atan2(start.Y - centreY, start.X - centreX);
        var rotation = original.Rotation + delta * 180 / Math.PI;
        if (steps) rotation = Math.Round(rotation / 15) * 15;
        var turned = original with { Rotation = rotation };
        return turned.IsValid ? turned : original;
    }

    /// <summary>
    /// What a group transform moves: every visible layer that holds pixels and is either selected or inside a
    /// folder that is. A folder is not moved itself — its rectangle is only what a mask covers.
    /// </summary>
    public static List<ImageLayer> GroupMembers(CanvasDocument document, IReadOnlyCollection<Guid> ids)
    {
        var wanted = ids.ToHashSet();
        var visible = document.EffectiveVisibleIDs();
        return document.Layers.Where(layer => layer.Asset is not null && !layer.IsGroup && visible.Contains(layer.ID))
            .Where(layer => Inside(document, layer, wanted))
            .ToList();
    }

    /// <summary>
    /// The upright box around a group's members, which is the rectangle the handles sit on and the one a drag
    /// takes from where it was to where it goes. Null when nothing is being transformed.
    /// </summary>
    public static LayerTransform? GroupBox(CanvasDocument document, IReadOnlyCollection<Guid> ids)
    {
        var members = GroupMembers(document, ids);
        if (members.Count == 0) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var member in members)
        {
            var box = Bounds(member.Transform);
            minX = Math.Min(minX, box.Left);
            minY = Math.Min(minY, box.Top);
            maxX = Math.Max(maxX, box.Right);
            maxY = Math.Max(maxY, box.Bottom);
        }
        return new LayerTransform(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    /// <summary>
    /// A layer carried along as the box it sits in moves from one place to another: a plain move shifts it,
    /// and a scale or a turn puts it through the same mapping, so a group keeps the shape it had. This is
    /// what makes several layers transform together rather than each about its own middle.
    /// </summary>
    public static LayerTransform Following(LayerTransform layer, LayerTransform from, LayerTransform to)
    {
        if (from == to) return layer;
        if (from.Width == to.Width && from.Height == to.Height && from.Rotation == to.Rotation
            && from.FlipX == to.FlipX && from.FlipY == to.FlipY)
        {
            // A plain move carries exactly, without going near the matrix.
            var moved = layer with { X = layer.X + (to.X - from.X), Y = layer.Y + (to.Y - from.Y) };
            return moved.IsValid ? moved : layer;
        }
        if (!Unit(from).TryInvert(out var inverse)) return layer;
        // The box's own mapping, applied to this layer: the layer into the box's units, through the box's
        // own move, and back out where the box is now.
        var placed = Placing(layer, Compose(Unit(to), Compose(inverse, Unit(layer))));
        return placed.IsValid ? placed : layer;
    }

    /// <summary>
    /// Carries a whole group along with its box, from what each layer was when the drag began. Nothing is
    /// moved when the resulting layer transforms already match. Returning to the starting box restores
    /// the original transforms after an earlier move.
    /// </summary>
    public static bool Carry(CanvasDocument document, IReadOnlyDictionary<Guid, LayerTransform> originals,
        LayerTransform from, LayerTransform to, IReadOnlyDictionary<Guid, LayerTransform?>? maskOriginals = null)
    {
        var changed = false;
        foreach (var (id, original) in originals)
        {
            if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) continue;
            var next = Following(original, from, to);
            if (layer.Transform == next) continue;
            if (layer.Mask is { } mask)
            {
                mask.Placement = maskOriginals is not null && maskOriginals.TryGetValue(id, out var maskOriginal)
                    ? mask.PlacementMoving(original, next, maskOriginal)
                    : mask.PlacementMoving(layer.Transform, next);
            }
            layer.Transform = next;
            changed = true;
        }
        return changed;
    }

    /// <summary>The unit square placed on the document as this transform places a layer.</summary>
    private static SKMatrix Unit(LayerTransform transform) => BrushEdits.PixelToDocument(transform, 1, 1);

    /// <summary>
    /// A transform placing the unit square the way a mapping does — a turned, maybe flipped rectangle. A
    /// shear, which only an uneven scale of something already turned makes, is dropped, as the Mac build
    /// drops it. The rotation nearest this one's is kept, so the numbers stay familiar.
    /// </summary>
    private static LayerTransform Placing(LayerTransform current, SKMatrix map)
    {
        var sign = current.FlipX ? -1.0 : 1.0;
        var angle = Math.Atan2(map.SkewY * sign, map.ScaleX * sign);
        var along = -map.SkewX * Math.Sin(angle) + map.ScaleY * Math.Cos(angle);
        var middle = map.MapPoint(0.5f, 0.5f);
        var width = Math.Sqrt((double)map.ScaleX * map.ScaleX + (double)map.SkewY * map.SkewY);
        var height = Math.Abs(along);
        var degrees = angle * 180 / Math.PI;
        var placed = current with
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Rotation = degrees + Math.Round((current.Rotation - degrees) / 360) * 360,
            FlipY = along < 0,
            X = middle.X - width / 2,
            Y = middle.Y - height / 2,
        };
        return placed;
    }

    /// <summary>
    /// Two mappings in one: <paramref name="before"/> is applied first, then <paramref name="after"/>. Named
    /// this way round because Skia's own concatenation reads backwards from what it looks like.
    /// </summary>
    private static SKMatrix Compose(SKMatrix after, SKMatrix before) => SKMatrix.Concat(after, before);

    /// <summary>Whether a layer is one of the named layers or sits inside one of them.</summary>
    private static bool Inside(CanvasDocument document, ImageLayer layer, HashSet<Guid> ids)
    {
        var current = (Guid?)layer.ID;
        for (var depth = 0; current is { } id && depth < 64; depth++)
        {
            if (ids.Contains(id)) return true;
            current = document.Layers.FirstOrDefault(candidate => candidate.ID == id)?.ParentID;
        }
        return false;
    }

    /// <summary>
    /// What a moving layer lines up with: the canvas edges and middle, the canvas guides, and the boxes of
    /// the other layers that hold pixels. Which of the three is asked for is the View menu's business, so
    /// each can be switched off on its own.
    /// </summary>
    public static (List<double> Xs, List<double> Ys) SnapTargets(CanvasDocument document,
        IReadOnlyCollection<Guid> moving, SnapTo snapTo = SnapTo.All, LayoutGrid? grid = null)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        if (snapTo.HasFlag(SnapTo.Grid) && grid is { } lines)
        {
            // The grid's own lines across the whole document, which is where the eye sees them.
            for (var at = 0.0; at <= document.Width; at += lines.Step) xs.Add(at);
            for (var at = 0.0; at <= document.Height; at += lines.Step) ys.Add(at);
        }
        if (snapTo.HasFlag(SnapTo.Canvas))
        {
            xs.AddRange([0, document.Width, document.Width / 2.0]);
            ys.AddRange([0, document.Height, document.Height / 2.0]);
        }
        if (snapTo.HasFlag(SnapTo.Guides))
        {
            foreach (var guide in document.Guides)
            {
                if (guide.Axis == GuideAxis.Vertical) xs.Add(guide.Position);
                else ys.Add(guide.Position);
            }
        }
        if (snapTo.HasFlag(SnapTo.Layers))
        {
            var visible = document.EffectiveVisibleIDs();
            foreach (var layer in document.Layers)
            {
                if (layer.IsGroup || layer.Asset is null || moving.Contains(layer.ID) || !visible.Contains(layer.ID)) continue;
                var box = Bounds(layer.Transform);
                xs.AddRange([Math.Round(box.Left), Math.Round(box.MidX), Math.Round(box.Right)]);
                ys.AddRange([Math.Round(box.Top), Math.Round(box.MidY), Math.Round(box.Bottom)]);
            }
        }
        return (xs, ys);
    }

    /// <summary>
    /// The box nudged so its nearest edge or middle meets a nearby target, each axis on its own and only
    /// within <paramref name="tolerance"/> document pixels. The two lines it landed on come back too, to
    /// draw along.
    /// </summary>
    public static LayerTransform Snap(CanvasDocument document, LayerTransform draft, IReadOnlyCollection<Guid> moving,
        double tolerance, out double? lineX, out double? lineY, SnapTo snapTo = SnapTo.All, LayoutGrid? grid = null)
    {
        var (xs, ys) = SnapTargets(document, moving, snapTo, grid);
        var snap = TransformSnap.Offset(Bounds(draft), xs, ys, tolerance);
        lineX = snap.LineX;
        lineY = snap.LineY;
        if (snap.X == 0 && snap.Y == 0) return draft;
        var snapped = draft with { X = draft.X + snap.X, Y = draft.Y + snap.Y };
        return snapped.IsValid ? snapped : draft;
    }

    private static bool Near(SKPoint point, SKPoint other, double tolerance)
    {
        var dx = point.X - other.X;
        var dy = point.Y - other.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }
}

/// <summary>
/// Moving a layer snaps its edges and middle to the canvas, the guides and the other layers. The pull is a
/// fixed distance on screen, so it feels the same at any zoom, and small enough to slide past without a
/// fight.
/// </summary>
public static class TransformSnap
{
    /// <summary>How close, in screen pixels, a line comes before it snaps.</summary>
    public const double Distance = 10;

    /// <summary>
    /// The move that puts whichever of the box's left, middle or right lands nearest one of
    /// <paramref name="xs"/> on it, and the same vertically — each axis on its own, and only within
    /// <paramref name="tolerance"/>. The lines it landed on come back too.
    /// </summary>
    public static (double X, double Y, double? LineX, double? LineY) Offset(SKRect box, IReadOnlyList<double> xs,
        IReadOnlyList<double> ys, double tolerance)
    {
        var horizontal = Shift([box.Left, box.MidX, box.Right], xs, tolerance);
        var vertical = Shift([box.Top, box.MidY, box.Bottom], ys, tolerance);
        return (horizontal.Move, vertical.Move, horizontal.Target, vertical.Target);
    }

    /// <summary>The smallest move that puts one of <paramref name="from"/> onto one of <paramref name="to"/>.</summary>
    private static (double Move, double? Target) Shift(IReadOnlyList<double> from, IReadOnlyList<double> to,
        double tolerance)
    {
        (double Move, double? Target)? best = null;
        foreach (var value in from)
        {
            foreach (var target in to)
            {
                var move = target - value;
                if (Math.Abs(move) > tolerance) continue;
                if (best is { } current && Math.Abs(current.Move) <= Math.Abs(move)) continue;
                best = (move, target);
            }
        }
        return best is { } found ? (found.Move, found.Target) : (0, null);
    }
}
