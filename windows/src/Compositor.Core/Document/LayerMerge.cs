using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// The Layer panel's merge (⌘E): the layers a plan names, composited the way the canvas shows them — blend
/// modes, opacity, masks, clipping and adjustments baked in — into one pixel layer, trimmed to what is
/// there and left in their place. It changes the document in place; the caller brackets it with the history
/// so the whole thing becomes one undo step.
/// </summary>
public static class LayerMerge
{
    /// <summary>
    /// What a merge takes in and where its result goes. Every member is merged away, and the result takes
    /// the anchor's slot, carrying the anchor's name and folder.
    /// </summary>
    public sealed record MergePlan(
        /// <summary>Bottom to top, so the result composites the way the members did.</summary>
        IReadOnlyList<Guid> Members,
        /// <summary>What the result is called: the name of the topmost layer the merge takes in.</summary>
        string Name,
        /// <summary>The folder the result belongs to; null at the top level.</summary>
        Guid? Parent,
        /// <summary>The layer whose slot the result takes.</summary>
        Guid Anchor,
        /// <summary>"Merge Down", "Merge Layers" or "Merge Group", as the Mac build names them.</summary>
        string Action);

    /// <summary>
    /// What merging would do, in stacking order, and where the result goes; null when there is nothing to
    /// merge. One layer merges with the layer beneath it in the same folder; several selected layers merge
    /// together, with anything their folders hold; a folder merges its contents, and the folder goes.
    /// </summary>
    public static MergePlan? Plan(CanvasDocument document, IReadOnlyCollection<Guid> selected, Guid? activeID)
    {
        if (activeID is not { } active) return null;
        var layers = document.Layers;
        if (layers.FirstOrDefault(layer => layer.ID == active) is not { } activeLayer) return null;
        var picked = selected.ToHashSet();

        if (picked.Count > 1)
        {
            foreach (var id in selected) picked.UnionWith(document.Descendants(id));
            var members = layers.Where(layer => picked.Contains(layer.ID)).ToList();
            // A folder holds no pixels of its own, so a merge of folders alone has nothing to bake.
            if (!members.Any(layer => !layer.IsGroup)) return null;
            // The result is named and placed by the topmost layer among the ones actually selected.
            var top = members.LastOrDefault(layer => selected.Contains(layer.ID));
            if (top is null) return null;
            return new MergePlan([.. members.Select(layer => layer.ID)], top.Name, top.ParentID, top.ID, "Merge Layers");
        }

        if (activeLayer.IsGroup)
        {
            var inside = document.Descendants(active);
            if (!layers.Any(layer => inside.Contains(layer.ID) && !layer.IsGroup)) return null;
            var members = layers.Where(layer => inside.Contains(layer.ID) || layer.ID == active)
                .Select(layer => layer.ID).ToList();
            return new MergePlan(members, activeLayer.Name, activeLayer.ParentID, active, "Merge Group");
        }

        var index = layers.FindIndex(layer => layer.ID == active);
        if (index < 0) return null;
        // The nearest layer below it in the same folder is the one it merges into.
        ImageLayer? below = null;
        for (var candidate = index - 1; candidate >= 0 && below is null; candidate--)
        {
            if (layers[candidate].ParentID == activeLayer.ParentID) below = layers[candidate];
        }
        if (below is null || below.IsGroup) return null;
        return new MergePlan([below.ID, active], below.Name, activeLayer.ParentID, active, "Merge Down");
    }

