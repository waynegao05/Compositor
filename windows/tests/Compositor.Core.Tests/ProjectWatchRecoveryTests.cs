using Compositor.Core.IO;

namespace Compositor.Core.Tests;

public sealed class ProjectWatchRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "compositor-watch-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public ProjectWatchRecoveryTests()
    {
        _project = Path.Combine(_directory, "中文工程.comp");
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_project, "manifest.json"), "original manifest");
    }

    [Fact]
    public void RenameGapIsNotAChangeAndDoesNotReplaceTheBaseline()
    {
        var watch = ProjectWatch.For(_project)!;
        var renamed = _project + ".moving";
        Directory.Move(_project, renamed);
        Assert.False(watch.Changed());
        Assert.False(watch.Changed());
        Directory.Move(renamed, _project);
        Assert.False(watch.Changed());
        File.WriteAllText(Path.Combine(_project, "manifest.json"), "a genuinely different manifest");
        Assert.True(watch.Changed());
        Assert.False(watch.Changed());
    }

    [Fact]
    public void RememberDuringRenameGapKeepsTheLastReadableBaseline()
    {
        var watch = ProjectWatch.For(_project)!;
        var renamed = _project + ".moving";
        Directory.Move(_project, renamed);
        watch.Remember();
        Directory.Move(renamed, _project);
        Assert.False(watch.Changed());
    }

    [Fact]
    public void AChangeMadeDuringRenameGapIsReportedAfterThePackageReturns()
    {
        var watch = ProjectWatch.For(_project)!;
        var renamed = _project + ".moving";
        Directory.Move(_project, renamed);
        Assert.False(watch.Changed());
        File.WriteAllText(Path.Combine(renamed, "manifest.json"), "external editor's replacement manifest");
        Directory.Move(renamed, _project);
        Assert.True(watch.Changed());
        Assert.False(watch.Changed());
    }

    [Fact]
    public void FailedLoadCanRetryWithoutAnotherMetadataChange()
    {
        var watch = ProjectWatch.For(_project)!;
        File.WriteAllText(Path.Combine(_project, "manifest.json"), "external replacement manifest");
        Assert.True(watch.Changed());
        // Loading may have failed because a writer still held the file. Unlocking it does not
        // necessarily change its length or write time, so the caller explicitly requests another try.
        watch.Retry();
        Assert.True(watch.Changed());
        Assert.False(watch.Changed());
    }

    [Fact]
    public void FailedScanAndRememberCannotConsumeAPendingRetry()
    {
        var watch = ProjectWatch.For(_project)!;
        watch.Retry();
        var renamed = _project + ".moving";
        Directory.Move(_project, renamed);
        Assert.False(watch.Changed());
        watch.Remember();
        Directory.Move(renamed, _project);
        Assert.True(watch.Changed());
        Assert.False(watch.Changed());
    }

    [Fact]
    public void SuccessfulRememberAcknowledgesAPendingRetry()
    {
        var watch = ProjectWatch.For(_project)!;
        watch.Retry();
        watch.Remember();
        Assert.False(watch.Changed());
    }

    [ProjectWatchLinkFact]
    public void LinkedSubdirectoriesAreNotScanned()
    {
        var outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "private.txt");
        File.WriteAllText(outsideFile, "outside content");
        var link = Path.Combine(_project, "linked-directory");
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            var watch = ProjectWatch.For(_project)!;
            File.WriteAllText(outsideFile, "changed outside content must not enter the project fingerprint");
            Assert.False(watch.Changed());
        }
        finally { Directory.Delete(link); }
    }

    [ProjectWatchLinkFact]
    public void ALinkedRootCannotReplaceTheBaselineOrFollowExternalChanges()
    {
        var watch = ProjectWatch.For(_project)!;
        var moved = _project + ".moving";
        Directory.Move(_project, moved);
        Directory.CreateSymbolicLink(_project, moved);
        try
        {
            File.WriteAllText(Path.Combine(moved, "manifest.json"), "external replacement manifest");
            Assert.False(watch.Changed());
            watch.Remember();
        }
        finally { Directory.Delete(_project); }
        Directory.Move(moved, _project);
        Assert.True(watch.Changed());
        Assert.False(watch.Changed());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>Skip link-specific checks when the test account lacks permission to create symbolic links.</summary>
public sealed class ProjectWatchLinkFactAttribute : FactAttribute
{
    public ProjectWatchLinkFactAttribute()
    {
        var root = Path.Combine(Path.GetTempPath(), "compositor-link-capability-" + Guid.NewGuid().ToString("N"));
        var link = root + ".link";
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(link, root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip = "This test account cannot create directory symbolic links: " + error.GetType().Name;
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
