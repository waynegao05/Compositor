using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;

namespace Compositor.Desktop;

/// <summary>Display known operation failures without changing the exception kept in diagnostics.</summary>
internal static class OperationErrors
{
    internal static string Describe(Exception error) => error switch
    {
        ProjectException { Error: ProjectError.Invalid } =>
            L10n.T("This is not a valid Compositor project, or its metadata is damaged."),
        ProjectException { Error: ProjectError.Version } project =>
            L10n.F("This project uses format version {0}. This app supports versions {1} through {2}.",
                project.Version, ProjectManifest.SupportedLower, ProjectManifest.SupportedUpper),
        ProjectException { Error: ProjectError.MissingImage } =>
            L10n.T("An image inside the project is missing or damaged. The current document has not been replaced."),
        ProjectException { Error: ProjectError.TooLarge } =>
            L10n.F("This project exceeds the supported canvas, layer, file-size, or {0}-megapixel document limit.",
                DocumentLimits.DocumentBudgetMegapixels),
        ProjectException { Error: ProjectError.Encode } =>
            L10n.T("An image could not be saved. The previous project has not been replaced."),
        ImportException { Error: ImportError.Unreadable } =>
            L10n.T("The image could not be read. It may be damaged or unavailable."),
        ImportException { Error: ImportError.Unsupported } =>
            L10n.T("This image format is not supported by this import operation. Try PNG, JPEG, TIFF or SVG."),
        ImportException { Error: ImportError.TooLarge } =>
            L10n.F("This import exceeds the {0:N0}-pixel side limit or the {1}-megapixel document budget.",
                DocumentLimits.MaxSide, DocumentLimits.DocumentBudgetMegapixels),
        // Safety failures already have catalog keys. Native dependency messages may already be translated.
        // Unknown failures keep their details, including paths, rather than guessing at a cause.
        _ => L10n.T(error.Message),
    };

    /// <summary>Exercise both language catalogs, dynamic limits and the unknown-message fallback.</summary>
    internal static string SelfCheck()
    {
        var previous = L10n.CurrentLanguage;
        var passed = 0;
        try
        {
            foreach (var language in new[] { L10n.English, L10n.SimplifiedChinese })
            {
                L10n.Initialize(language);
                var chinese = language == L10n.SimplifiedChinese;
                var budget = DocumentLimits.DocumentBudgetMegapixels;
                var cases = new (Exception Error, string English, string Chinese)[]
                {
                    (new ProjectException(ProjectError.Invalid),
                        "This is not a valid Compositor project, or its metadata is damaged.",
                        "此文件夹不是有效的 Compositor 工程，或工程元数据已损坏。"),
                    (new ProjectException(ProjectError.Version, 123),
                        $"This project uses format version 123. This app supports versions {ProjectManifest.SupportedLower} through {ProjectManifest.SupportedUpper}.",
                        $"此工程使用格式版本 123，当前应用支持版本 {ProjectManifest.SupportedLower} 至 {ProjectManifest.SupportedUpper}。"),
                    (new ProjectException(ProjectError.MissingImage),
                        "An image inside the project is missing or damaged. The current document has not been replaced.",
                        "工程内的图像缺失或损坏，当前文档保持原样。"),
                    (new ProjectException(ProjectError.TooLarge),
                        $"This project exceeds the supported canvas, layer, file-size, or {budget}-megapixel document limit.",
                        $"此工程超出支持的画布、图层、文件大小或 {budget} 百万像素文档上限。"),
                    (new ProjectException(ProjectError.Encode),
                        "An image could not be saved. The previous project has not been replaced.",
                        "图像保存失败，原工程保持原样。"),
                    (new ImportException(ImportError.Unreadable, "native decoder error"),
                        "The image could not be read. It may be damaged or unavailable.",
                        "无法读取图像，文件可能已损坏或无法访问。"),
                    (new ImportException(ImportError.Unsupported, "unsupported Photoshop mode"),
                        "This image format is not supported by this import operation. Try PNG, JPEG, TIFF or SVG.",
                        "当前导入操作不支持此图像格式，请尝试 PNG、JPEG、TIFF 或 SVG。"),
                    (new ImportException(ImportError.TooLarge),
                        $"This import exceeds the {DocumentLimits.MaxSide:N0}-pixel side limit or the {budget}-megapixel document budget.",
                        $"导入图像超出单边 {DocumentLimits.MaxSide:N0} 像素限制或文档 {budget} 百万像素上限。"),
                    (new ProjectException(ProjectError.UnsafeDestination),
                        "The existing destination is not a readable Compositor project or is a link. Save to a new folder to keep its contents safe.",
                        "目标位置不是可读取的 Compositor 工程，或属于链接。请另存到新的文件夹，以保留原有内容。"),
                    (new ProjectException(ProjectError.ExtraFiles),
                        "The destination contains files or folders outside the Compositor project. Move those items elsewhere or save to a new folder.",
                        "目标工程内含有额外的文件或文件夹。请先将这些内容移到其他位置，或另存到新的文件夹。"),
                    (new ProjectException((ProjectError)999),
                        "The project could not be read or written.", "无法读取或写入工程。"),
                    (new IOException("A project is a folder, and that path is a file."),
                        "A project is a folder, and that path is a file.", "项目需要保存为文件夹，但所选路径是文件"),
                };
                foreach (var (error, english, translated) in cases)
                {
                    var expected = chinese ? translated : english;
                    if (Describe(error) != expected)
                        throw new InvalidOperationException($"Operation error display differs for {error.GetType().Name} in {language}.");
                    passed++;
                }
                foreach (var failure in new Exception[]
                {
                    new IOException(@"Decoder failure on C:\用户\图像.png"),
                    new InvalidOperationException(NativeDependencyChecks.UnavailableMessage),
                })
                {
                    if (Describe(failure) != failure.Message)
                        throw new InvalidOperationException("An unknown or already-translated error message was changed.");
                    passed++;
                }
            }
            return $"operation errors: {passed} checks passed; bilingual categories and limits, safety catalog reuse, unknown details retained";
        }
        finally { L10n.Initialize(previous); }
    }
}