    /// <summary>
    /// Merges what <see cref="Plan"/> names, in place, and returns the layer it made — null when there is
    /// nothing to merge, when the canvas is too big to composite in one surface, or when the result would
    /// not load back.
    /// </summary>
    public static Guid? Merge(CanvasDocument document, IReadOnlyCollection<Guid> selected, Guid? activeID)
    {
        if (Plan(document, selected, activeID) is not { } plan) return null;
        if ((long)document.Width * document.Height > DocumentLimits.MaxSurfacePixels) return null;
        var kept = plan.Members.ToHashSet();
        var layers = document.Layers;
        var prepared = layers.Select(layer => layer.Clone()).ToList();
        var normalized = PushAncestorOpacity(prepared, kept);
        var preparedByID = prepared.ToDictionary(layer => layer.ID);

        // The members on their own: folders they belong to but that are not part of the merge are left out,
        // and a clip whose base is outside the merge stops clipping rather than clipping to nothing.
        var subset = new List<ImageLayer>();
        foreach (var layer in prepared)
        {
            if (!kept.Contains(layer.ID)) continue;
            var copy = layer.Clone();
            if (copy.ParentID is { } parent && !kept.Contains(parent)) copy.ParentID = null;
            if (copy.MaskSourceID is { } source && !kept.Contains(source)) copy.MaskSourceID = null;
            subset.Add(copy);
        }

        // Rendering reads the members' own pixels, so this copy of the document must never dispose them.
        var flat = CanvasDocument.Borrowing(document.ID, document.Width, document.Height, document.Resolution);
        flat.Layers.AddRange(subset);
        using var full = DocumentRenderer.Render(flat);
        var (trimmed, transform) = Trimmed(full, CanvasPlaced(document));
        var merged = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(trimmed, plan.Name), transform, plan.Name)
        {
            ParentID = plan.Parent,
        };

        var next = layers.Where(layer => !kept.Contains(layer.ID)).ToList();
        var insertion = Insertion(layers, kept, plan.Anchor, next.Count);
        var placed = new List<ImageLayer>(next);
        placed.Insert(insertion, merged);

        // Layers clipped to something that was merged now clip to the result. The re-pointing is worked out
        // before anything is changed, so a document that would not load back is refused rather than
        // half-merged.
        try
        {
            LayerHierarchy.Validate(placed.Select(RecordOf).ToList());
        }
        catch (ProjectException)
        {
            merged.Dispose();
            return null;
        }
        if (normalized)
        {
            // A parent mask or a blend with unmerged content can make even the normalized result
            // impossible to represent as one layer. Refuse it without changing the live records.
            using var candidate = CanvasDocument.Borrowing(document.ID, document.Width, document.Height, document.Resolution);
            foreach (var layer in placed)
            {
                var copy = layer.ID == merged.ID ? merged.Clone() : preparedByID[layer.ID].Clone();
                if (copy.MaskSourceID is { } source && kept.Contains(source)) copy.MaskSourceID = merged.ID;
                candidate.Layers.Add(copy);
            }
            if (!SameAppearance(document, candidate))
            {
                merged.Dispose();
                return null;
            }
        }
        foreach (var layer in next)
        {
            layer.Opacity = preparedByID[layer.ID].Opacity;
            if (layer.MaskSourceID is { } source && kept.Contains(source)) layer.MaskSourceID = merged.ID;
        }
        document.Layers.Clear();
        document.Layers.AddRange(placed);
        return merged.ID;

