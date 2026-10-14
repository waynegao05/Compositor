using Compositor.Core.Format;
using Format = Compositor.Core.Format;
using Model = Compositor.Core.Model;

namespace Compositor.Core.Model;

/// <summary>
/// A document: pixel dimensions, the layer stack bottom to top, and the user-placed alignment guides.
/// Undo history and the viewport are session state and are not part of this.
/// </summary>
public sealed class CanvasDocument : IDisposable
{
    public CanvasDocument(Guid id, int width, int height, double resolution = 72)
    {
        ID = id;
        Width = width;
        Height = height;
        Resolution = resolution;
    }

    public Guid ID { get; init; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Resolution { get; set; } = 72;

    /// <summary>Bottom to top.</summary>
    public List<ImageLayer> Layers { get; } = [];

    /// <summary>Saved with the project; undo covers them.</summary>
    public List<CanvasGuide> Guides { get; } = [];

    /// <summary>What an edit may touch. Part of the document so undo covers it; not saved to disk.</summary>
    public DocumentSelection Selection { get; set; } = DocumentSelection.All;

    /// <summary>False on a history snapshot: the pixels belong to the document it was copied from.</summary>
    private bool _ownsPixels = true;

    public double PixelWidth => Width;
    public double PixelHeight => Height;

    /// <summary>
    /// A copy holding the same layers and guides, sharing the same pixels. It does not own them, so disposing
    /// it leaves the pixels alone: they belong to the document it was copied from.
    /// </summary>
    public CanvasDocument Clone()
    {
        var copy = new CanvasDocument(ID, Width, Height, Resolution) { _ownsPixels = false };
        copy.Layers.AddRange(Layers.Select(layer => layer.Clone()));
        copy.Selection = Selection;
        // Guides are objects, so they are copied rather than shared: a snapshot has to keep the positions it
        // was taken at, or moving a guide could not be undone.
        copy.Guides.AddRange(Guides.Select(guide => new CanvasGuide
        {
            ID = guide.ID,
            Axis = guide.Axis,
            Position = guide.Position,
        }));
        return copy;
    }

    /// <summary>
    /// A document holding another's pixels — a subset of a stack built to be rendered. It does not own
    /// them, so it must never be disposed: the layers it holds are given to it, and they still belong to
    /// the document they came from.
    /// </summary>
    internal static CanvasDocument Borrowing(Guid id, int width, int height, double resolution) =>
        new(id, width, height, resolution) { _ownsPixels = false };

    /// <summary>
    /// Replaces this document's contents with another's. The layers are copied, so this document stays the
    /// one that owns the pixels, which is what an undo needs: it puts a snapshot's contents back without
    /// handing ownership of the images to a copy.
    /// </summary>
    public void Adopt(CanvasDocument other)
    {
        Width = other.Width;
        Height = other.Height;
        Resolution = other.Resolution;
        Layers.Clear();
        Layers.AddRange(other.Layers.Select(layer => layer.Clone()));
        Guides.Clear();
        // A restored document must not mutate the snapshot kept for later undo or redo.
        Guides.AddRange(other.Guides.Select(guide => new CanvasGuide
        {
            ID = guide.ID,
            Axis = guide.Axis,
            Position = guide.Position,
        }));
        Selection = other.Selection;
    }

    /// <summary>Whether two documents hold the same thing, with pixels compared by identity.</summary>
    public bool SameAs(CanvasDocument other)
    {
        if (ID != other.ID || Width != other.Width || Height != other.Height || Resolution != other.Resolution
            || Layers.Count != other.Layers.Count || Guides.Count != other.Guides.Count
            || !Selection.Matches(other.Selection)) return false;
        for (var index = 0; index < Layers.Count; index++)
        {
            if (!Layers[index].SameAs(other.Layers[index])) return false;
        }
        for (var index = 0; index < Guides.Count; index++)
        {
            if (Guides[index].ID != other.Guides[index].ID || Guides[index].Axis != other.Guides[index].Axis
                || Guides[index].Position != other.Guides[index].Position) return false;
        }
        return true;
    }

    /// <summary>Every layer inside the folder, at any depth.</summary>
    public HashSet<Guid> Descendants(Guid folderID)
    {
        var found = new HashSet<Guid>();
        var pending = new Stack<Guid>([folderID]);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            foreach (var layer in Layers)
            {
                if (layer.ParentID == parent && found.Add(layer.ID)) pending.Push(layer.ID);
            }
        }
        return found;
    }

    /// <summary>The opacity each layer is drawn at, folders included.</summary>
    public Dictionary<Guid, double> EffectiveOpacities()    {
        var byID = Layers.ToDictionary(layer => layer.ID);
        return Layers.ToDictionary(layer => layer.ID, layer => layer.EffectiveOpacity(byID));
    }

    /// <summary>Bottom to top, folders included, with each layer's inherited visibility.</summary>
    public List<LayerHierarchy.Entry> HierarchyEntries(HashSet<Guid>? collapsed = null, bool topFirst = false) =>
        LayerHierarchy.Entries(Layers.Select(Record).ToList(), topFirst, collapsed ?? []);

    public List<ImageLayer> RenderLayers()
    {
        var byID = Layers.ToDictionary(layer => layer.ID);
        return LayerHierarchy.Entries(Layers.Select(Record).ToList())
            .Where(entry => entry.Visible && entry.Layer.IsGroup != true)
            .Select(entry => byID[entry.Layer.ID])
            .ToList();
    }

    public HashSet<Guid> EffectiveVisibleIDs() =>
        HierarchyEntries().Where(entry => entry.Visible).Select(entry => entry.Layer.ID).ToHashSet();

    /// <summary>The manifest record for one layer, without the pixels.</summary>
    public static ProjectLayerRecord Record(ImageLayer layer) => new()
    {
        ID = layer.ID,
        Name = layer.Name,
        IsVisible = layer.IsVisible,
        Transform = Format.LayerTransform.FromRuntime(layer.Transform),
        ImageFile = layer.Asset is null ? null : Format.LayerMask.ExpectedImageFile(layer.ID),
        ParentID = layer.ParentID,
        IsGroup = layer.IsGroup,
        Opacity = layer.Opacity,
        BlendMode = layer.BlendMode,
        MaskFile = layer.Mask is null ? null : Format.LayerMask.ExpectedFile(layer.ID),
        MaskEnabled = layer.Mask?.IsEnabled,
        MaskSourceID = layer.MaskSourceID,
        Adjustment = layer.Adjustment,
        MaskPlacement = layer.Mask?.Placement is { } placement ? Format.LayerTransform.FromRuntime(placement) : null,
        MaskLinked = layer.Mask?.IsLinked,
        Shape = layer.LiveShape,
        Effects = layer.Effects,
        Text = layer.LiveText,
    };

    public void Dispose()
    {
        if (_ownsPixels)
        {
            foreach (var layer in Layers) layer.Dispose();
        }
        Layers.Clear();
    }
}
