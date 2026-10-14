using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>
/// Undo and redo as value snapshots. A document is copied when an edit begins and again when it ends, and
/// the copies share the same pixels, so a step costs a list of layer records rather than a set of images.
/// </summary>
public sealed class DocumentHistory
{
    public readonly record struct Snapshot(CanvasDocument? Document, Guid? ActiveLayerID, Guid Revision);

    private sealed record Entry(string Name, Snapshot Before, Snapshot After);

    private readonly List<Entry> _past = [];
    private readonly List<Entry> _future = [];
    private Guid _revision = Guid.NewGuid();
    private Guid? _savedRevision;
    private Snapshot? _pending;
    private string _pendingName = "Edit";
    private int _depth;

    public DocumentHistory(int entryLimit = 100, int retainedByteLimit = 256 * 1024 * 1024)
    {
        EntryLimit = Math.Max(0, entryLimit);
        RetainedByteLimit = Math.Max(0, retainedByteLimit);
        _savedRevision = _revision;
    }

    public int EntryLimit { get; }
    public int RetainedByteLimit { get; }

    public bool CanUndo => _depth == 0 && _past.Count > 0;
    public bool CanRedo => _depth == 0 && _future.Count > 0;
    public string UndoName => _past.Count > 0 ? _past[^1].Name : "";
    public string RedoName => _future.Count > 0 ? _future[^1].Name : "";
    public bool IsModified => _revision != _savedRevision;
    public bool HasPendingEdit => _depth > 0;

    /// <summary>Changes whenever a new edit or history navigation invalidates an asynchronous result.</summary>
    public long EditGeneration { get; private set; }
    public int UndoCount => _past.Count;

    /// <summary>The document as it stands, for a save that captures it now and finishes later.</summary>
    public Guid CurrentRevision => _revision;

    public void MarkSaved() => _savedRevision = _revision;

    /// <summary>
    /// A save of <paramref name="saved"/> finished. Edits made while it was writing leave the document
    /// modified; undoing back to it does not.
    /// </summary>
    public void MarkSaved(Guid saved) => _savedRevision = saved;

    public void Reset()
    {
        EditGeneration++;
        _past.Clear();
        _future.Clear();
        _pending = null;
        _depth = 0;
        _revision = Guid.NewGuid();
        _savedRevision = _revision;
    }

    public void Begin(string name, CanvasDocument? document, Guid? activeLayer)
    {
        if (_depth == 0)
        {
            EditGeneration++;
            _pending = new Snapshot(document?.Clone(), activeLayer, _revision);
            _pendingName = name;
        }
        _depth++;
    }

    public void End(CanvasDocument? document, Guid? activeLayer)
    {
        if (_depth == 0) return;
        _depth--;
        if (_depth > 0 || _pending is not { } before) return;
        _pending = null;
        // Selecting, navigating and edits that changed nothing must preserve the redo history.
        if (Same(before.Document, document)) return;
        _revision = Guid.NewGuid();
        _past.Add(new Entry(_pendingName, before, new Snapshot(document?.Clone(), activeLayer, _revision)));
        _future.Clear();
        Trim(document);
    }

    public Snapshot? Undo()
    {
        if (!CanUndo) return null;
        EditGeneration++;
        var entry = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        _future.Add(entry);
        _revision = entry.Before.Revision;
        Trim(entry.Before.Document);
        return entry.Before;
    }

    public Snapshot? Redo()
    {
        if (!CanRedo) return null;
        EditGeneration++;
        var entry = _future[^1];
        _future.RemoveAt(_future.Count - 1);
        _past.Add(entry);
        _revision = entry.After.Revision;
        Trim(entry.After.Document);
        return entry.After;
    }

    /// <summary>Bytes only the history holds: the images no live layer names.</summary>
    public long RetainedBytes(CanvasDocument? current)
    {
        var live = new HashSet<SKBitmap>();
        foreach (var (image, thumbnail) in Assets(current)) { live.Add(image); live.Add(thumbnail); }
        var seen = new HashSet<SKBitmap>();
        long bytes = 0;
        foreach (var entry in _past.Concat(_future))
        {
            foreach (var snapshot in new[] { entry.Before, entry.After })
            {
                foreach (var (image, thumbnail) in Assets(snapshot.Document))
                {
                    foreach (var bitmap in new[] { image, thumbnail })
                    {
                        if (live.Contains(bitmap) || !seen.Add(bitmap)) continue;
                        bytes += (long)bitmap.RowBytes * bitmap.Height;
                    }
                }
            }
        }
        return bytes;
    }

    private static IEnumerable<(SKBitmap Image, SKBitmap Thumbnail)> Assets(CanvasDocument? document)
    {
        foreach (var layer in document?.Layers ?? [])
        {
            if (layer.Asset is { } asset) yield return (asset.Image, asset.Thumbnail);
            if (layer.Mask?.Asset is { } mask) yield return (mask.Image, mask.Thumbnail);
        }
    }

    private void Trim(CanvasDocument? current)
    {
        while (_past.Count + _future.Count > EntryLimit || RetainedBytes(current) > RetainedByteLimit)
        {
            if (_past.Count > 0) _past.RemoveAt(0);
            else if (_future.Count > 0) _future.RemoveAt(0);
            else break;
        }
    }

    private static bool Same(CanvasDocument? left, CanvasDocument? right) => left is null
        ? right is null
        : right is not null && left.SameAs(right);
}
