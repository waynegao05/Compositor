using Compositor.Core.IO;

namespace Compositor.Core.Tests;

/// <summary>
/// The key shortcut table: that the rows it ships with are themselves sound, that a table a person has got
/// wrong is refused with a message naming what is wrong, and that a file is dropped rather than half followed.
/// </summary>
public class ShortcutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "compositor-keys-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_folder, "shortcuts.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, ShortcutChord> Table(params (string ID, ShortcutChord Chord)[] rows) =>
        rows.ToDictionary(row => row.ID, row => row.Chord);

    private static string AnyID(string title) => $"{Shortcuts.Menus}:{title}";

    [Fact]
    public void TheRowsItShipsWithAreSound()
    {
        // The table every install starts on has to pass the check it runs on everyone else's.
        Assert.Null(Shortcuts.Problem(new Dictionary<string, ShortcutChord>()));
    }

    [Fact]
    public void EveryRowIsNamedOnce()
    {
        var ids = Shortcuts.Definitions.Select(row => row.ID).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(Shortcuts.Definitions, row => Assert.StartsWith(row.Group + ":", row.ID, StringComparison.Ordinal));
        Assert.All(Shortcuts.Definitions, row => Assert.Contains(row.Group, Shortcuts.Groups));
    }

    [Fact]
    public void AChordReadsAsItsKeys()
    {
        Assert.Equal("Ctrl+Shift+Z", new ShortcutChord("Z", ShortcutModifiers.Control | ShortcutModifiers.Shift).Label);
        Assert.Equal("Alt+Delete", new ShortcutChord("Delete", ShortcutModifiers.Alt).Label);
        Assert.Equal("Ctrl+[", new ShortcutChord("OemOpenBrackets", ShortcutModifiers.Control).Label);
        Assert.Equal("3", new ShortcutChord("D3").Label);
        Assert.Equal("Space", new ShortcutChord("Space").Label);
        Assert.Equal("", ShortcutChord.Unbound.Label);
    }

    [Fact]
    public void AKeyOnTwoRowsNamesBoth()
    {
        // Move Layer Up and Move Layer Down are the two rows this is done to.
        var table = Table(
            (AnyID("Move Layer Up"), new ShortcutChord("OemCloseBrackets", ShortcutModifiers.Control)),
            (AnyID("Move Layer Down"), new ShortcutChord("OemCloseBrackets", ShortcutModifiers.Control)));
        var problem = Shortcuts.Problem(table);
        Assert.NotNull(problem);
        Assert.Contains("Move Layer Up", problem, StringComparison.Ordinal);
        Assert.Contains("Move Layer Down", problem, StringComparison.Ordinal);
        Assert.Contains("Ctrl+]", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverrideThatClashesWithAnOriginalNamesBoth()
    {
        // A rebind must not land on a key some row still holds by default.
        var table = Table((AnyID("Undo"), new ShortcutChord("E", ShortcutModifiers.Control)));
        var problem = Shortcuts.Problem(table);
        Assert.NotNull(problem);
        Assert.Contains("Undo", problem, StringComparison.Ordinal);
        Assert.Contains("Merge Layers", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Tab", ShortcutModifiers.Alt)]
    [InlineData("Escape", ShortcutModifiers.Control)]
    [InlineData("F4", ShortcutModifiers.Alt)]
    [InlineData("F12", ShortcutModifiers.None)]
    [InlineData("B", ShortcutModifiers.Alt)]
    [InlineData("B", ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
    public void WhatWindowsAnswersIsRefused(string key, ShortcutModifiers modifiers)
    {
        var table = Table((AnyID("Undo"), new ShortcutChord(key, modifiers)));
        var problem = Shortcuts.Problem(table);
        Assert.NotNull(problem);
        Assert.Contains("reserved by Windows", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlWithAltAndALetterIsAllowed()
    {
        // The one Alt-and-a-letter that is free: Ctrl holds it away from the menus' own access keys.
        var table = Table((AnyID("Undo"), new ShortcutChord("B", ShortcutModifiers.Control | ShortcutModifiers.Alt)));
        Assert.Null(Shortcuts.Problem(table));
    }

    [Fact]
    public void ARowMayBeLeftWithNoKey()
    {
        var table = Table((AnyID("New Guide"), ShortcutChord.Unbound));
        Assert.Null(Shortcuts.Problem(table));
        Assert.False(Shortcuts.Effective(table)[AnyID("New Guide")].IsBound);
    }

    [Fact]
    public void AChordThatIsNotAKeyIsRefused()
    {
        var table = Table((AnyID("Undo"), new ShortcutChord("Ctrl+Z")));
        Assert.Contains("is not one key", Shortcuts.Problem(table)!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTableIsTheDefaultsWithTheChangesOnTop()
    {
        var id = AnyID("Undo");
        var effective = Shortcuts.Effective(Table((id, new ShortcutChord("Y", ShortcutModifiers.Control))));
        Assert.Equal(new ShortcutChord("Y", ShortcutModifiers.Control), effective[id]);
        Assert.Equal(Shortcuts.Definitions.Count, effective.Count);
    }

    [Fact]
    public void ChangedRowsComeBackAsTheyWereLeft()
    {
        var saved = new ShortcutDefaults
        {
            Overrides = Table(
                (AnyID("Undo"), new ShortcutChord("Y", ShortcutModifiers.Control)),
                (AnyID("New Guide"), new ShortcutChord("G", ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift))),
        };
        saved.Save(Path_);
        var read = ShortcutDefaults.Load(Path_);
        Assert.Equal(new ShortcutChord("Y", ShortcutModifiers.Control), read.Overrides[AnyID("Undo")]);
        Assert.Equal(2, read.Overrides.Count);
    }

    [Fact]
    public void ARowLeftOnItsOriginalKeyIsNotWrittenOut()
    {
        var saved = new ShortcutDefaults
        {
            Overrides = Table((AnyID("Undo"), Shortcuts.Definitions.First(row => row.ID == AnyID("Undo")).Original)),
        };
        saved.Save(Path_);
        Assert.Empty(ShortcutDefaults.Load(Path_).Overrides);
    }

    [Fact]
    public void ARowThisBuildHasNoPlaceForIsDropped()
    {
        var saved = new ShortcutDefaults
        {
            Overrides = Table(
                ("Menus:Something Else Entirely", new ShortcutChord("Y", ShortcutModifiers.Control)),
                (AnyID("Undo"), new ShortcutChord("Y", ShortcutModifiers.Control))),
        };
        saved.Save(Path_);
        var read = ShortcutDefaults.Load(Path_);
        Assert.Single(read.Overrides);
        Assert.True(read.Overrides.ContainsKey(AnyID("Undo")));
    }

    [Fact]
    public void AFileThatWouldBreakARuleIsDroppedWhole()
    {
        // Written by hand: two rows on one key. Nothing of it is followed, not even the rows that were fine.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path_, """
            {
              "Overrides": {
                "Menus:Undo": { "Key": "E", "Modifiers": 1 },
                "Menus:Redo": { "Key": "Y", "Modifiers": 1 }
              }
            }
            """);
        Assert.Empty(ShortcutDefaults.Load(Path_).Overrides);
    }

    [Fact]
    public void AFileThatCannotBeReadIsNoFile()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path_, "{ not json at all");
        Assert.Empty(ShortcutDefaults.Load(Path_).Overrides);
        Assert.Empty(ShortcutDefaults.Load(Path.Combine(_folder, "nothing-here.json")).Overrides);
    }

    [Fact]
    public void SaveReportsFailuresAndRemovesItsTemporaryFile()
    {
        Directory.CreateDirectory(_folder);
        var defaults = new ShortcutDefaults();
        Assert.True(defaults.Save(Path_, out var success));
        Assert.Null(success);
        var original = File.ReadAllBytes(Path_);
        var blocked = Path.Combine(_folder, "blocked");
        Directory.CreateDirectory(blocked);
        Assert.False(defaults.Save(blocked, out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
        Assert.Equal(original, File.ReadAllBytes(Path_));
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.tmp"));
        Assert.False(defaults.Save(Path.Combine(Path_, "child.json")));
        Assert.Equal(original, File.ReadAllBytes(Path_));
    }

    [Fact]
    public void TheListCoversTheToolsAndTheMenus()
    {
        // The rows the port cannot do without: every tool letter, and the two the canvas answers to.
        var ids = Shortcuts.Definitions.Select(row => row.ID).ToHashSet();
        foreach (var title in new[]
                 {
                     "Hand tool", "Brush tool", "Clone Stamp", "Crop tool", "Gradient tool", "Magic wand",
                     "Temporary Hand tool (hold)", "Apply Canvas Operation", "Cancel Canvas Operation",
                 })
        {
            Assert.Contains($"{Shortcuts.Canvas}:{title}", ids);
        }
        Assert.Contains(AnyID("Undo"), ids);
        Assert.Contains(AnyID("Save"), ids);
    }
}
