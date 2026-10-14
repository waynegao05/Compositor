namespace Compositor.Core.IO;

/// <summary>Why a project could not be read or written, as the Swift build's <c>ProjectError</c> reports it.</summary>
public enum ProjectError
{
    Invalid,
    Version,
    MissingImage,
    TooLarge,
    Encode,
    UnsafeDestination,
    ExtraFiles,
}

public sealed class ProjectException : Exception
{
    public ProjectException(ProjectError error, int version = 0) : base(Describe(error, version))
    {
        Error = error;
        Version = version;
    }

    public ProjectError Error { get; }

    /// <summary>Set when <see cref="Error"/> is <see cref="ProjectError.Version"/>.</summary>
    public int Version { get; }

    private static string Describe(ProjectError error, int version) => error switch
    {
        ProjectError.Invalid => "This is not a valid Compositor project, or its metadata is damaged.",
        ProjectError.Version =>
            $"This project uses format version {version}. This app supports versions {Format.ProjectManifest.SupportedLower}–{Format.ProjectManifest.SupportedUpper}.",
        ProjectError.MissingImage => "An image inside the project is missing or damaged. The current document has not been replaced.",
        ProjectError.TooLarge =>
            $"This project exceeds the supported canvas, layer, file-size, or {Model.DocumentLimits.DocumentBudgetMegapixels}-megapixel document limit.",
        ProjectError.Encode => "An image could not be saved. The previous project has not been replaced.",
        ProjectError.UnsafeDestination =>
            "The existing destination is not a readable Compositor project or is a link. Save to a new folder to keep its contents safe.",
        ProjectError.ExtraFiles =>
            "The destination contains files or folders outside the Compositor project. Move those items elsewhere or save to a new folder.",
        _ => "The project could not be read or written.",
    };
}
