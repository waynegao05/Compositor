using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Compositor.Core.Diagnostics;

/// <summary>
/// Bounded local diagnostics. Callers supply environment metadata and operational events;
/// do not include document content, command lines, or credentials. Exceptions can contain paths.
/// No network requests are made. File-system failures are reported without throwing.
/// </summary>
public sealed class DiagnosticLog
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex LogName = new(
        @"^compositor-[0-9]{8}T[0-9]{13}Z-[0-9a-f]{32}-[0-9]{6,10}\.log$",
        RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly string _session = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N");
    private readonly string _environment;
    private readonly int _maxFileBytes;
    private readonly int _maxFiles;
    private int _part;
    private string? _currentLogPath;

    public DiagnosticLog(string directory, string environmentSummary, int maxFileBytes = 262144, int maxFiles = 8)
    {
        DirectoryPath = directory;
        _maxFileBytes = Math.Clamp(maxFileBytes, 1024, 16 * 1024 * 1024);
        _maxFiles = Math.Clamp(maxFiles, 1, 100);
        _environment = Utf8.GetString(BoundedText(environmentSummary ?? string.Empty, 32768));
        try
        {
            DirectoryPath = Path.GetFullPath(directory);
            Write("Session started.");
        }
        catch (Exception exception) { Fail(exception); }
    }

    public string DirectoryPath { get; }
    public string? CurrentLogPath { get { lock (_gate) return _currentLogPath; } }
    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public bool Write(string message, Exception? exception = null)
    {
        lock (_gate)
        {
            try
            {
                var header = BoundedText($"Compositor session {_session}\n{_environment}\n", _maxFileBytes / 2);
                var details = exception is null ? string.Empty : "\n" + exception;
                var entry = BoundedText($"[{DateTime.UtcNow:O}] {message}{details}\n", _maxFileBytes - header.Length);
                Directory.CreateDirectory(DirectoryPath);
                TrimOldFiles(_maxFiles);
                if (_currentLogPath is null || !File.Exists(_currentLogPath)
                    || new FileInfo(_currentLogPath).Length + entry.Length > _maxFileBytes)
                {
                    // Remove only files using this logger's exact naming convention. Failure to
                    // free a slot prevents further rotation rather than growing without a bound.
                    TrimOldFiles(_maxFiles - 1);
                    var path = Path.Combine(DirectoryPath, $"compositor-{_session}-{_part++:D6}.log");
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        stream.Write(header);
                        stream.Write(entry);
                        stream.Flush(flushToDisk: true);
                    }
                    _currentLogPath = path;
                }
                else
                {
                    EnsureRegularFile(_currentLogPath);
                    using var stream = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                    stream.Seek(0, SeekOrigin.End);
                    stream.Write(entry);
                    stream.Flush(flushToDisk: true);
                }
                IsAvailable = true;
                LastError = null;
                return true;
            }
            catch (Exception failure)
            {
                IsAvailable = false;
                return Fail(failure);
            }
        }
    }

    /// <summary>
    /// Exports recent session logs matching the logger's exact schema in its configured directory,
    /// plus the supplied environment summary. Previous sessions are included for crash diagnosis.
    /// A failed export leaves an existing destination unchanged. The user chooses whether to share it.
    /// </summary>
    public bool Export(string destination, out string? error)
    {
        lock (_gate)
        {
            string? temporary = null;
            try
            {
                var fullPath = Path.GetFullPath(destination);
                if (LogName.IsMatch(Path.GetFileName(fullPath)))
                    throw new IOException("A diagnostic archive cannot replace a session log.");
                var parent = Path.GetDirectoryName(fullPath)!;
                Directory.CreateDirectory(parent);
                temporary = Path.Combine(parent, ".compositor-diagnostics-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var summary = archive.CreateEntry("environment.txt");
                        var metadata = IsAvailable ? _environment : _environment
                            + "\nSession logging unavailable: " + (LastError ?? "No session log could be created.");
                        using (var output = summary.Open()) output.Write(BoundedText(metadata, 32768));
                        foreach (var path in SessionLogFiles().TakeLast(_maxFiles))
                        {
                            EnsureRegularFile(path);
                            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (input.Length > _maxFileBytes)
                                throw new IOException("A session log exceeds its expected size.");
                            var entry = archive.CreateEntry("logs/" + Path.GetFileName(path), CompressionLevel.Optimal);
                            using var output = entry.Open();
                            CopyBounded(input, output, _maxFileBytes);
                        }
                    }
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, fullPath, overwrite: true);
                temporary = null;
                error = null;
                LastError = null;
                return true;
            }
            catch (Exception failure)
            {
                Fail(failure);
                error = LastError;
                return false;
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception) { /* Export failure must not hide the original error. */ }
                }
            }
        }
    }

    private void TrimOldFiles(int keep)
    {
        var files = SessionLogFiles();
        foreach (var path in files.Where(path => new FileInfo(path).Length > _maxFileBytes)) File.Delete(path);
        files = files.Where(File.Exists).ToArray();
        foreach (var path in files.Take(Math.Max(0, files.Length - keep))) File.Delete(path);
    }

    private string[] SessionLogFiles() => !Directory.Exists(DirectoryPath) ? [] :
        Directory.EnumerateFiles(DirectoryPath, "compositor-*.log")
            .Where(path => LogName.IsMatch(Path.GetFileName(path)))
            .Where(path => (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private static void EnsureRegularFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("A session log is no longer a regular file.");
    }

    private static void CopyBounded(Stream input, Stream output, int limit)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            if (read > limit) throw new IOException("A session log exceeds its expected size.");
            output.Write(buffer, 0, read);
            limit -= read;
        }
    }

    private static byte[] BoundedText(string text, int limit)
    {
        if (text.Length <= limit && Utf8.GetByteCount(text) <= limit) return Utf8.GetBytes(text);
        ReadOnlySpan<byte> suffix = "\n[truncated]\n"u8;
        var bytes = new byte[limit];
        Utf8.GetEncoder().Convert(text.AsSpan(), bytes.AsSpan(0, limit - suffix.Length), flush: true,
            out _, out var written, out _);
        suffix.CopyTo(bytes.AsSpan(written));
        return bytes.AsSpan(0, written + suffix.Length).ToArray();
    }

    private bool Fail(Exception exception)
    {
        LastError = exception.GetType().Name + ": " + exception.Message;
        return false;
    }
}
