using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("Compositor.Core.Tests")]

namespace Compositor.Core.IO;

public enum ProjectRecoveryKind { PendingSave, PreviousSave }

public sealed record ProjectRecoveryCandidate(Guid TransactionID, string TargetPath, string SourcePath,
    DateTimeOffset CreatedUtc, ProjectRecoveryKind Kind, Guid DocumentID, int Width, int Height);

/// <summary>Records directory replacements and recovers verified copies without overwriting any project.</summary>
public static class ProjectRecovery
{
    private const int MaximumRecords = 256;
    private const int MaximumRecordBytes = 64 * 1024;
    private const int MaximumPackageEntries = 30_000;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string RegistryPath => Path.Combine(AppDataPaths.Root, "Recovery");

    internal sealed record Transaction
    {
        public int Version { get; init; } = 1;
        public Guid TransactionID { get; init; }
        public string TargetPath { get; init; } = "";
        public DateTimeOffset CreatedUtc { get; init; }
        public string PendingHash { get; init; } = "";
        public string? PreviousHash { get; init; }
        public bool PendingRecovered { get; init; }
        public bool PreviousRecovered { get; init; }
    }

    private sealed record Package(string Hash, Guid DocumentID, int Width, int Height);

    /// <summary>Only valid, unconsumed candidates are returned. A damaged record never blocks startup.</summary>
    public static IReadOnlyList<ProjectRecoveryCandidate> FindAvailable(string? targetPath = null)
    {
        var found = new List<ProjectRecoveryCandidate>();
        try
        {
            var filter = targetPath is null ? null : Canonical(targetPath);
            EnsureNoLinks(RegistryPath);
            if (!Directory.Exists(RegistryPath)) return found;
            var records = Directory.EnumerateFiles(RegistryPath, "*.json")
                .Select(path => (Path: path, Written: RecordWriteTime(path)))
                .Where(record => record.Written is not null)
                .OrderByDescending(record => record.Written).Take(MaximumRecords).ToArray();
            foreach (var record in records)
            {
                try
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(record.Path), "N", out var id)) continue;
                    // Journals are replaced atomically. Discover the target without Gate, then keep the
                    // writer's lock order. An active save is skipped and can be found on the next scan.
                    var discovered = Read(id);
                    if (filter is not null && !string.Equals(filter, discovered.TargetPath, PathComparison)) continue;
                    using var operation = TryAcquireOperationLocks(id, discovered.TargetPath);
                    if (operation is null) continue;
                    lock (Gate)
                    {
                        var transaction = Read(id);
                        if (!string.Equals(discovered.TargetPath, transaction.TargetPath, PathComparison)) continue;
                        foreach (var kind in Enum.GetValues<ProjectRecoveryKind>())
                        {
                            try
                            {
                                if (Consumed(transaction, kind) || ExpectedHash(transaction, kind) is not { } expected) continue;
                                var source = SourcePath(transaction, kind);
                                if (!Directory.Exists(source)) continue;
                                var package = Inspect(source);
                                if (package.Hash != expected) continue;
                                found.Add(new(id, transaction.TargetPath, source, transaction.CreatedUtc, kind,
                                    package.DocumentID, package.Width, package.Height));
                            }
                            catch (Exception error) when (IsUnreadable(error)) { }
                        }
                    }
                }
                catch (Exception error) when (IsUnreadable(error)) { }
            }
        }
        catch (Exception error) when (IsUnreadable(error)) { }
        return found.OrderByDescending(candidate => candidate.CreatedUtc).ToArray();
    }

    /// <summary>Revalidates the recorded candidate, creates a new package, then marks that copy consumed.</summary>
    public static string RestoreCopy(ProjectRecoveryCandidate candidate, string newPath)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var destination = Canonical(newPath);
        using var operation = AcquireOperationLocks(candidate.TransactionID, candidate.TargetPath, destination);
        lock (Gate)
        {
            Transaction transaction;
            try { transaction = Read(candidate.TransactionID); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new InvalidOperationException("This recovery candidate has already been restored or is unavailable.");
            }
            if (!Enum.IsDefined(candidate.Kind) || Consumed(transaction, candidate.Kind)
                || ExpectedHash(transaction, candidate.Kind) is not { } expected)
                throw new InvalidOperationException("This recovery candidate has already been restored or is unavailable.");
            var source = SourcePath(transaction, candidate.Kind);
            // Candidate objects are display snapshots. The journal and its derived paths remain authoritative.
            if (!string.Equals(candidate.TargetPath, transaction.TargetPath, PathComparison)
                || !string.Equals(candidate.SourcePath, source, PathComparison))
                throw new InvalidOperationException("The recovery candidate no longer matches its saved record.");
            if (Inspect(source).Hash != expected)
                throw new InvalidOperationException("The recovery candidate has changed or is damaged.");
            EnsureNoLinks(destination);
            if (Exists(destination) || Within(destination, transaction.TargetPath)
                || Within(destination, SourcePath(transaction, ProjectRecoveryKind.PendingSave))
                || Within(destination, SourcePath(transaction, ProjectRecoveryKind.PreviousSave))
                || Within(destination, Canonical(RegistryPath)))
                throw new IOException("Recovery requires a new folder outside the original project and its recovery copies.");
            var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Choose a project folder, not a drive root.");
            var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.restore");
            var installed = false;
            try
            {
                EnsureNoLinks(parent);
                Directory.CreateDirectory(staging);
                CopyPackage(source, staging);
                if (Inspect(staging).Hash != expected || Inspect(source).Hash != expected)
                    throw new IOException("The recovery candidate changed while it was being copied.");
                EnsureNoLinks(destination);
                if (Exists(destination)) throw new IOException("The recovery destination already exists.");
                Directory.Move(staging, destination);
                installed = true;
                // Inspect loads the entire package, including PNG assets, before consumption is persisted.
                if (Inspect(destination).Hash != expected) throw new IOException("The restored project did not verify.");
                var consumed = transaction with
                {
                    PendingRecovered = transaction.PendingRecovered || candidate.Kind == ProjectRecoveryKind.PendingSave,
                    PreviousRecovered = transaction.PreviousRecovered || candidate.Kind == ProjectRecoveryKind.PreviousSave,
                };
                Write(consumed, replacing: true);
                RetireConsumed(consumed);
                return destination;
            }
            catch
            {
                if (installed) DeleteOwnedPackage(destination, expected);
                throw;
            }
            finally
            {
                DeletePrivateStaging(staging);
            }
        }
    }

    internal static string SidecarPath(string target, Guid id, ProjectRecoveryKind kind)
    {
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("A project requires a parent directory.");
        return Path.Combine(parent, $".{Path.GetFileName(target)}.{id:N}.{(kind == ProjectRecoveryKind.PendingSave ? "tmp" : "old")}");
    }

    /// <summary>Readers hold the same target path lock for the manifest and every image.</summary>
    internal static IDisposable AcquireReadLock(string target) => new OperationLocks(PathLockNames([target]));

    /// <summary>Every writer takes path locks in ordinal order, then its transaction lock, before Gate.</summary>
    internal static IDisposable AcquireOperationLocks(Guid transactionID, params string[] targets) =>
        new OperationLocks(OperationLockNames(transactionID, targets));

    private static IDisposable? TryAcquireOperationLocks(Guid transactionID, params string[] targets)
    {
        try { return new OperationLocks(OperationLockNames(transactionID, targets), timeoutMilliseconds: 0); }
        catch (IOException) { return null; }
    }

    private static IEnumerable<string> OperationLockNames(Guid transactionID, string[] targets) =>
        PathLockNames(targets).Append("Compositor.Transaction." + transactionID.ToString("N"));

    private static IEnumerable<string> PathLockNames(IEnumerable<string> targets) => targets.Select(Canonical)
        .Select(path => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select(path => "Compositor.Project." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))));

    private sealed class OperationLocks : IDisposable
    {
        private readonly List<Mutex> _held = [];

        public OperationLocks(IEnumerable<string> names, int timeoutMilliseconds = 2_000)
        {
            var deadline = Environment.TickCount64 + timeoutMilliseconds;
            try
            {
                foreach (var name in names)
                {
                    var mutex = new Mutex(initiallyOwned: false, name);
                    var acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64))); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) throw new IOException("Another Compositor window is saving or recovering this project. Try again when it finishes.");
                        _held.Add(mutex);
                    }
                    finally { if (!acquired) mutex.Dispose(); }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        // Named mutex ownership is thread-affine. All callers are synchronous and release on this thread.
        public void Dispose()
        {
            for (var index = _held.Count - 1; index >= 0; index--)
            {
                _held[index].ReleaseMutex();
                _held[index].Dispose();
            }
            _held.Clear();
        }
    }

    private static void RetireConsumed(Transaction transaction)
    {
        try
        {
            foreach (var kind in Enum.GetValues<ProjectRecoveryKind>())
            {
                if (!Consumed(transaction, kind) && ExpectedHash(transaction, kind) is not null
                    && Exists(SourcePath(transaction, kind))) return;
            }
            var path = RecordPath(transaction.TransactionID);
            EnsureNoLinks(path);
            if (Read(transaction.TransactionID) == transaction) File.Delete(path);
        }
        catch (Exception error) when (IsUnreadable(error)) { }
    }

    internal static Transaction Register(Guid id, string target)
    {
        lock (Gate)
        {
            target = Canonical(target);
            EnsureNoLinks(target);
            var pending = Inspect(SidecarPath(target, id, ProjectRecoveryKind.PendingSave));
            var previous = Directory.Exists(target) ? Inspect(target) : null;
            var transaction = new Transaction
            {
                TransactionID = id,
                TargetPath = target,
                CreatedUtc = DateTimeOffset.UtcNow,
                PendingHash = pending.Hash,
                PreviousHash = previous?.Hash,
            };
            Write(transaction, replacing: false);
            return transaction;
        }
    }

    internal static void CheckDestination(Transaction transaction)
    {
        EnsureNoLinks(transaction.TargetPath);
        if (transaction.PreviousHash is { } previous)
        {
            if (Inspect(transaction.TargetPath).Hash != previous)
                throw new IOException("The project changed while the save was being prepared. Its current version has been kept.");
        }
        else if (Exists(transaction.TargetPath))
        {
            throw new IOException("The save destination was created by something else. Its contents have been kept.");
        }
    }

    /// <summary>Successful saves remove only a verified backup belonging to their own transaction.</summary>
    internal static void Complete(Transaction transaction)
    {
        lock (Gate)
        {
            try
            {
                if (Inspect(transaction.TargetPath).Hash != transaction.PendingHash) return;
                var previous = SourcePath(transaction, ProjectRecoveryKind.PreviousSave);
                if (Exists(previous) && (transaction.PreviousHash is not { } expected || !DeleteOwnedPackage(previous, expected))) return;
                var pending = SourcePath(transaction, ProjectRecoveryKind.PendingSave);
                if (Exists(pending)) return;
                var record = RecordPath(transaction.TransactionID);
                EnsureNoLinks(record);
                // Do not remove a replaced or edited record while finishing an older in-memory transaction.
                if (Read(transaction.TransactionID) != transaction) return;
                File.Delete(record);
            }
            catch (Exception error) when (IsUnreadable(error)) { }
        }
    }

    /// <summary>A failed replacement may put the verified old package back only if the target is absent.</summary>
    internal static void TryRollback(Transaction transaction)
    {
        lock (Gate)
        {
            try
            {
                EnsureNoLinks(transaction.TargetPath);
                if (Exists(transaction.TargetPath) || transaction.PreviousHash is not { } expected) return;
                var previous = SourcePath(transaction, ProjectRecoveryKind.PreviousSave);
                if (!Directory.Exists(previous) || Inspect(previous).Hash != expected) return;
                Directory.Move(previous, transaction.TargetPath);
            }
            catch (Exception error) when (IsUnreadable(error)) { }
        }
    }

    internal static void WriteDurable(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void Write(Transaction transaction, bool replacing)
    {
        EnsureNoLinks(RegistryPath);
        Directory.CreateDirectory(RegistryPath);
        var path = RecordPath(transaction.TransactionID);
        EnsureNoLinks(path);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            WriteDurable(staging, JsonSerializer.SerializeToUtf8Bytes(transaction, Json));
            File.Move(staging, path, overwrite: replacing);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static Transaction Read(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("A recovery record needs a transaction ID.");
        var path = RecordPath(id);
        EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumRecordBytes) throw new InvalidDataException("Invalid recovery record size.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Recovery record changed while it was read.");
        var transaction = JsonSerializer.Deserialize<Transaction>(bytes, Json)
            ?? throw new InvalidDataException("Empty recovery record.");
        if (transaction.Version != 1 || transaction.TransactionID != id || transaction.CreatedUtc == default
            || !IsHash(transaction.PendingHash) || (transaction.PreviousHash is not null && !IsHash(transaction.PreviousHash))
            || !Path.IsPathFullyQualified(transaction.TargetPath)
            || !string.Equals(transaction.TargetPath, Canonical(transaction.TargetPath), PathComparison)
            || Path.GetDirectoryName(transaction.TargetPath) is null)
            throw new InvalidDataException("Invalid recovery record metadata.");
        EnsureNoLinks(transaction.TargetPath);
        return transaction;
    }

    private static string RecordPath(Guid id) => Path.Combine(RegistryPath, $"{id:N}.json");
    private static DateTime? RecordWriteTime(string path)
    {
        try
        {
            EnsureNoLinks(path);
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception error) when (IsUnreadable(error)) { return null; }
    }

    private static string SourcePath(Transaction transaction, ProjectRecoveryKind kind) => SidecarPath(transaction.TargetPath, transaction.TransactionID, kind);
    private static string? ExpectedHash(Transaction transaction, ProjectRecoveryKind kind) => kind == ProjectRecoveryKind.PendingSave ? transaction.PendingHash : transaction.PreviousHash;
    private static bool Consumed(Transaction transaction, ProjectRecoveryKind kind) => kind == ProjectRecoveryKind.PendingSave ? transaction.PendingRecovered : transaction.PreviousRecovered;
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Within(string path, string directory) => string.Equals(path, directory, PathComparison)
        || path.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison);

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void EnsureNoLinks(string path)
    {
        var full = Canonical(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("A recovery path requires a root.");
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;
            current = Path.Combine(current, part);
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Recovery cannot use a linked file or folder.");
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
        }
    }

    private static Package Inspect(string path)
    {
        EnsureNoLinks(path);
        using var snapshot = ProjectStore.LoadUnderOperationLock(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, directory) in Entries(path))
        {
            hash.AppendData(Encoding.UTF8.GetBytes((directory ? "D" : "F") + relative.Replace(Path.DirectorySeparatorChar, '/') + "\0"));
            if (directory) continue;
            var file = Path.Combine(path, relative);
            var length = new FileInfo(file).Length;
            if (length > Model.DocumentLimits.MaxAssetBytes) throw new InvalidDataException("Recovery asset is too large.");
            hash.AppendData(Encoding.ASCII.GetBytes(length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = stream.Read(buffer)) > 0)
            {
                total += read;
                if (total > length) throw new IOException("A recovery asset changed while it was being read.");
                hash.AppendData(buffer.AsSpan(0, read));
            }
            if (total != length) throw new IOException("A recovery asset changed while it was being read.");
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), snapshot.Manifest.DocumentID,
            snapshot.Manifest.Width, snapshot.Manifest.Height);
    }

    private static List<(string Relative, bool Directory)> Entries(string root)
    {
        var entries = new List<(string Relative, bool Directory)>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Recovery cannot follow links.");
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                entries.Add((Path.GetRelativePath(root, entry), isDirectory));
                if (entries.Count > MaximumPackageEntries) throw new InvalidDataException("Recovery package contains too many entries.");
                if (isDirectory) pending.Push(entry);
            }
        }
        return entries.OrderBy(entry => entry.Relative, StringComparer.Ordinal).ToList();
    }

    private static void CopyPackage(string source, string destination)
    {
        EnsureNoLinks(source);
        foreach (var (relative, directory) in Entries(source))
        {
            var output = Path.Combine(destination, relative);
            if (directory) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var input = Path.Combine(source, relative);
            EnsureNoLinks(input);
            using var read = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var write = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough);
            read.CopyTo(write);
            write.Flush(flushToDisk: true);
        }
    }

    private static bool DeleteOwnedPackage(string path, string expected)
    {
        try
        {
            if (Inspect(path).Hash != expected) return false;
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception error) when (IsUnreadable(error)) { return false; }
    }

    private static void DeletePrivateStaging(string path)
    {
        try
        {
            EnsureNoLinks(path);
            if (Directory.Exists(path))
            {
                _ = Entries(path); // Refuse to traverse a link added after the private staging was created.
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (IsUnreadable(error)) { }
    }

    private static bool IsUnreadable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ProjectException or ArgumentException or NotSupportedException or InvalidOperationException;
}

internal enum ProjectSaveCheckpoint { Staged, Journaled, PreviousMoved, ReplacementMoved }
internal sealed class ProjectSaveInterruptionException : Exception { }