        ProjectLayerRecord RecordOf(ImageLayer layer)
        {
            var record = CanvasDocument.Record(layer);
            if (record.MaskSourceID is { } source && kept.Contains(source)) record.MaskSourceID = merged.ID;
            return record;
        }
    }

    /// <summary>
    /// A pass-through folder applies opacity to each descendant, which cannot in general be represented
    /// by the same opacity on one merged raster. Move only the affected ancestors' factors down one level
    /// at a time, preserving every descendant's effective opacity, including hidden siblings.
    /// </summary>
    private static bool PushAncestorOpacity(List<ImageLayer> layers, HashSet<Guid> merged)
    {
        var byID = layers.ToDictionary(layer => layer.ID);
        var ancestors = new HashSet<Guid>();
        foreach (var member in layers.Where(layer => merged.Contains(layer.ID)))
        {
            var parent = member.ParentID;
            for (var depth = 0; parent is { } id && depth < 64 && byID.TryGetValue(id, out var folder); depth++)
            {
                if (!merged.Contains(id)) ancestors.Add(id);
                parent = folder.ParentID;
            }
        }
        var changed = false;
        // The hierarchy visits parents first, so a nested folder receives its parent's factor before
        // passing its own combined factor on to its children.
        foreach (var entry in LayerHierarchy.Entries(layers.Select(CanvasDocument.Record).ToList()))
        {
            if (!ancestors.Contains(entry.Layer.ID)) continue;
            var folder = byID[entry.Layer.ID];
            if (folder.Opacity == 1) continue;
            foreach (var child in layers.Where(layer => layer.ParentID == folder.ID)) child.Opacity *= folder.Opacity;
            folder.Opacity = 1;
            changed = true;
        }
        return changed;
    }

    private static bool SameAppearance(CanvasDocument before, CanvasDocument after)
    {
        // Bound the extra verification buffers even when the project is near the surface size limit.
        const int side = 256;
        for (var y = 0; y < before.Height; y += side)
        {
            for (var x = 0; x < before.Width; x += side)
            {
                var region = SKRectI.Create(x, y, Math.Min(side, before.Width - x), Math.Min(side, before.Height - y));
                using var original = DocumentRenderer.RenderRegion(before, region);
                using var merged = DocumentRenderer.RenderRegion(after, region);
                var left = original.GetPixelSpan();
                var right = merged.GetPixelSpan();
                for (var i = 0; i < left.Length; i++)
                {
                    if (Math.Abs(left[i] - right[i]) > 1) return false;
                }
            }
        }
        return true;
    }

    /// <summary>Where in the shortened array the anchor's slot lands once the members before it are gone.</summary>
    private static int Insertion(List<ImageLayer> layers, HashSet<Guid> kept, Guid anchor, int limit)
    {
        var slot = layers.FindIndex(layer => layer.ID == anchor);
        if (slot < 0) return limit;
        var dropped = 0;
        for (var index = 0; index < slot; index++)
        {
            if (kept.Contains(layers[index].ID)) dropped++;
        }
        return Math.Clamp(slot - dropped, 0, limit);
    }

    /// <summary>The whole canvas as a layer transform: a render has no rotation, flip or placement of its own.</summary>
    private static Model.LayerTransform CanvasPlaced(CanvasDocument document) =>
        new(0, 0, document.Width, document.Height);

    /// <summary>
    /// A rendered image cut back to the pixels that are actually there, with the transform that keeps them
    /// in place — a whole-canvas render cropped, so its transform is the crop itself. A crop with nothing in
    /// it, or one that would take nothing away, is left alone, as the Mac build leaves it.
    /// <para>
    /// Shared with the filters that spread: a blur is given room past the layer's edge and whatever stays
    /// empty once it has run is cut away again.
    /// </para>
    /// </summary>
    internal static (SKBitmap Image, Model.LayerTransform Transform) Trimmed(SKBitmap image,
        Model.LayerTransform placed)
    {
        Span<int> bounds = stackalloc int[4];
        BrushPixels.AlphaBounds(image.GetPixelSpan(), image.Width, image.Height, image.RowBytes, bounds);
        var crop = SKRectI.Create(bounds[0], bounds[1], bounds[2] - bounds[0], bounds[3] - bounds[1]);
        if (crop.Width < 1 || crop.Height < 1 || crop.Width == image.Width && crop.Height == image.Height)
        {
            return (Cropped(image, SKRectI.Create(0, 0, image.Width, image.Height)), placed);
        }

        // The crop's middle stays where it was, so the picture does not move as it shrinks.
        var width = crop.Width * placed.Width / image.Width;
        var height = crop.Height * placed.Height / image.Height;
        var toDocument = BrushEdits.PixelToDocument(placed, image.Width, image.Height);
        var middle = toDocument.MapPoint(crop.Left + crop.Width / 2f, crop.Top + crop.Height / 2f);
        var transform = placed with
        {
            X = middle.X - width / 2,
            Y = middle.Y - height / 2,
            Width = width,
            Height = height,
        };
        return (Cropped(image, crop), transform);
    }

    /// <summary>
    /// One rectangle of a render as a layer's own pixels: an exact copy at the same scale, in the
    /// straight-alpha format every layer asset is held in.
    /// </summary>
    private static SKBitmap Cropped(SKBitmap source, SKRectI rect)
    {
        var cropped = new SKBitmap(Bitmaps.ColorInfo(rect.Width, rect.Height));
        if (!cropped.ReadyToDraw) throw new InvalidOperationException($"Could not allocate a {rect.Width}x{rect.Height} bitmap.");
        using (var canvas = new SKCanvas(cropped))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(source,
                SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height),
                SKRect.Create(0, 0, rect.Width, rect.Height),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), paint);
        }
        return cropped;
    }
}
