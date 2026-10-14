using System.Text.Json.Nodes;
using Compositor.Core.IO;

namespace Compositor.Core.Tests;

public sealed class ProjectRecoveryTests : ProjectTestBase
{
    [Theory]
    [InlineData(0, 0, true, "original")]
    [InlineData(1, 1, true, "original")]
    [InlineData(2, 2, false, "")]
    [InlineData(3, 1, true, "new edits")]
    public void EachInterruptionPointLeavesOnlyValidatedCandidates(int point, int count, bool targetExists, string targetName)
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var target = PathIn("工程.comp");
        using var project = KitchenSink();
        project.Manifest.Layers[0].Name = "original";
        ProjectStore.Save(project, target);
        project.Manifest.Layers[0].Name = "new edits";
        Interrupt(project, target, (ProjectSaveCheckpoint)point);
        Assert.Equal(targetExists, Directory.Exists(target));
        if (targetExists) AssertName(target, targetName);
        var candidates = ProjectRecovery.FindAvailable();
        Assert.Equal(count, candidates.Count);
        foreach (var candidate in candidates)
        {
            Assert.Equal(target, candidate.TargetPath);
            Assert.Equal(project.Manifest.DocumentID, candidate.DocumentID);
            Assert.Equal(project.Manifest.Width, candidate.Width);
            Assert.Equal(project.Manifest.Height, candidate.Height);
            var sourceBefore = Contents(candidate.SourcePath);
            var destination = PathIn("恢复 " + candidate.Kind + ".comp");
            Assert.Equal(destination, ProjectRecovery.RestoreCopy(candidate, destination));
            AssertName(destination, candidate.Kind == ProjectRecoveryKind.PendingSave ? "new edits" : "original");
            AssertContents(candidate.SourcePath, sourceBefore);
            Assert.Throws<InvalidOperationException>(() => ProjectRecovery.RestoreCopy(candidate, PathIn("duplicate.comp")));
            Assert.False(Directory.Exists(PathIn("duplicate.comp")));
        }
        Assert.Empty(ProjectRecovery.FindAvailable());
        Assert.Equal(targetExists, Directory.Exists(target));
        if (targetExists) AssertName(target, targetName);
    }

    [Fact]
    public void ACompletedSaveClearsOnlyItsOwnTransaction()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var target = PathIn("First.comp");
        using var project = KitchenSink();
        Interrupt(project, target, ProjectSaveCheckpoint.Journaled);
        var first = Assert.Single(ProjectRecovery.FindAvailable());
        var record = Path.Combine(ProjectRecovery.RegistryPath, first.TransactionID.ToString("N") + ".json");
        var recordBytes = File.ReadAllBytes(record);
        var other = PathIn("Other.comp");
        ProjectStore.Save(project, other);
        project.Manifest.Layers[0].Name = "updated";
        ProjectStore.Save(project, other);
        AssertName(other, "updated");
        Assert.Equal(recordBytes, File.ReadAllBytes(record));
        Assert.Single(Directory.GetFiles(ProjectRecovery.RegistryPath, "*.json"));
        Assert.Equal(first.TransactionID, Assert.Single(ProjectRecovery.FindAvailable()).TransactionID);
        Assert.Empty(Directory.GetDirectories(Root, ".Other.comp.*"));
    }

    [Fact]
    public void ANewerTargetIsNeverOverwrittenByRecovery()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var target = PathIn("Current.comp");
        using var project = KitchenSink();
        project.Manifest.Layers[0].Name = "old";
        ProjectStore.Save(project, target);
        project.Manifest.Layers[0].Name = "interrupted";
        Interrupt(project, target, ProjectSaveCheckpoint.PreviousMoved);
        var oldCandidate = ProjectRecovery.FindAvailable(target).Single(candidate => candidate.Kind == ProjectRecoveryKind.PreviousSave);
        project.Manifest.Layers[0].Name = "newer work";
        ProjectStore.Save(project, target);
        var before = Contents(target);
        Assert.Throws<IOException>(() => ProjectRecovery.RestoreCopy(oldCandidate, target));
        AssertContents(target, before);
        var restored = ProjectRecovery.RestoreCopy(oldCandidate, PathIn("RecoveredOld.comp"));
        AssertName(restored, "old");
        AssertContents(target, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExistingRecoveryDestinationIsPreserved(bool directory)
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        using var project = KitchenSink();
        Interrupt(project, PathIn("Pending.comp"), ProjectSaveCheckpoint.Journaled);
        var candidate = Assert.Single(ProjectRecovery.FindAvailable());
        var destination = PathIn("occupied.comp");
        if (directory) Directory.CreateDirectory(destination);
        else File.WriteAllText(destination, "unrelated file");
        Assert.Throws<IOException>(() => ProjectRecovery.RestoreCopy(candidate, destination));
        Assert.Equal(directory, Directory.Exists(destination));
        if (!directory) Assert.Equal("unrelated file", File.ReadAllText(destination));
        Assert.Single(ProjectRecovery.FindAvailable());
    }

    [Fact]
    public void AChangedCandidateIsNotOfferedOrCopied()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        using var project = KitchenSink();
        Interrupt(project, PathIn("Pending.comp"), ProjectSaveCheckpoint.Journaled);
        var candidate = Assert.Single(ProjectRecovery.FindAvailable());
        File.WriteAllText(Path.Combine(candidate.SourcePath, "unrelated.txt"), "retain this file");
        var before = Contents(candidate.SourcePath);
        Assert.Empty(ProjectRecovery.FindAvailable());
        Assert.Throws<InvalidOperationException>(() => ProjectRecovery.RestoreCopy(candidate, PathIn("Copy.comp")));
        AssertContents(candidate.SourcePath, before);
        Assert.False(Directory.Exists(PathIn("Copy.comp")));
    }

    [Fact]
    public void AFailedReplacementPreservesAnInterveningTargetAndBothCandidates()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var target = PathIn("Intervening.comp");
        using var project = KitchenSink();
        ProjectStore.Save(project, target);
        Assert.ThrowsAny<IOException>(() => ProjectStore.SaveWithCheckpoints(project, target, stage =>
        {
            if (stage == ProjectSaveCheckpoint.PreviousMoved) File.WriteAllText(target, "another program created this");
        }));
        Assert.Equal("another program created this", File.ReadAllText(target));
        Assert.Equal(2, ProjectRecovery.FindAvailable().Count);
    }

    [Fact]
    public void ATargetUpdatedAfterRegistrationIsNotReplacedByTheOlderSave()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var target = PathIn("Changed.comp");
        using var project = KitchenSink();
        ProjectStore.Save(project, target);
        using var newer = KitchenSink();
        newer.Manifest.Layers[0].Name = "newer external save";
        Assert.Throws<IOException>(() => ProjectStore.SaveWithCheckpoints(project, target, stage =>
        {
            if (stage == ProjectSaveCheckpoint.Journaled) ProjectStore.Save(newer, target);
        }));
        AssertName(target, "newer external save");
        Assert.Equal(ProjectRecoveryKind.PendingSave, Assert.Single(ProjectRecovery.FindAvailable()).Kind);
    }

    [Fact]
    public void AnOrdinarySaveFailureRollsBackOnlyWhenTheTargetIsStillAbsent()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var target = PathIn("Rollback.comp");
        using var project = KitchenSink();
        project.Manifest.Layers[0].Name = "original";
        ProjectStore.Save(project, target);
        project.Manifest.Layers[0].Name = "pending";
        Assert.Throws<IOException>(() => ProjectStore.SaveWithCheckpoints(project, target, stage =>
        {
            if (stage == ProjectSaveCheckpoint.PreviousMoved) throw new IOException("injected failure");
        }));
        AssertName(target, "original");
        var pending = Assert.Single(ProjectRecovery.FindAvailable());
        Assert.Equal(ProjectRecoveryKind.PendingSave, pending.Kind);
        AssertName(pending.SourcePath, "pending");
    }

    [Fact]
    public void AnUnwritableRegistryPreventsTheDirectorySwap()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var target = PathIn("Safe.comp");
        using var project = KitchenSink();
        ProjectStore.Save(project, target);
        var before = Contents(target);
        Directory.Delete(ProjectRecovery.RegistryPath);
        File.WriteAllText(ProjectRecovery.RegistryPath, "occupied registry");
        Assert.ThrowsAny<IOException>(() => ProjectStore.Save(project, target));
        AssertContents(target, before);
        Assert.Empty(Directory.GetDirectories(Root, ".*"));
    }

    [Theory]
    [InlineData("mismatched id")]
    [InlineData("relative target")]
    [InlineData("noncanonical target")]
    [InlineData("bad digest")]
    [InlineData("oversized record")]
    public void ForgedOrMalformedRecordsCannotPointRecoveryElsewhere(string problem)
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        using var project = KitchenSink();
        Interrupt(project, PathIn("Pending.comp"), ProjectSaveCheckpoint.Journaled);
        var candidate = Assert.Single(ProjectRecovery.FindAvailable());
        var record = Path.Combine(ProjectRecovery.RegistryPath, candidate.TransactionID.ToString("N") + ".json");
        var json = JsonNode.Parse(File.ReadAllText(record))!.AsObject();
        switch (problem)
        {
            case "mismatched id": json["transactionID"] = Guid.NewGuid(); break;
            case "relative target": json["targetPath"] = "../elsewhere.comp"; break;
            case "noncanonical target": json["targetPath"] = Path.Combine(Root, "child", "..", "Pending.comp"); break;
            case "bad digest": json["pendingHash"] = "not a digest"; break;
        }
        File.WriteAllText(record, problem == "oversized record" ? new string('x', 65 * 1024) : json.ToJsonString());
        Assert.Empty(ProjectRecovery.FindAvailable());
        Assert.Throws<InvalidDataException>(() => ProjectRecovery.RestoreCopy(candidate, PathIn("Copy.comp")));
        Assert.False(Directory.Exists(PathIn("Copy.comp")));
        Assert.True(Directory.Exists(candidate.SourcePath));
    }

    [Fact]
    public void ADisplayCandidateCannotSubstituteAnotherSourcePath()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        using var project = KitchenSink();
        Interrupt(project, PathIn("Pending.comp"), ProjectSaveCheckpoint.Journaled);
        var candidate = Assert.Single(ProjectRecovery.FindAvailable());
        Assert.Throws<InvalidOperationException>(() => ProjectRecovery.RestoreCopy(
            candidate with { SourcePath = PathIn("unrelated") }, PathIn("Copy.comp")));
        Assert.False(Directory.Exists(PathIn("Copy.comp")));
        Assert.Single(ProjectRecovery.FindAvailable());
    }

    [Fact]
    public void LinkedCandidateDirectoriesAreNotFollowed()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        using var project = KitchenSink();
        Interrupt(project, PathIn("Pending.comp"), ProjectSaveCheckpoint.Journaled);
        var candidate = Assert.Single(ProjectRecovery.FindAvailable());
        var outside = PathIn("Outside.comp");
        Directory.Move(candidate.SourcePath, outside);
        Directory.CreateSymbolicLink(candidate.SourcePath, outside);
        try
        {
            var before = Contents(outside);
            Assert.Empty(ProjectRecovery.FindAvailable());
            Assert.Throws<InvalidDataException>(() => ProjectRecovery.RestoreCopy(candidate, PathIn("Copy.comp")));
            AssertContents(outside, before);
        }
        finally { Directory.Delete(candidate.SourcePath); }
    }

    [Fact]
    public void RecoveryCannotWriteInsideTheCandidateOrOriginalPackage()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        using var project = KitchenSink();
        var target = PathIn("Original.comp");
        ProjectStore.Save(project, target);
        Interrupt(project, target, ProjectSaveCheckpoint.Journaled);
        var candidate = Assert.Single(ProjectRecovery.FindAvailable());
        Assert.Throws<IOException>(() => ProjectRecovery.RestoreCopy(candidate, Path.Combine(candidate.SourcePath, "copy.comp")));
        Assert.Throws<IOException>(() => ProjectRecovery.RestoreCopy(candidate, Path.Combine(target, "copy.comp")));
        Assert.Single(ProjectRecovery.FindAvailable());
    }

    [Fact]
    public void OldInvalidRecordsDoNotHideTheNewestInterruptedSave()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        Directory.CreateDirectory(ProjectRecovery.RegistryPath);
        for (var index = 0; index < 300; index++)
        {
            var old = Path.Combine(ProjectRecovery.RegistryPath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(old, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(-1));
        }
        using var project = KitchenSink();
        var target = PathIn("Recent.comp");
        Interrupt(project, target, ProjectSaveCheckpoint.Journaled);
        Assert.Equal(target, Assert.Single(ProjectRecovery.FindAvailable()).TargetPath);
    }

    [Fact]
    public void AWriterTimeoutReleasesItsPartiallyAcquiredPathLocks()
    {
        var first = PathIn("alpha.comp");
        var second = PathIn("omega.comp");
        using var blocker = ProjectRecovery.AcquireOperationLocks(Guid.NewGuid(), second);
        using var ready = new ManualResetEvent(false);
        using var finish = new ManualResetEvent(false);
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var operation = ProjectRecovery.AcquireOperationLocks(Guid.NewGuid(), first, second);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                ready.Set();
                // Keep this thread alive until the assertion so abandoned ownership cannot hide a leak.
                finish.WaitOne(TimeSpan.FromSeconds(10));
            }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(8)), "The competing writer did not reach its bounded timeout.");
            Assert.IsType<IOException>(failure);
            using var available = ProjectRecovery.AcquireOperationLocks(Guid.NewGuid(), first);
        }
        finally
        {
            finish.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(3)), "The lock-check thread did not finish.");
        }
    }

    [Fact]
    public void ASharedTransactionSerializesConsumptionAcrossDifferentDestinationPaths()
    {
        var transaction = Guid.NewGuid();
        using var blocker = ProjectRecovery.AcquireOperationLocks(transaction, PathIn("one.comp"));
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var operation = ProjectRecovery.AcquireOperationLocks(transaction, PathIn("two.comp"));
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(8)), "The shared transaction did not enforce a bounded wait.");
        Assert.IsType<IOException>(failure);
    }

    private static void Interrupt(ProjectSnapshot project, string target, ProjectSaveCheckpoint at) =>
        Assert.Throws<ProjectSaveInterruptionException>(() => ProjectStore.SaveWithCheckpoints(project, target, stage =>
        {
            if (stage == at) throw new ProjectSaveInterruptionException();
        }));

    private static void AssertName(string path, string expected)
    {
        using var restored = ProjectStore.Load(path);
        Assert.Equal(expected, restored.Manifest.Layers[0].Name);
    }

    private static Dictionary<string, byte[]> Contents(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(path, file), File.ReadAllBytes);

    private static void AssertContents(string path, Dictionary<string, byte[]> expected)
    {
        var actual = Contents(path);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (file, bytes) in expected) Assert.Equal(bytes, actual[file]);
    }
}
