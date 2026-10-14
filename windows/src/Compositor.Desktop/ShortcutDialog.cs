using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.IO;

namespace Compositor.Desktop;

/// <summary>
/// Edit ▸ Keyboard Shortcuts…: every row of the table under its group, with the key it holds beside it. Clicking
/// a key starts recording and the next key pressed is the row's new one — the Mac build's own shortcut sheet,
/// with the Mac's three groups cut to the two this port has.
/// <para>
/// The recorder takes its keys through a tunnelling handler on the dialog itself, so Return, Escape, Tab and the
/// arrows can be recorded like any other key; while a row is recording, the sheet's own Return and Escape are
/// held off, so that recording Return does not also save the sheet.
/// </para>
/// </summary>
internal sealed class ShortcutDialog : DialogWindow
{
    /// <summary>The table being edited, every row on a key, which is what the check runs on.</summary>
    private readonly Dictionary<string, ShortcutChord> _draft;
    private readonly TextBox _search = new() { PlaceholderText = L10n.T("Search shortcuts"), Width = 400 };
    private readonly StackPanel _list = new() { Spacing = 2 };
    /// <summary>What scrolls the list, kept so that the check can take the list out of it to be drawn.</summary>
    private readonly ScrollViewer _scroller = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _complaint = new()
    {
        Foreground = Brushes.Orange,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };
    private readonly Button _save = new() { Content = L10n.T("Save"), IsDefault = true };
    private readonly Button _restore = new() { Content = L10n.T("Restore Defaults"), HorizontalAlignment = HorizontalAlignment.Left };
    /// <summary>The row being recorded, or null when the keys are the sheet's own again.</summary>
    private string? _recording;
    private Dictionary<string, ShortcutChord>? _result;

