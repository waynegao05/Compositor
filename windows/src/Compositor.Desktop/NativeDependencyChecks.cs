using System.Text.Json;
using Sdcb.LibRaw;

namespace Compositor.Desktop;

/// <summary>Probe the actual RAW binding without opening a user's image or changing the machine.</summary>
internal static class NativeDependencyChecks
{
    internal const string DocumentationUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170";
    internal const string DownloadUrl = "https://aka.ms/vc14/vc_redist.x64.exe";

    internal sealed record RawRuntimeResult(string Status, bool Available, string? Version, string? Detail);

    internal static RawRuntimeResult InspectRaw()
    {
        if (!OperatingSystem.IsWindows())
            return new("not-checked", false, null, "Windows native RAW dependencies were not checked on this platform.");
        try
        {
            // This follows the same managed/native loader as ImageImporter. Checking registry entries or
            // individual DLL names alone cannot prove that LibRaw and its transitive dependencies load.
            var version = RawContext.Version;
            return string.IsNullOrWhiteSpace(version)
                ? new("unavailable", false, null, "The RAW library returned no version.")
                : new("available", true, version, null);
        }
        catch (Exception error)
        {
            return new("unavailable", false, null, error.GetBaseException().Message);
        }
    }

    internal static string UnavailableMessage => L10n.F(
        "Camera RAW support could not be loaded. Keep all files from the portable package together. If the package is complete, install or repair Microsoft's Visual C++ v14 Redistributable (x64), then restart Compositor. PNG and JPEG editing can still be used. Official download: {0}",
        DownloadUrl);

    /// <summary>0 = available, 3 = optional RAW support unavailable, 4 = not checked on this platform.</summary>
    internal static int Run()
    {
        var result = InspectRaw();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.Status,
            result.Available,
            result.Version,
            result.Detail,
            Scope = "Native RAW dependency loading only; this does not validate camera-file decoding.",
            Guidance = result.Status == "unavailable" ? UnavailableMessage : null,
            DocumentationUrl,
            DownloadUrl,
        }, new JsonSerializerOptions { WriteIndented = true }));
        AppDiagnostics.Record($"Native RAW dependency status: {result.Status}; version: {result.Version ?? "unavailable"}.");
        return result.Status switch { "available" => 0, "unavailable" => 3, _ => 4 };
    }
}
