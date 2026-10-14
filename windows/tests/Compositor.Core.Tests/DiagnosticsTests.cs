using System.IO.Compression;
using System.Text;
using Compositor.Core.Diagnostics;

namespace Compositor.Core.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "compositor-diagnostics-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SessionContainsMetadataAndExceptionDetails()
    {
        var log = new DiagnosticLog(_directory, "Version: test\nLanguage: zh-Hans");
        Assert.True(log.IsAvailable);
        Assert.True(log.Write("Save failed.", new IOException("磁盘空间不足")));
        var text = File.ReadAllText(log.CurrentLogPath!);
        Assert.Contains("Version: test", text);
        Assert.Contains("Language: zh-Hans", text);
        Assert.Contains("Save failed.", text);
        Assert.Contains("System.IO.IOException: 磁盘空间不足", text);
    }

    [Fact]
    public void BlockedDirectoryDoesNotThrowAndCanRecover()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "logs");
        File.WriteAllText(blocked, "Keep this file.");
        var log = new DiagnosticLog(blocked, "Version: test");
        Assert.False(log.IsAvailable);
        Assert.False(log.Write("Cannot write."));
        Assert.NotNull(log.LastError);
        Assert.Equal("Keep this file.", File.ReadAllText(blocked));
        File.Delete(blocked);
        Assert.True(log.Write("Recovered."));
        Assert.True(log.IsAvailable);
        Assert.Null(log.LastError);
        Assert.Contains("Recovered.", File.ReadAllText(log.CurrentLogPath!));
    }

    [Fact]
    public void InvalidPathDoesNotThrowFromConstructorOrWrites()
    {
        var log = new DiagnosticLog("invalid\0path", "Version: test");
        Assert.False(log.IsAvailable);
        Assert.False(log.Write("Cannot write."));
        Assert.NotNull(log.LastError);
        Assert.False(log.Export("invalid\0destination", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void RotationBoundsFileSizeAndRetainedSessionsWithoutDeletingOtherFiles()
    {
        Directory.CreateDirectory(_directory);
        var unrelated = Path.Combine(_directory, "compositor-not-a-real-session.log");
        File.WriteAllText(unrelated, "User-owned file.");
        for (var session = 0; session < 5; session++)
        {
            var log = new DiagnosticLog(_directory, "Version: test", maxFileBytes: 1024, maxFiles: 3);
            for (var entry = 0; entry < 4; entry++) Assert.True(log.Write(new string('x', 2000)));
            Assert.Contains("[truncated]", File.ReadAllText(log.CurrentLogPath!));
        }
        var logs = Directory.GetFiles(_directory, "*.log").Where(path => path != unrelated).ToArray();
        Assert.Equal(3, logs.Length);
        Assert.All(logs, path => Assert.InRange(new FileInfo(path).Length, 1, 1024));
        Assert.Equal("User-owned file.", File.ReadAllText(unrelated));
    }

    [Fact]
    public void OversizedChineseEntriesRemainValidUtf8()
    {
        var log = new DiagnosticLog(_directory, new string('中', 1000), maxFileBytes: 1024);
        Assert.True(log.Write(string.Concat(Enumerable.Repeat("中文😀", 2000))));
        var bytes = File.ReadAllBytes(log.CurrentLogPath!);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.InRange(bytes.Length, 1, 1024);
        Assert.Contains("[truncated]", text);
        Assert.DoesNotContain("\ufffd", text);
    }

    [Fact]
    public void ConcurrentWritesPreserveEveryEntry()
    {
        var log = new DiagnosticLog(_directory, "Version: test");
        var written = new bool[200];
        Parallel.For(0, written.Length, index => written[index] = log.Write($"event-{index:D3}"));
        Assert.All(written, Assert.True);
        var lines = File.ReadAllLines(log.CurrentLogPath!);
        for (var index = 0; index < written.Length; index++)
            Assert.Single(lines, line => line.EndsWith($" event-{index:D3}", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportIncludesPreviousAndCurrentSessionsWithSafeEntryNames()
    {
        var previous = new DiagnosticLog(_directory, "Previous session.");
        Assert.True(previous.Write("Previous failure for diagnosis."));
        var log = new DiagnosticLog(_directory, "Version: test\nLanguage: zh-Hans", maxFileBytes: 1024, maxFiles: 4);
        Assert.True(log.Write(new string('x', 2000)));
        Assert.True(log.Write("Export this session."));
        File.WriteAllText(Path.Combine(_directory, "private-document.txt"), "User content.");
        var destination = Path.Combine(_directory, "诊断报告.zip");
        Assert.True(log.Export(destination, out var error), error);
        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains(archive.Entries, entry => entry.FullName == "environment.txt");
        Assert.All(archive.Entries, entry => Assert.False(entry.FullName.Contains("..", StringComparison.Ordinal)));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("private-document", StringComparison.Ordinal));
        var text = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.Contains("Language: zh-Hans", text);
        Assert.Contains("Export this session.", text);
        Assert.Contains("Previous session.", text);
        Assert.Contains("Previous failure for diagnosis.", text);
        Assert.DoesNotContain("User content.", text);
    }

    [Fact]
    public void FailedExportPreservesExistingArchiveAndCleansTemporaryFile()
    {
        var log = new DiagnosticLog(_directory, "Version: test", maxFileBytes: 1024);
        var destination = Path.Combine(_directory, "diagnostics.zip");
        File.WriteAllText(destination, "Existing archive must survive.");
        // Simulate an externally modified log. The exporter refuses unbounded content.
        File.WriteAllText(log.CurrentLogPath!, new string('x', 4096));
        Assert.False(log.Export(destination, out var error));
        Assert.NotNull(error);
        Assert.Equal("Existing archive must survive.", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".compositor-diagnostics-*.tmp"));
    }

    [Fact]
    public void ExportFailureCannotOverwriteALogOrAnExistingDirectory()
    {
        var log = new DiagnosticLog(_directory, "Version: test");
        var before = File.ReadAllBytes(log.CurrentLogPath!);
        Assert.False(log.Export(log.CurrentLogPath!, out _));
        Assert.Equal(before, File.ReadAllBytes(log.CurrentLogPath!));
        var destination = Path.Combine(_directory, "existing-directory");
        Directory.CreateDirectory(destination);
        var protectedFile = Path.Combine(destination, "keep.txt");
        File.WriteAllText(protectedFile, "Keep.");
        Assert.False(log.Export(destination, out _));
        Assert.Equal("Keep.", File.ReadAllText(protectedFile));
        Assert.Empty(Directory.GetFiles(_directory, ".compositor-diagnostics-*.tmp"));
    }

    [Fact]
    public void WritesRemoveOversizedSessionFilesBeforeContinuing()
    {
        var previous = new DiagnosticLog(_directory, "Previous session.", maxFileBytes: 1024, maxFiles: 3);
        var oversized = previous.CurrentLogPath!;
        File.WriteAllText(oversized, new string('x', 4096));
        var current = new DiagnosticLog(_directory, "Current session.", maxFileBytes: 1024, maxFiles: 3);
        Assert.True(current.Write("Recovered from an oversized prior log."));
        Assert.False(File.Exists(oversized));
        Assert.All(Directory.GetFiles(_directory, "*.log"), path => Assert.InRange(new FileInfo(path).Length, 1, 1024));
    }

    [Fact]
    public void ExportToBlockedParentLeavesItsContentsUnchanged()
    {
        var log = new DiagnosticLog(_directory, "Version: test");
        var blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "Keep.");
        Assert.False(log.Export(Path.Combine(blocked, "diagnostics.zip"), out var error));
        Assert.NotNull(error);
        Assert.Equal("Keep.", File.ReadAllText(blocked));
    }

    [Fact]
    public void ExportExcludesUnrelatedLogsAndDirectories()
    {
        var log = new DiagnosticLog(_directory, "Version: test");
        File.WriteAllText(Path.Combine(_directory, "compositor-manual.log"), "User private note.");
        var directoryName = "compositor-20000101T0000000000000Z-00000000000000000000000000000000-000000.log";
        Directory.CreateDirectory(Path.Combine(_directory, directoryName));
        File.WriteAllText(Path.Combine(_directory, directoryName, "private.txt"), "Private nested content.");
        var destination = Path.Combine(_directory, "diagnostics.zip");
        Assert.True(log.Export(destination, out var error), error);
        using var archive = ZipFile.OpenRead(destination);
        Assert.Equal(2, archive.Entries.Count);
        var text = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain("User private note.", text);
        Assert.DoesNotContain("Private nested content.", text);
    }

    [Fact]
    public void ExportCanReplaceAnExistingArchiveAfterSuccess()
    {
        var log = new DiagnosticLog(_directory, "Version: test");
        var destination = Path.Combine(_directory, "diagnostics.zip");
        File.WriteAllText(destination, "Old archive.");
        Assert.True(log.Write("New archive."));
        Assert.True(log.Export(destination, out var error), error);
        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains("New archive.", string.Join("\n", archive.Entries.Select(ReadEntry)));
    }

    [Fact]
    public void EnvironmentCanBeExportedWhenSessionLoggingIsUnavailable()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "Cannot create a log directory here.");
        var log = new DiagnosticLog(blocked, "Version: test");
        Assert.False(log.IsAvailable);
        Assert.True(log.Export(Path.Combine(_directory, "environment-only.zip"), out var error), error);
        using var archive = ZipFile.OpenRead(Path.Combine(_directory, "environment-only.zip"));
        Assert.Single(archive.Entries);
        var summary = ReadEntry(archive.GetEntry("environment.txt")!);
        Assert.Contains("Version: test", summary);
        Assert.Contains("Session logging unavailable:", summary);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
