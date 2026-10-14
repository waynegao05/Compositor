using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core.IO;

/// <summary>
/// What a shortcut is held down with. The bits are the port's own, and they sit where the Mac build's do: the
/// Mac's Command is this port's Ctrl and its Option is this port's Alt, so a chord ports across untouched.
/// </summary>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// One key combination: a key named after Avalonia's own <c>Avalonia.Input.Key</c> member, and the modifiers
/// held with it. The key is a string because this assembly does not depend on Avalonia — the window is what
/// turns the name into a key, and the window's <c>--shortcuts</c> check is what proves every name is one it
/// knows, so a table that names a key the build has never heard of is reported rather than obeyed.
/// </summary>
public readonly record struct ShortcutChord(string Key, ShortcutModifiers Modifiers = ShortcutModifiers.None)
{
    /// <summary>A row with a place on the list and no key on it, which the editor may leave alone or fill.</summary>
    public static ShortcutChord Unbound { get; } = new("");

    /// <summary>Whether there is a key on this chord at all.</summary>
    [JsonIgnore]
    public bool IsBound => Key.Length > 0;

    /// <summary>How the chord reads to a person: "Ctrl+Shift+Z", "Alt+Delete", "[", "Space".</summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            if (!IsBound) return "";
            var text = "";
            if (Modifiers.HasFlag(ShortcutModifiers.Control)) text += "Ctrl+";
            if (Modifiers.HasFlag(ShortcutModifiers.Alt)) text += "Alt+";
            if (Modifiers.HasFlag(ShortcutModifiers.Shift)) text += "Shift+";
            return text + Spell(Key);
        }
    }

    public override string ToString() => Label;

    /// <summary>The name of the key as a person reads it, which is not always the enum member's own name.</summary>
    private static string Spell(string key) => key switch
    {
        "OemPlus" => "=",
        "OemMinus" => "-",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemComma" => ",",
        "OemPeriod" => ".",
        _ when key.Length == 2 && key[0] == 'D' && char.IsAsciiDigit(key[1]) => key[1..],
        _ => key,
    };
}

/// <summary>
/// One row of the shortcut list: what it is called, which group it is in, and the key it starts on. The id is
/// the Mac build's own "group:title", so a table written by either build names the same rows.
/// </summary>
public sealed record ShortcutDefinition(string ID, string Title, string Group, ShortcutChord Original)
{
    /// <summary>Whether this row is one of the menus', which is what its place in the title bar is drawn from.</summary>
    [JsonIgnore]
    public bool IsMenu => Group == Shortcuts.Menus;
}

/// <summary>
/// Every key the app answers to, as one list. This is the Mac build's KeyboardShortcuts.swift, in this port's
/// terms: the same rows and the same grouping, with the Mac's chords kept — Command read as Ctrl — except
/// where Windows has its own claim on a combination, and with rows for the verbs this port has that the Mac
/// reaches another way.
/// </summary>
public static class Shortcuts
{
    /// <summary>The group of the rows that are drawn in the menus, and shown there with their key.</summary>
    public const string Menus = "Menus";

    /// <summary>The group of the rows the canvas and the layers answer to, which are not in any menu.</summary>
    public const string Canvas = "Canvas & Layers";

