namespace Compositor.Core.IO;

/// <summary>
/// Watches a project's folder for someone else writing it. A project is a package — a manifest and the images
/// beside it — so what is compared is the whole folder: the name, length and write time of every file in it.
/// The Mac build watches its packages the same way, which is what lets a person work on a project with an
/// editor open beside the app and see the canvas take the change.
/// </summary>
public sealed class ProjectWatch
{
    private readonly string _path;
    private Dictionary<string, (DateTime Written, long Size)>? _seen;
    private bool _retry;

    private ProjectWatch(string path)
    {
        _path = path;
        _seen = Fingerprint(path);
    }

    /// <summary>A watch over the project at <paramref name="path"/>, or null when there is no such folder.</summary>
    public static ProjectWatch? For(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        return new ProjectWatch(path);
    }

    /// <summary>
    /// Whether a complete read of the package differs from the last complete read. An inaccessible package
    /// does not replace the baseline, so a rename gap or an interrupted scan can be retried on the next tick.
    /// A successfully observed change is reported once unless the caller asks to retry its failed load.
    /// </summary>
    public bool Changed()
    {
        var now = Fingerprint(_path);
        if (now is null) return false;
        var changed = _retry || _seen is null || !Same(now, _seen);
        _seen = now;
        _retry = false;
        return changed;
    }

    /// <summary>
    /// Takes what the package holds now as what it holds, which is what the app does after saving it itself:
    /// a save is not a change to be told about.
    /// </summary>
    public void Remember()
    {
        if (Fingerprint(_path) is not { } now) return;
        _seen = now;
        _retry = false;
    }

    /// <summary>
    /// The caller could not load a reported change. Report it again after the next complete scan, even if
    /// its metadata has not changed, since a file may merely have been locked during the previous load.
    /// </summary>
    public void Retry() => _retry = true;

    private static bool Same(Dictionary<string, (DateTime Written, long Size)> one,
        Dictionary<string, (DateTime Written, long Size)> other)
    {
        if (one.Count != other.Count) return false;
        foreach (var (name, state) in one)
        {
            if (!other.TryGetValue(name, out var was) || was != state) return false;
        }
        return true;
    }

    /// <summary>A complete package fingerprint, or null when its current state cannot be read.</summary>
    private static Dictionary<string, (DateTime Written, long Size)>? Fingerprint(string path)
    {
        var files = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                return null;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                var info = new FileInfo(file);
                info.Refresh();
                // Losing one file during enumeration makes the whole scan incomplete. Keeping a partial
                // dictionary here would acknowledge a state that the consumer has never successfully read.
                if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    return null;
                files[Path.GetRelativePath(path, file)] = (info.LastWriteTimeUtc, info.Length);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is not a folder that has changed: nothing is claimed about it.
            return null;
        }
        return files;
    }
}
