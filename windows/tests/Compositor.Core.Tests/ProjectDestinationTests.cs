using Compositor.Core.IO;

namespace Compositor.Core.Tests;

public sealed class ProjectDestinationTests : ProjectTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOrdinaryDirectoryIsNeverReplaced(bool withFiles)
    {
        var destination = PathIn("普通目录.comp");
        Directory.CreateDirectory(destination);
        if (withFiles) File.WriteAllText(Path.Combine(destination, "笔记.txt"), "keep these notes");
        var before = Contents(destination);
        using var project = KitchenSink();
        Assert.Equal(ProjectError.UnsafeDestination,
            Assert.Throws<ProjectException>(() => ProjectStore.Save(project, destination)).Error);
        AssertContents(destination, before);
        Assert.Empty(Directory.GetDirectories(Root, ".*"));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("image")]
    public void ADamagedProjectCannotBeSilentlyOverwritten(string damaged)
    {
        var destination = PathIn("Damaged.comp");
        using var project = KitchenSink();
        ProjectStore.Save(project, destination);
        var file = damaged == "manifest" ? Path.Combine(destination, ProjectStore.ManifestName)
            : Directory.GetFiles(Path.Combine(destination, ProjectStore.ImagesName))[0];
        File.WriteAllText(file, "damaged but must be preserved");
        var before = Contents(destination);
        Assert.Equal(ProjectError.UnsafeDestination,
            Assert.Throws<ProjectException>(() => ProjectStore.Save(project, destination)).Error);
        AssertContents(destination, before);
        Assert.Empty(Directory.GetDirectories(Root, ".*"));
    }

    [Theory]
    [InlineData("notes.txt", false)]
    [InlineData("images/unused.png", false)]
    [InlineData("QuickLook/notes.txt", false)]
    [InlineData(".DS_Store", false)]
    [InlineData("extra-empty-folder", true)]
    public void UnrelatedProjectContentsArePreservedByRejectingTheReplacement(string extra, bool directory)
    {
        var destination = PathIn("Extra.comp");
        using var project = KitchenSink();
        ProjectStore.Save(project, destination);
        var path = Path.Combine(destination, extra.Replace('/', Path.DirectorySeparatorChar));
        if (directory) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "unrelated contents");
        }
        var before = Contents(destination);
        Assert.Equal(ProjectError.ExtraFiles,
            Assert.Throws<ProjectException>(() => ProjectStore.Save(project, destination)).Error);
        AssertContents(destination, before);
        Assert.Equal(directory, Directory.Exists(path));
        Assert.Empty(Directory.GetDirectories(Root, ".*"));
    }

    [Fact]
    public void ATargetLinkDoesNotReplaceTheLinkOrItsRealProject()
    {
        var actual = PathIn("Actual.comp");
        using var project = KitchenSink();
        ProjectStore.Save(project, actual);
        var before = Contents(actual);
        var link = PathIn("Link.comp");
        Directory.CreateSymbolicLink(link, actual);
        try
        {
            Assert.Equal(ProjectError.UnsafeDestination,
                Assert.Throws<ProjectException>(() => ProjectStore.Save(project, link)).Error);
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            AssertContents(actual, before);
            Assert.Empty(Directory.GetDirectories(Root, ".*"));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ATrailingDirectorySeparatorStillReplacesBesideTheTarget()
    {
        var destination = PathIn("末尾分隔符.comp");
        using var project = KitchenSink();
        ProjectStore.Save(project, destination);
        project.Manifest.Layers[0].Name = "第二次保存";
        ProjectStore.Save(project, destination + Path.DirectorySeparatorChar);
        using var loaded = ProjectStore.Load(destination);
        Assert.Equal("第二次保存", loaded.Manifest.Layers[0].Name);
        Assert.Empty(Directory.GetDirectories(Root, ".*", SearchOption.AllDirectories));
    }

    private static Dictionary<string, byte[]> Contents(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);

    private static void AssertContents(string directory, Dictionary<string, byte[]> expected)
    {
        Assert.True(Directory.Exists(directory));
        var actual = Contents(directory);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (file, bytes) in expected) Assert.Equal(bytes, actual[file]);
    }
}
