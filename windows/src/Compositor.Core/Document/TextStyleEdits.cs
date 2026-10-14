using Compositor.Core.Format;

namespace Compositor.Core.Document;

/// <summary>Text replacements keep style ranges in UTF-16 offsets, as stored in a project.</summary>
public static class TextStyleEdits
{
    public static LayerTextStyle Copy(LayerTextStyle style) => new()
    {
        Content = style.Content, FontName = style.FontName, FontSize = style.FontSize,
        Red = style.Red, Green = style.Green, Blue = style.Blue, Alignment = style.Alignment,
        Tracking = style.Tracking, Leading = style.Leading, BoxSize = style.BoxSize,
        ColorRuns = style.ColorRuns?.Select(run => new LayerTextColorRun
        {
            Location = run.Location, Length = run.Length, Red = run.Red, Green = run.Green, Blue = run.Blue,
        }).ToList(),
        FontRuns = style.FontRuns?.Select(run => new LayerTextFontRun
        {
            Location = run.Location, Length = run.Length, FontName = run.FontName,
        }).ToList(),
    };

    public static LayerTextStyle Splice(LayerTextStyle style, int at, int remove, string insert)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        ArgumentOutOfRangeException.ThrowIfNegative(remove);
        if (at > style.Content.Length || remove > style.Content.Length - at)
            throw new ArgumentOutOfRangeException(nameof(remove));
        var next = Copy(style);
        next.Content = style.Content.Remove(at, remove).Insert(at, insert);
        if (next.Content.Length > 100_000) return next;
        var colors = Enumerable.Repeat((style.Red, style.Green, style.Blue), style.Content.Length).ToArray();
        foreach (var run in style.ColorRuns ?? [])
            Array.Fill(colors, (run.Red, run.Green, run.Blue), run.Location, run.Length);
        var fonts = Enumerable.Repeat(style.FontName, style.Content.Length).ToArray();
        foreach (var run in style.FontRuns ?? []) Array.Fill(fonts, run.FontName, run.Location, run.Length);
        // A replacement inherits its first character. Insertion inherits the character before the caret,
        // or the first one when typing at the beginning of an existing paragraph.
        var inherit = style.Content.Length == 0 ? -1
            : Math.Min(remove > 0 ? at : Math.Max(0, at - 1), style.Content.Length - 1);
        var editedColors = Replace(colors, at, remove, insert.Length,
            inherit < 0 ? (style.Red, style.Green, style.Blue) : colors[inherit]);
        var editedFonts = Replace(fonts, at, remove, insert.Length,
            inherit < 0 ? style.FontName : fonts[inherit]);
        var colorRuns = new List<LayerTextColorRun>();
        foreach (var (start, length, value) in Runs(editedColors))
            if (value != (style.Red, style.Green, style.Blue)) colorRuns.Add(new LayerTextColorRun
            { Location = start, Length = length, Red = value.Item1, Green = value.Item2, Blue = value.Item3 });
        var fontRuns = new List<LayerTextFontRun>();
        foreach (var (start, length, value) in Runs(editedFonts))
            if (value != style.FontName) fontRuns.Add(new LayerTextFontRun
            { Location = start, Length = length, FontName = value });
        next.ColorRuns = colorRuns.Count == 0 ? null : colorRuns;
        next.FontRuns = fontRuns.Count == 0 ? null : fontRuns;
        return next;
    }

    private static T[] Replace<T>(T[] values, int at, int remove, int insert, T inherited) =>
        values.Take(at).Concat(Enumerable.Repeat(inherited, insert)).Concat(values.Skip(at + remove)).ToArray();

    private static IEnumerable<(int Start, int Length, T Value)> Runs<T>(T[] values)
    {
        for (var start = 0; start < values.Length;)
        {
            var end = start + 1;
            while (end < values.Length && EqualityComparer<T>.Default.Equals(values[start], values[end])) end++;
            yield return (start, end - start, values[start]);
            start = end;
        }
    }
}
