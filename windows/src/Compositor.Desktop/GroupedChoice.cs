using Avalonia.Controls;
using Avalonia.Layout;

namespace Compositor.Desktop;

/// <summary>
/// Fills a pop-up with the choices the Mac build lists in groups — the blend modes, the dither looks — drawing
/// the same line between the groups that its menus draw, so a long list stays readable.
/// <para>
/// A rule is an item that cannot be chosen: it is disabled and unfocusable, so the keyboard walks past it, and
/// what it stands for is nothing. The caller keeps the list this hands back and reads the choice out of it, so
/// an index into the pop-up is never mistaken for an index into the enum.
/// </para>
/// </summary>
internal static class GroupedChoice
{
    /// <summary>
    /// Fills the box with each group's labels, a rule between the groups, and hands back what each item stands
    /// for: the choice, or null for a rule. The box's items are cleared first.
    /// </summary>
    public static List<T?> Fill<T>(ComboBox box, IReadOnlyList<T[]> groups, Func<T, string> label)
        where T : struct
    {
        var rows = new List<T?>();
        box.Items.Clear();
        foreach (var group in groups)
        {
            if (rows.Count > 0)
            {
                box.Items.Add(new ComboBoxItem
                {
                    Content = new Border
                    {
                        Height = 1,
                        Margin = new Avalonia.Thickness(4, 3),
                        Background = Skin.MenuRule,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    },
                    IsEnabled = false,
                    Focusable = false,
                });
                rows.Add(null);
            }
            foreach (var choice in group)
            {
                box.Items.Add(new ComboBoxItem { Content = L10n.T(label(choice)) });
                rows.Add(choice);
            }
        }
        return rows;
    }
}
