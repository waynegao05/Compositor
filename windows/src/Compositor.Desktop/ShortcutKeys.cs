using Avalonia.Input;
using Compositor.Core.IO;

namespace Compositor.Desktop;

/// <summary>
/// Where a shortcut's names cross into Avalonia's: the core's modifier bits and Avalonia's are not laid out in
/// the same order, and a key written down in the table is a string until it is put through
/// <see cref="Avalonia.Input.Key"/>. One home for the crossing, because the window that delivers a key and the
/// dialog that records one have to read it the same way.
/// </summary>
internal static class ShortcutKeys
{
    /// <summary>
    /// Localize the core's validation message at the display boundary. The core's identifiers, validation
    /// rules and persisted shortcut titles remain unchanged. Unknown messages keep their original text.
    /// </summary>
    internal static string? ProblemText(string? problem)
    {
        if (problem is null) return null;
        const string invalid = " is not one key with modifiers Ctrl, Alt or Shift";
        const string reserved = " is reserved by Windows";
        const string conflict = " is on both ";
        if (problem.EndsWith(invalid, StringComparison.Ordinal))
            return L10n.F("{0} is not one key with modifiers Ctrl, Alt or Shift", problem[..^invalid.Length]);
        if (problem.EndsWith(reserved, StringComparison.Ordinal))
            return L10n.F("{0} is reserved by Windows", problem[..^reserved.Length]);
        var split = problem.IndexOf(conflict, StringComparison.Ordinal);
        if (split >= 0)
        {
            var titles = problem[(split + conflict.Length)..];
            // Resolve against the known titles, so a title containing " and " is still unambiguous.
            foreach (var definition in Shortcuts.Definitions)
            {
                var prefix = definition.Title + " and ";
                if (!titles.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var other = titles[prefix.Length..];
                if (!Shortcuts.Definitions.Any(row => row.Title == other)) continue;
                return L10n.F("{0} is on both {1} and {2}", problem[..split],
                    L10n.T(definition.Title), L10n.T(other));
            }
        }
        return problem;
    }

    /// <summary>The modifiers of a chord in Avalonia's bits.</summary>
    internal static KeyModifiers Modifiers(ShortcutModifiers held)
    {
        var modifiers = KeyModifiers.None;
        if (held.HasFlag(ShortcutModifiers.Control)) modifiers |= KeyModifiers.Control;
        if (held.HasFlag(ShortcutModifiers.Alt)) modifiers |= KeyModifiers.Alt;
        if (held.HasFlag(ShortcutModifiers.Shift)) modifiers |= KeyModifiers.Shift;
        return modifiers;
    }

    /// <summary>The same the other way about, for a key that has just been pressed.</summary>
    internal static ShortcutModifiers Held(KeyModifiers modifiers)
    {
        var held = ShortcutModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) held |= ShortcutModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) held |= ShortcutModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) held |= ShortcutModifiers.Shift;
        return held;
    }

    /// <summary>
    /// A chord as this build knows it: the key name put through Avalonia's own enum, so that the two spellings
    /// of one key — "OemOpenBrackets" and "Oem4" are the same key — come out the same, and a key pressed finds
    /// its row however the row was written down. Null is a name this build has no key for.
    /// </summary>
    internal static ShortcutChord? Known(ShortcutChord chord) =>
        chord.IsBound && Enum.TryParse<Key>(chord.Key, out var key)
            ? new ShortcutChord(key.ToString(), chord.Modifiers)
            : null;

    /// <summary>
    /// The gesture a chord stands for, which is what a menu row is shown with and what the recorder hands the
    /// list. Null when the chord holds nothing or names a key this build has never heard of.
    /// </summary>
    internal static KeyGesture? Gesture(ShortcutChord chord) =>
        Known(chord) is { } known && Enum.TryParse<Key>(known.Key, out var key)
            ? new KeyGesture(key, Modifiers(known.Modifiers))
            : null;

    /// <summary>
    /// Whether a key is only a modifier: holding Control is not a combination, and a recorder that took it
    /// would leave a row on a key nothing can press.
    /// </summary>
    internal static bool Bare(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;
}
