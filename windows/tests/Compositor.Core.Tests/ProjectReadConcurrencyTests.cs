using Compositor.Core.IO;
using SkiaSharp;

namespace Compositor.Core.Tests;

public sealed class ProjectReadConcurrencyTests : ProjectTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReaderWaitsUntilTheEntireReplacementIsInstalled(bool trailingSeparator)
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var path = PathIn("一致性.comp");
        using var project = KitchenSink();
        ProjectStore.Save(project, path);
        project.Manifest.Layers[0].Name = "replacement";
        using var moved = new ManualResetEvent(false);
        using var release = new ManualResetEvent(false);
        using var reading = new ManualResetEvent(false);
        Exception? writerFailure = null;
        Exception? readerFailure = null;
        ProjectSnapshot? loaded = null;
        var readerStarted = false;
        var writer = new Thread(() =>
        {
            try
            {
                ProjectStore.SaveWithCheckpoints(project, path, stage =>
                {
                    if (stage != ProjectSaveCheckpoint.PreviousMoved) return;
                    moved.Set();
                    if (!release.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                });
            }
            catch (Exception error) { writerFailure = error; }
        }) { IsBackground = true };
        var reader = new Thread(() =>
        {
            reading.Set();
            try { loaded = ProjectStore.Load(path + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : "")); }
            catch (Exception error) { readerFailure = error; }
        }) { IsBackground = true };
        writer.Start();
        try
        {
            Assert.True(moved.WaitOne(TimeSpan.FromSeconds(10)));
            reader.Start();
            readerStarted = true;
            Assert.True(reading.WaitOne(TimeSpan.FromSeconds(3)));
            Assert.False(reader.Join(TimeSpan.FromMilliseconds(100)), "A reader escaped the writer's package lock.");
        }
        finally
        {
            release.Set();
            Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
            if (readerStarted) Assert.True(reader.Join(TimeSpan.FromSeconds(10)));
        }
        using (loaded)
        {
            Assert.Null(writerFailure);
            Assert.Null(readerFailure);
            Assert.NotNull(loaded);
            Assert.Equal("replacement", loaded.Manifest.Layers[0].Name);
        }
    }

    [Fact]
    public void AlternatingSavesNeverMixManifestAndPixelVersions()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var path = PathIn("Alternating.comp");
        using var a = KitchenSink();
        // Use the same layer and asset names in both versions, so a successful mixed read is detectable.
        foreach (var record in a.Manifest.Layers) record.Name = "A";
        foreach (var asset in a.Images.Values) asset.Image.Erase(SKColors.Red);
        var bytes = Compositor.Core.Format.ManifestJson.Serialize(a.Manifest);
        var bManifest = Compositor.Core.Format.ManifestJson.Deserialize(bytes);
        foreach (var record in bManifest.Layers) record.Name = "B";
        using var replacement = new ProjectSnapshot(bManifest);
        foreach (var (id, asset) in a.Images) replacement.Images[id] = Asset(asset.Width, asset.Height, SKColors.Blue);
        foreach (var (id, asset) in a.Masks) replacement.Masks[id] = MaskAsset(asset.Width, asset.Height);
        ProjectStore.Save(a, path);
        Exception? writerFailure = null;
        var writer = new Thread(() =>
        {
            try { for (var i = 0; i < 12; i++) ProjectStore.Save(i % 2 == 0 ? replacement : a, path); }
            catch (Exception error) { writerFailure = error; }
        }) { IsBackground = true };
        writer.Start();
        try
        {
            for (var i = 0; i < 24; i++)
            {
                using var loaded = ProjectStore.Load(path);
                var version = loaded.Manifest.Layers[0].Name;
                Assert.All(loaded.Manifest.Layers, layer => Assert.Equal(version, layer.Name));
                var expected = version == "A" ? SKColors.Red : SKColors.Blue;
                Assert.All(loaded.Images.Values, asset => Assert.Equal(expected, asset.Image.GetPixel(0, 0)));
            }
        }
        finally { Assert.True(writer.Join(TimeSpan.FromSeconds(20))); }
        Assert.Null(writerFailure);
    }

    [Fact]
    public void RecoverySkipsABusyTargetWithoutHoldingGateOrConsumingItsJournal()
    {
        using var profile = AppDataPaths.UseTemporaryProfile();
        var path = PathIn("Busy.comp");
        using var project = KitchenSink();
        Assert.Throws<ProjectSaveInterruptionException>(() => ProjectStore.SaveWithCheckpoints(project, path, stage =>
        {
            if (stage == ProjectSaveCheckpoint.Journaled) throw new ProjectSaveInterruptionException();
        }));
        var candidate = Assert.Single(ProjectRecovery.FindAvailable(path));
        using var locked = new ManualResetEvent(false);
        using var release = new ManualResetEvent(false);
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var operation = ProjectRecovery.AcquireOperationLocks(Guid.NewGuid(), path);
                locked.Set();
                release.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            Assert.True(locked.WaitOne(TimeSpan.FromSeconds(3)));
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(ProjectRecovery.FindAvailable(path));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), "Recovery waited for the busy target.");
            ProjectStore.Save(project, PathIn("Independent.comp"));
        }
        finally { release.Set(); Assert.True(worker.Join(TimeSpan.FromSeconds(3))); }
        Assert.Null(failure);
        Assert.Equal(candidate, Assert.Single(ProjectRecovery.FindAvailable(path)));
    }
}