    /// <summary>The groups in the order the list shows them, which is the Mac build's order.</summary>
    public static IReadOnlyList<string> Groups { get; } = [Menus, Canvas];

    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } = Build();

    /// <summary>
    /// The table with the defaults filled in, so every row has the key it would fire on: what the window
    /// delivers from, and what the list is drawn from.
    /// </summary>
    public static Dictionary<string, ShortcutChord> Effective(IReadOnlyDictionary<string, ShortcutChord> overrides)
    {
        var table = new Dictionary<string, ShortcutChord>();
        foreach (var definition in Definitions)
        {
            table[definition.ID] = overrides.TryGetValue(definition.ID, out var chosen)
                ? chosen
                : definition.Original;
        }
        return table;
    }

    /// <summary>
    /// What is wrong with a table, or null when nothing is — the Mac build's own check (its <c>problem(in:)</c>)
    /// in this port's terms. A row may be left with no key on it, which is how a row the Mac gives no key to is
    /// written down; a key may not be on two rows, and may not be one Windows answers itself.
    /// </summary>
    public static string? Problem(IReadOnlyDictionary<string, ShortcutChord> overrides)
    {
        var assigned = new Dictionary<ShortcutChord, string>();
        foreach (var definition in Definitions)
        {
            var chord = overrides.TryGetValue(definition.ID, out var chosen) ? chosen : definition.Original;
            if (!chord.IsBound) continue;
            if (Mislaid(chord)) return $"{chord.Label} is not one key with modifiers Ctrl, Alt or Shift";
            if (Reserved(chord)) return $"{chord.Label} is reserved by Windows";
            if (assigned.TryGetValue(chord, out var other))
            {
                return $"{chord.Label} is on both {other} and {definition.Title}";
            }
            assigned[chord] = definition.Title;
        }
        return null;
    }

    private const ShortcutModifiers Held =
        ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift;

    /// <summary>Whether a chord is shaped like something a keyboard can produce at all.</summary>
    private static bool Mislaid(ShortcutChord chord) =>
        chord.Key.Length > 32 || chord.Key.Contains('+') || (chord.Modifiers & ~Held) != 0;

    /// <summary>
    /// Whether Windows has its own claim on the combination: the two window-switching keys, the one that opens
    /// the Start menu, the one every debugger takes, and — because every menu here is reached by holding Alt
    /// and pressing its own letter — Alt with a letter and no Ctrl.
    /// </summary>
    private static bool Reserved(ShortcutChord chord) => chord switch
    {
        { Key: "F4", Modifiers: ShortcutModifiers.Alt } => true,
        { Key: "Tab", Modifiers: ShortcutModifiers.Alt } => true,
        { Key: "Escape", Modifiers: ShortcutModifiers.Control } => true,
        { Key: "F12", Modifiers: ShortcutModifiers.None } => true,
        { Key: var key, Modifiers: var held }
            when held.HasFlag(ShortcutModifiers.Alt) && !held.HasFlag(ShortcutModifiers.Control)
                && key.Length == 1 && char.IsAsciiLetter(key[0]) => true,
        _ => false,
    };

    /// <summary>
    /// The list itself. The menus' rows read as the port's own menu rows read; a row the port has that the Mac
    /// reaches another way — the wand's, the ellipse's, a second duplicate — is written with a key of its own
    /// rather than left out, and a row with no sensible key left unbound.
    /// </summary>
    private static IReadOnlyList<ShortcutDefinition> Build()
    {
        const ShortcutModifiers Ctrl = ShortcutModifiers.Control;
        const ShortcutModifiers Alt = ShortcutModifiers.Alt;
        const ShortcutModifiers Shift = ShortcutModifiers.Shift;

        var rows = new List<ShortcutDefinition>();
        // Qualified, because the local function's own name would otherwise stand in for the group's.
        void Menu(string title, string key = "", ShortcutModifiers modifiers = ShortcutModifiers.None) =>
            rows.Add(new ShortcutDefinition($"{Shortcuts.Menus}:{title}", title, Shortcuts.Menus,
                new ShortcutChord(key, modifiers)));
        void Canvas(string title, string key = "", ShortcutModifiers modifiers = ShortcutModifiers.None) =>
            rows.Add(new ShortcutDefinition($"{Shortcuts.Canvas}:{title}", title, Shortcuts.Canvas,
                new ShortcutChord(key, modifiers)));

        // The File menu, then Edit, then Layer, then the view's own switches — the Mac's order.
        Menu("Undo", "Z", Ctrl);
        Menu("Redo", "Z", Ctrl | Shift);
        Menu("New Project", "N", Ctrl);
        Menu("Open Project", "O", Ctrl);
        Menu("Save", "S", Ctrl);
        Menu("Save As", "S", Ctrl | Shift);
        Menu("Export PNG", "E", Ctrl | Shift);
        Menu("Export JPEG", "S", Ctrl | Alt | Shift);
        Menu("Close Tab", "W", Ctrl);
        Menu("Fit Canvas", "D0", Ctrl);
        Menu("Actual Pixels", "D1", Ctrl);
        Menu("Zoom In", "OemPlus", Ctrl);
        Menu("Zoom Out", "OemMinus", Ctrl);
        Menu("Show Transform Controls", "H", Ctrl);
        Menu("Cut", "X", Ctrl);
        Menu("Copy", "C", Ctrl);
        Menu("Copy Merged", "C", Ctrl | Shift);
        Menu("Paste", "V", Ctrl);
        Menu("Fill with Foreground Color", "Delete", Alt);
        Menu("Fill with Background Color", "Delete", Ctrl);
        Menu("Content-Aware Fill", "Delete", Alt | Shift);
        Menu("Select All", "A", Ctrl);
        Menu("Deselect", "D", Ctrl);
        Menu("Inverse Selection", "I", Ctrl | Shift);
        Menu("Curves", "M", Ctrl);
        Menu("Levels", "L", Ctrl);
        Menu("Hue/Saturation", "U", Ctrl);
        Menu("Invert", "I", Ctrl);
        Menu("Canvas Size", "C", Ctrl | Alt);
        Menu("Image Size", "I", Ctrl | Alt);
        Menu("Layer via Copy", "J", Ctrl);
        // The port has the Mac's one duplicate row twice over: Layer via Copy takes the copy that is bounded by
        // the selection, and the layer's own duplicate takes Shift with it rather than the same key twice.
        Menu("Duplicate Layer", "J", Ctrl | Shift);
        Menu("Toggle Clipping Mask", "G", Ctrl | Alt);
        Menu("Group Layers", "G", Ctrl);
        Menu("New Blank Layer", "N", Ctrl | Shift);
        Menu("Move Layer Up", "OemCloseBrackets", Ctrl);
        Menu("Move Layer Down", "OemOpenBrackets", Ctrl);
        Menu("Merge Layers", "E", Ctrl);
        Menu("Rename Layer", "F2");
        Menu("Delete Layer", "Delete");
        Menu("Show Grid", "OemQuotes", Ctrl);
        Menu("Show Guides", "OemSemicolon", Ctrl);
        Menu("Show Rulers", "R", Ctrl);
        Menu("Snap", "OemSemicolon", Ctrl | Shift);
        Menu("Lock Guides", "OemSemicolon", Ctrl | Alt);
        // The port adds a guide from the View menu; the Mac gives that row no key at all, and so does this.
        Menu("New Guide");

        // The canvas's own: the tools, the colours, the brush, and what the arrows do.
        Canvas("Hand tool", "H");
        Canvas("Move / Transform tool", "V");
        Canvas("Marquee tool", "M");
        Canvas("Lasso tool", "L");
        Canvas("Magic wand", "W");
        Canvas("Brush tool", "B");
        Canvas("Clone Stamp", "S");
        Canvas("Blur / Smudge / Liquify", "R");
        Canvas("Spot Healing", "J");
        Canvas("Eyedropper tool", "I");
        Canvas("Type tool", "T");
        Canvas("Crop tool", "C");
        Canvas("Shape tool", "U");
        Canvas("Gradient tool", "G");
        Canvas("Swap foreground/background", "X");
        Canvas("Reset colors", "D");
        Canvas("Temporary Hand tool (hold)", "Space");
        Canvas("Decrease brush size", "OemOpenBrackets");
        Canvas("Increase brush size", "OemCloseBrackets");
        Canvas("Decrease brush hardness", "OemOpenBrackets", Shift);
        Canvas("Increase brush hardness", "OemCloseBrackets", Shift);
        Canvas("Previous blend mode", "OemMinus", Shift);
        Canvas("Next blend mode", "OemPlus", Shift);
        Canvas("Cycle shape kind", "U", Shift);
        for (var digit = 0; digit <= 9; digit++)
        {
            Canvas($"Opacity digit {digit} (type two for exact %)", $"D{digit}");
        }
        foreach (var (direction, key) in new[]
                 {
                     ("Left", "Left"), ("Right", "Right"), ("Up", "Up"), ("Down", "Down"),
                 })
        {
            Canvas($"Nudge {direction} 1 px", key);
            Canvas($"Nudge {direction} 10 px", key, Shift);
            // The Mac's Command-with-an-arrow rows: they move the pixels inside the selection rather than the
            // layer the plain arrows move — the one place a modifier changes what an arrow does.
            Canvas($"Move selected pixels {direction} 1 px", key, Ctrl);
            Canvas($"Move selected pixels {direction} 10 px", key, Ctrl | Shift);
        }
        Canvas("Apply Canvas Operation", "Enter");
        Canvas("Cancel Canvas Operation", "Escape");
        return rows;
    }
}

