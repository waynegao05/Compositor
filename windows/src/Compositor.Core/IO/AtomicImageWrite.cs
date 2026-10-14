namespace Compositor.Core.IO;

/// <summary>Keep an existing export intact until its replacement has been written and closed.</summary>
internal static class AtomicImageWrite
{
    internal static void Write(string path, Action<Stream> write)
    {
        var target = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!,
            "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                created = true;
                write(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (created && File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
