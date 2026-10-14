using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Threading;
using Compositor.Core.Document;
using Avalonia.Media;
using Compositor.Core.Format;
using TextAlignment = Compositor.Core.Format.TextAlignment;

namespace Compositor.Desktop;

/// <summary>
/// The Type tool's settings: the text, the face, and how it is set. Avalonia ships no such dialog, so this
/// is one — a box for the words, the fields a text layer holds, and OK.
/// </summary>
internal sealed class TextDialog : DialogWindow
{
    private readonly StyledContentBox _content = new() { AcceptsReturn = true, MaxLength = 100_000, Height = 120, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _font = new();
    private readonly TextBox _size = new();
    private readonly TextBox _colour = new();
    private readonly TextBox _tracking = new();
    private readonly TextBox _leading = new();
    private readonly ComboBox _alignment = new();
    private LayerTextStyle? _result;

    private TextDialog(string title, LayerTextStyle style)
    {
        Title = L10n.T(title);
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _content.SetStyle(style);
        _font.Text = style.FontName;
        _size.Text = $"{style.FontSize:0.##}";
        _colour.Text = $"{style.Red * 255:0},{style.Green * 255:0},{style.Blue * 255:0}";
        _tracking.Text = $"{style.Tracking:0.##}";
        _leading.Text = $"{style.Leading:0.##}";
        _alignment.ItemsSource = new[] { "Left", "Centre", "Right" }.Select(L10n.T).ToArray();
        _alignment.SelectedIndex = style.Alignment switch
        {
            TextAlignment.Center => 1,
            TextAlignment.Right => 2,
            _ => 0,
        };
        var ok = new Button { Content = L10n.T("OK"), IsDefault = true };
        var cancel = new Button { Content = L10n.T("Cancel"), IsCancel = true };
        ok.Click += (_, _) => Accept(style);
        cancel.Click += (_, _) => Close();
        var rows = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = L10n.T("Text") },
                _content,
                Row("Font", _font),
                Row("Size in pixels", _size),
                Row("Color, red green blue 0-255", _colour),
                Row("Tracking, pixels between letters", _tracking),
                Row("Leading, line to line 0 for auto", _leading),
                new TextBlock { Text = L10n.T("Alignment") },
                _alignment,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { cancel, ok },
                },
            },
        };
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Children = { rows } } };
        Opened += (_, _) => _content.Focus();
    }

    private static Control Row(string label, Control field) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = L10n.T(label) }, field },
    };

    private void Accept(LayerTextStyle original)
    {
        var style = new LayerTextStyle
        {
            Content = _content.Text ?? "",
            FontName = string.IsNullOrWhiteSpace(_font.Text) ? original.FontName : _font.Text.Trim(),
            FontSize = original.FontSize,
            Red = original.Red,
            Green = original.Green,
            Blue = original.Blue,
            Alignment = _alignment.SelectedIndex switch
            {
                1 => TextAlignment.Center,
                2 => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
            Tracking = original.Tracking,
            Leading = original.Leading,
            BoxSize = original.BoxSize,
            ColorRuns = _content.CurrentStyle.ColorRuns,
            FontRuns = _content.CurrentStyle.FontRuns,
        };
        if (Number(_size.Text, 1, 2000) is not { } size) return;
        style.FontSize = size;
        if (Colour(_colour.Text) is not { } colour) return;
        (style.Red, style.Green, style.Blue) = colour;
        if (Number(_tracking.Text, -100, 1000) is not { } tracking) return;
        style.Tracking = tracking;
        if (Number(_leading.Text, 0, 5000) is not { } leading) return;
        style.Leading = leading;
        if (!style.IsValid) return;
        _result = style;
        Close();
    }

    private double? Number(string? typed, double least, double most) =>
        double.TryParse((typed ?? "").Trim(), out var value) && value >= least && value <= most ? value : null;

    private (double Red, double Green, double Blue)? Colour(string? typed)
    {
        var parts = (typed ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var values = new double[3];
        for (var index = 0; index < 3; index++)
        {
            if (!double.TryParse(parts[index], out values[index]) || values[index] is < 0 or > 255) return null;
            values[index] /= 255;
        }
        return (values[0], values[1], values[2]);
    }

    // Keep our own text-and-style undo entries. TextBox's string-only history cannot restore a deleted run.
    private sealed class StyledContentBox : TextBox
    {
        private sealed record Entry(LayerTextStyle Style, int Start, int End);
        private readonly Stack<Entry> _undo = new();
        private readonly Stack<Entry> _redo = new();
        private LayerTextStyle? _style;
        private bool _restoring;
        private int? _editAt;
        private bool _backspace;
        protected override Type StyleKeyOverride => typeof(TextBox);
        internal LayerTextStyle CurrentStyle => _style ?? throw new InvalidOperationException("Text style was not initialized.");

        internal StyledContentBox()
        {
            IsUndoEnabled = false;
            MenuItem Item(string label, Action action)
            {
                var item = new MenuItem { Header = L10n.T(label) };
                item.Click += (_, _) => action();
                return item;
            }
            ContextMenu = new ContextMenu { ItemsSource = new Control[]
            {
                Item("Undo", () => Restore(_undo, _redo)), Item("Redo", () => Restore(_redo, _undo)),
                new Separator(), Item("Cut", () => Cut()), Item("Copy", () => Copy()),
                Item("Paste", () => Paste()), Item("Select All", SelectAll),
            }};
        }

        internal void SetStyle(LayerTextStyle style)
        {
            _restoring = true;
            try { _style = TextStyleEdits.Copy(style); Text = style.Content; }
            finally { _restoring = false; }
            _undo.Clear();
            _redo.Clear();
        }

        private Entry Snapshot() => new(TextStyleEdits.Copy(CurrentStyle), SelectionStart, SelectionEnd);

        private static void Push(Stack<Entry> history, Entry entry)
        {
            history.Push(entry);
            if (history.Count <= 100) return;
            var kept = history.Take(100).Reverse().ToArray();
            history.Clear();
            foreach (var previous in kept) history.Push(previous);
        }

        private void Restore(Stack<Entry> from, Stack<Entry> to)
        {
            if (from.Count == 0) return;
            Push(to, Snapshot());
            var entry = from.Pop();
            _restoring = true;
            try
            {
                _style = TextStyleEdits.Copy(entry.Style);
                Text = entry.Style.Content;
                SelectionStart = entry.Start;
                SelectionEnd = entry.End;
            }
            finally { _restoring = false; }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Z or Key.Y)
            {
                var redo = e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                Restore(redo ? _redo : _undo, redo ? _undo : _redo);
                e.Handled = true;
                return;
            }
            if (SelectionStart == SelectionEnd)
            {
                if (e.Key == Key.Delete) _editAt = CaretIndex;
                else if (e.Key == Key.Back) { _editAt = CaretIndex; _backspace = true; }
            }
            try { base.OnKeyDown(e); }
            finally { _editAt = null; _backspace = false; }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            // Before TextBox updates/coerces its caret and selection, capture the exact replacement span.
            if (change.Property == TextProperty && !_restoring && _style is { } style)
            {
                var before = style.Content;
                var after = change.GetNewValue<string?>() ?? "";
                if (before != after)
                {
                    Push(_undo, Snapshot());
                    _redo.Clear();
                    var at = Math.Clamp(_editAt ?? Math.Min(SelectionStart, SelectionEnd), 0, before.Length);
                    var remove = Math.Abs(SelectionEnd - SelectionStart);
                    var insertLength = after.Length - before.Length + remove;
                    if (remove == 0 && after.Length < before.Length)
                    {
                        remove = before.Length - after.Length;
                        if (_backspace) at = Math.Max(0, at - remove);
                        insertLength = 0;
                    }
                    bool Matches() => insertLength >= 0 && at + remove <= before.Length
                        && at + insertLength <= after.Length
                        && before.AsSpan(0, at).SequenceEqual(after.AsSpan(0, at))
                        && before.AsSpan(at + remove).SequenceEqual(after.AsSpan(at + insertLength));
                    if (!Matches())
                    {
                        // Programmatic replacement of the whole string has no selection to describe it.
                        at = 0;
                        while (at < before.Length && at < after.Length && before[at] == after[at]) at++;
                        var tail = 0;
                        while (tail < before.Length - at && tail < after.Length - at
                               && before[^(tail + 1)] == after[^(tail + 1)]) tail++;
                        remove = before.Length - at - tail;
                        insertLength = after.Length - at - tail;
                    }
                    _style = TextStyleEdits.Splice(style, at, remove, after.Substring(at, insertLength));
                }
            }
            base.OnPropertyChanged(change);
        }
    }

    internal static string RichTextSelfCheck()
    {
        var checks = 0;
        foreach (var at in new[] { 0, 1 })
        foreach (var key in new[] { Key.Delete, Key.Back })
        {
            var style = new LayerTextStyle
            {
                Content = "AAA", FontSize = 24,
                ColorRuns = [new LayerTextColorRun { Location = 1, Length = 1, Blue = 1 }],
                FontRuns = [new LayerTextFontRun { Location = 1, Length = 1, FontName = "Arial" }],
            };
            var dialog = new TextDialog("Edit Text", style);
            dialog.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var box = dialog._content;
                box.Focus();
                box.CaretIndex = key == Key.Back ? at + 1 : at;
                box.SelectionStart = box.SelectionEnd = box.CaretIndex;
                box.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key });
                var changed = box.CurrentStyle;
                var expected = TextStyleEdits.Splice(style, at, 1, "");
                if (changed.Content != "AA" || changed.ColorRuns?.SingleOrDefault()?.Location != expected.ColorRuns?.SingleOrDefault()?.Location
                    || changed.FontRuns?.SingleOrDefault()?.Location != expected.FontRuns?.SingleOrDefault()?.Location)
                    throw new InvalidOperationException($"Repeated text style was deleted at the wrong offset: {key}, {at}.");
                box.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
                if (box.Text != "AAA" || box.CurrentStyle.ColorRuns?.SingleOrDefault()?.Location != 1
                    || box.CurrentStyle.FontRuns?.SingleOrDefault()?.Location != 1)
                    throw new InvalidOperationException("Text dialog undo did not restore the removed rich-text run.");
                box.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Y, KeyModifiers = KeyModifiers.Control });
                if (box.Text != "AA" || box.CurrentStyle.ColorRuns?.SingleOrDefault()?.Location != expected.ColorRuns?.SingleOrDefault()?.Location)
                    throw new InvalidOperationException("Text dialog redo did not restore the edited run.");
                checks += 3;
            }
            finally { dialog.Close(); }
        }
        return $"text dialog: {checks} repeated-character deletion, undo and redo checks passed";
    }

    /// <summary>The style that was asked for, or null when the dialog was dismissed.</summary>
    public static async Task<LayerTextStyle?> Ask(Window owner, string title, LayerTextStyle style)
    {
        var dialog = new TextDialog(title, style);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