    internal ShortcutDialog(IReadOnlyDictionary<string, ShortcutChord> overrides)
    {
        Title = L10n.T("Keyboard Shortcuts");
        Width = 660;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _draft = Shortcuts.Effective(overrides);
        _search.TextChanged += (_, _) => ShowRows();

        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        _restore.Click += (_, _) => RestoreDefaults();
        cancel.Click += (_, _) => Close();
        _save.Click += (_, _) => Keep();

        var bottom = new DockPanel();
        bottom.Children.Add(_restore);
        DockPanel.SetDock(_restore, Dock.Left);
        bottom.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, _save },
        });

        var top = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = L10n.T("Click a key, then press the one you want. Backspace clears a row and Escape stops recording; the changes apply when you save."),
                    TextWrapping = TextWrapping.Wrap,
                },
                _search,
                _complaint,
            },
        };

        // The list is last, so it is what fills the room the other two leave.
        _scroller.Content = _list;
        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children = { top, bottom, _scroller },
        };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = body;
        ShowRows();
        // The keys come here first, wherever in the sheet they are pressed, so recording can take any of them.
        AddHandler(KeyDownEvent, Recording, RoutingStrategies.Tunnel);
    }

    /// <summary>Rebuilds the list from the draft, under the groups the table declares and the search text.</summary>
    private void ShowRows()
    {
        _list.Children.Clear();
        var search = _search.Text ?? "";
        foreach (var group in Shortcuts.Groups)
        {
            var rows = Shortcuts.Definitions
                .Where(row => row.Group == group)
                .Where(row => search.Length == 0
                    || row.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || L10n.T(row.Title).Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (rows.Count == 0) continue;
            _list.Children.Add(new TextBlock
            {
                Text = L10n.T(group),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 10, 0, 4),
            });
            foreach (var row in rows) _list.Children.Add(KeyRow(row));
        }
        Complaint();
    }

    /// <summary>One row: what it is called, and the key on it — press the key to start recording a new one.</summary>
    private Control KeyRow(ShortcutDefinition definition)
    {
        var chord = _draft[definition.ID];
        var button = new Button
        {
            Content = _recording == definition.ID ? L10n.T("Press keys…") : chord.IsBound ? chord.Label : "—",
            Width = 150,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        button.Click += (_, _) =>
        {
            _recording = definition.ID;
            ShowRows();
        };
        var name = new TextBlock { Text = L10n.T(definition.Title), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(button, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,160"),
            Margin = new Thickness(0, 1, 0, 1),
            Children = { name, button },
        };
    }

    /// <summary>
    /// A key while the sheet has it. While a row is recording, the key is that recording's whatever it is —
    /// Return, Escape, Tab and the arrows included — which is why the sheet listens on the way down.
    /// </summary>
    private void Recording(object? sender, KeyEventArgs e)
    {
        if (_recording is not { } id) return;
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            StopRecording();
            return;
        }
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            _draft[id] = ShortcutChord.Unbound;
            StopRecording();
            return;
        }
        // Somebody letting go of Control is not asking for Control to be a shortcut.
        if (ShortcutKeys.Bare(e.Key)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            _complaint.Text = L10n.T("The Windows key is reserved by Windows");
            _complaint.IsVisible = true;
            return;
        }
        _draft[id] = new ShortcutChord(e.Key.ToString(), ShortcutKeys.Held(e.KeyModifiers));
        StopRecording();
    }

    private void StopRecording()
    {
        _recording = null;
        ShowRows();
    }

    private void RestoreDefaults()
    {
        _recording = null;
        _draft.Clear();
        foreach (var definition in Shortcuts.Definitions) _draft[definition.ID] = definition.Original;
        ShowRows();
    }

    /// <summary>Whatever would stop the sheet being saved, said in one line, or nothing at all.</summary>
    private void Complaint()
    {
        var problem = Shortcuts.Problem(_draft);
        _complaint.Text = ShortcutKeys.ProblemText(problem) ?? "";
        _complaint.IsVisible = problem is not null;
        _save.IsEnabled = _recording is null && problem is null;
    }

    /// <summary>The rows that have moved off their original key, which is what a saved table is made of.</summary>
    private Dictionary<string, ShortcutChord> Moved()
    {
        var moved = new Dictionary<string, ShortcutChord>();
        foreach (var definition in Shortcuts.Definitions)
        {
            if (_draft[definition.ID] != definition.Original) moved[definition.ID] = _draft[definition.ID];
        }
        return moved;
    }

    private void Keep()
    {
        if (Shortcuts.Problem(_draft) is not null) return;
        _result = Moved();
        Close();
    }

    /// <summary>The rows the sheet was told to change, or null when it was dismissed or refused.</summary>
    public static async Task<Dictionary<string, ShortcutChord>?> Show(
        Window owner, IReadOnlyDictionary<string, ShortcutChord> overrides)
    {
        var dialog = new ShortcutDialog(overrides);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    /// <summary>The body this dialog is made of, handed over and let go of, for the check to drive.</summary>
    internal Control TakeBody()
    {
        var body = (Control)Content!;
        Content = null;
        return body;
    }

    /// <summary>What the sheet is saying would stop it being saved, for the check.</summary>
    internal string ComplaintText => _complaint.Text ?? "";

    /// <summary>Whether Save is on offer, which the complaint line gates.</summary>
    internal bool CanSave => _save.IsEnabled;

    /// <summary>The table as the sheet has it, for the check.</summary>
    internal IReadOnlyDictionary<string, ShortcutChord> Draft => _draft;

    /// <summary>
    /// The list of rows on its own, let go of by the sheet so that it can be drawn: the sheet is a window whose
    /// list sits in a scroll view, and a bitmap does not lay out what a scroll view holds.
    /// </summary>
    internal Control TakeRows()
    {
        _scroller.Content = null;
        return _list;
    }

    /// <summary>The rows that have moved, which Save would keep, for the check.</summary>
    internal Dictionary<string, ShortcutChord> Changes() => Moved();

    /// <summary>A saved rebind and an unbound row both return to the shipped table through the real button.</summary>
    internal static string RestoreDefaultsSelfCheck()
    {
        var undo = $"{Shortcuts.Menus}:Undo";
        var copy = $"{Shortcuts.Menus}:Copy";
        var overrides = new Dictionary<string, ShortcutChord>
        {
            [undo] = new("Y", ShortcutModifiers.Control),
            [copy] = ShortcutChord.Unbound,
        };
        var dialog = new ShortcutDialog(overrides);
        try
        {
            if (dialog.Draft[undo] != overrides[undo] || dialog.Draft[copy].IsBound)
                throw new InvalidOperationException("The shortcut restore fixture did not load its saved overrides.");
            dialog._recording = undo;
            dialog._restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (dialog._recording is not null || !dialog.CanSave || dialog.Changes().Count != 0
                || Shortcuts.Definitions.Any(definition => dialog.Draft[definition.ID] != definition.Original))
                throw new InvalidOperationException("Restore Defaults kept a custom shortcut or an unbound row.");
            if (dialog._result is not null || overrides[undo].Key != "Y" || overrides[copy].IsBound)
                throw new InvalidOperationException("Restore Defaults saved changes before the user chose Save.");
            return "shortcut defaults: saved custom bindings and unbound rows restored; recording stopped; changes remain unsaved";
        }
        finally { dialog.Close(); }
    }

    /// <summary>
    /// Records a key on a row the way pressing it would, so that the check drives the path a person drives
    /// rather than reaching past the recorder into the table.
    /// </summary>
    internal void Record(string id, Key key, KeyModifiers modifiers)
    {
        _recording = id;
        RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });
    }
}
