using System.Text.Json;
using Compositor.Core.Document;

namespace Compositor.Core.IO;

/// <summary>
/// The view's switches, which belong to the person rather than to a document: whether the layout grid is
/// shown, how its squares are spaced, and what a transform drag snaps to. They keep whatever they were last
/// set to, from one launch to the next, which is also why they are not saved with the project — as the Mac
/// build keeps them out of the project too.
/// </summary>
public sealed class ToolDefaults
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Where the switches are kept for whoever is using the app.</summary>
    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "tools.json");

    public bool ShowGrid { get; set; }

    /// <summary>Whether the ruler strips are down the side and along the top of the canvas.</summary>
    public bool ShowRulers { get; set; }

    /// <summary>Whether the guides are drawn at all.</summary>
    public bool ShowGuides { get; set; }

    /// <summary>Whether the guides may be dragged, as the View menu's Lock Guides switch has it.</summary>
    public bool LockGuides { get; set; }

    /// <summary>Whether the Move tool draws its transform handles.</summary>
    public bool ShowTransformControls { get; set; } = true;

    /// <summary>Whether a line is drawn around each document pixel when the view is in far enough.</summary>
    public bool PixelGrid { get; set; }

    /// <summary>Whether a drag lines up with anything at all, whatever the per-kind switches say.</summary>
    public bool Snapping { get; set; } = true;

    public int GridSpacing { get; set; } = 64;

    public int GridSubdivisions { get; set; } = 8;

    /// <summary>What a drag snaps to, as the Snap To switches have it.</summary>
    public SnapTo SnapTo { get; set; } = SnapTo.All;

    /// <summary>The switches as they were last left, or the defaults when there are none to read.</summary>
    public static ToolDefaults Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ToolDefaults();
            var read = JsonSerializer.Deserialize<ToolDefaults>(File.ReadAllText(path), Json);
            return read?.Kept() ?? new ToolDefaults();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // A kept switch is a convenience: one that cannot be read is not worth failing to start over.
            return new ToolDefaults();
        }
    }

    /// <summary>Writes the switches out, quietly doing nothing when they cannot be written.</summary>
    public void Save(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder is not null) Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The grid these switches ask for, with its spacing brought back inside the range a grid allows.</summary>
    public LayoutGrid Grid() => new(GridSpacing, GridSubdivisions);

    /// <summary>
    /// The same switches with anything a hand-edited file could have got wrong put right: a grid's spacing
    /// and subdivisions are clamped by <see cref="LayoutGrid"/> when they are used, and a snap flag that is
    /// not one of the four is dropped rather than followed.
    /// </summary>
    private ToolDefaults Kept() => new()
    {
        ShowGrid = ShowGrid,
        ShowRulers = ShowRulers,
        ShowGuides = ShowGuides,
        LockGuides = LockGuides,
        ShowTransformControls = ShowTransformControls,
        PixelGrid = PixelGrid,
        Snapping = Snapping,
        GridSpacing = Grid().Spacing,
        GridSubdivisions = Grid().Subdivisions,
        SnapTo = SnapTo & SnapTo.All,
    };
}