/// <summary>
/// The shortcut keys a person has changed, which belong to the person rather than to a document: a row left on
/// its original key is not written out, and a file that would break a rule is dropped whole rather than
/// followed — which is what the Mac build does with a saved table that does not pass its own check.
/// </summary>
public sealed class ShortcutDefaults
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Where the table is kept for whoever is using the app.</summary>
    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "shortcuts.json");

    /// <summary>Only the rows that are not on their original key.</summary>
    public Dictionary<string, ShortcutChord> Overrides { get; set; } = [];

    /// <summary>The rows as they were last left, or no changes at all when there is nothing to read.</summary>
    public static ShortcutDefaults Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ShortcutDefaults();
            var read = JsonSerializer.Deserialize<ShortcutDefaults>(File.ReadAllText(path), Json);
            return read?.Kept() ?? new ShortcutDefaults();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // A kept table is a convenience: one that cannot be read is not worth failing to start over.
            return new ShortcutDefaults();
        }
    }

    /// <summary>Replace the saved table only after the complete new file has been flushed.</summary>
    public bool Save(string path) => Save(path, out _);

    public bool Save(string path, out string? error)
    {
        error = null;
        string? temporary = null;
        try
        {
            var target = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            temporary = Path.Combine(folder, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, Kept(), Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or JsonException)
        {
            error = failure.Message;
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// The same table with anything a hand-edited file could have got wrong put right: a table that does not
    /// pass the check is emptied rather than half followed, rows this build has no place for are dropped, and a
    /// row written down on the key it already had is dropped as the nothing it is.
    /// </summary>
    private ShortcutDefaults Kept()
    {
        if (Shortcuts.Problem(Overrides) is not null) return new ShortcutDefaults();
        var kept = new Dictionary<string, ShortcutChord>();
        foreach (var definition in Shortcuts.Definitions)
        {
            if (!Overrides.TryGetValue(definition.ID, out var chord)) continue;
            if (chord == definition.Original) continue;
            kept[definition.ID] = chord;
        }
        return new ShortcutDefaults { Overrides = kept };
    }
}
