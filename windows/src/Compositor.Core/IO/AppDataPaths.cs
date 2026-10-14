namespace Compositor.Core.IO;

/// <summary>The preference directory, with a temporary profile for automated checks.</summary>
public static class AppDataPaths
{
    private static readonly AsyncLocal<TemporaryProfile?> Current = new();

    /// <summary>Uses the existing roaming profile unless a temporary profile is active.</summary>
    public static string Root
    {
        get
        {
            var profile = Current.Value;
            while (profile is { IsDisposed: true }) profile = profile.Previous;
            return profile?.Root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Compositor");
        }
    }

    /// <summary>
    /// Isolates subsequent settings reads and writes in a new temporary directory. Enter before creating
    /// settings objects or windows. Child tasks inherit the profile through their execution context;
    /// independent checks keep separate profiles. No environment variables are changed.
    /// </summary>
    public static IDisposable UseTemporaryProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "Compositor-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profile = new TemporaryProfile(root, Current.Value);
        Current.Value = profile;
        return profile;
    }

    private sealed class TemporaryProfile(string root, TemporaryProfile? previous) : IDisposable
    {
        private int _disposed;
        public string Root { get; } = root;
        public TemporaryProfile? Previous { get; } = previous;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (ReferenceEquals(Current.Value, this)) Current.Value = Previous;
            // Root is always the unique directory this scope created, never a supplied or roaming path.
            // Restore the previous profile before cleanup, even if an open file prevents deletion.
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A locked diagnostic fixture can remain in the OS temporary folder until it is cleaned.
            }
        }
    }
}
