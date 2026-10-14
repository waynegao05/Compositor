using System.Text.Json;

namespace Compositor.Core.IO;

/// <summary>
/// File ▸ Open Recent: the projects opened or saved lately, most recent first. Windows has no system-wide
/// list to keep this in — macOS hands its own to the Mac build — so it is written to a small file of its
/// own, and a project that has since been moved or deleted is left out when the list is read rather than
/// being offered and then failing to open.
/// </summary>
public sealed class RecentProjects
{
    /// <summary>How many are kept, as the Mac build's list keeps about this many.</summary>
    public const int Most = 10;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public RecentProjects(string path) => Path = path;

    /// <summary>Where the list is kept for whoever is using the app.</summary>
    public static string DefaultPath => System.IO.Path.Combine(AppDataPaths.Root, "recent.json");

    public string Path { get; }

    /// <summary>The projects noted lately that are still there, most recent first and each only once.</summary>
    public List<string> All()
    {
        var kept = new List<string>();
        foreach (var entry in Read())
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (!Directory.Exists(entry) && !File.Exists(entry)) continue;
            if (kept.Contains(entry, StringComparer.OrdinalIgnoreCase)) continue;
            kept.Add(entry);
            if (kept.Count >= Most) break;
        }
        return kept;
    }

    /// <summary>Notes a project as opened or saved, and answers with the list as it now reads.</summary>
    public List<string> Note(string project)
    {
        var list = All();
        list.RemoveAll(entry => string.Equals(entry, project, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, project);
        Write(list.Take(Most));
        return All();
    }

    /// <summary>Forgets every project, and answers with a list that is now empty.</summary>
    public List<string> Forgot()
    {
        Write([]);
        return All();
    }

    /// <summary>What is in the file, or nothing when there is no file or it cannot be read.</summary>
    private List<string> Read()
    {
        try
        {
            if (!File.Exists(Path)) return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path), Json) ?? [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // A list of recent projects is a convenience: one that cannot be read is not worth failing over.
            return [];
        }
    }

    private void Write(IEnumerable<string> projects)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (folder is not null) Directory.CreateDirectory(folder);
            File.WriteAllText(Path, JsonSerializer.Serialize(projects.ToList(), Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
