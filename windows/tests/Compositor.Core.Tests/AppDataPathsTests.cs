using Compositor.Core.IO;
using Compositor.Core.Localization;

namespace Compositor.Core.Tests;

public sealed class AppDataPathsTests
{
    [Fact]
    public void DefaultPreferenceLocationsRemainCompatible()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Compositor");
        Assert.Equal(expected, AppDataPaths.Root);
        Assert.Equal(Path.Combine(expected, "recent.json"), RecentProjects.DefaultPath);
        Assert.Equal(Path.Combine(expected, "tools.json"), ToolDefaults.DefaultPath);
        Assert.Equal(Path.Combine(expected, "shortcuts.json"), ShortcutDefaults.DefaultPath);
        Assert.Equal(Path.Combine(expected, "ui-language.json"), UiText.PreferencePath);
    }

    [Fact]
    public void NestedScopesRestoreTheirParentAndRemoveOnlyTheirOwnDirectories()
    {
        var original = AppDataPaths.Root;
        string outerPath;
        using (var outer = AppDataPaths.UseTemporaryProfile())
        {
            outerPath = AppDataPaths.Root;
            Assert.NotEqual(original, outerPath);
            var marker = Path.Combine(outerPath, "keep.txt");
            File.WriteAllText(marker, "outer profile");
            string innerPath;
            using (AppDataPaths.UseTemporaryProfile())
            {
                innerPath = AppDataPaths.Root;
                Assert.NotEqual(outerPath, innerPath);
                File.WriteAllText(Path.Combine(innerPath, "remove.txt"), "inner profile");
            }
            Assert.Equal(outerPath, AppDataPaths.Root);
            Assert.False(Directory.Exists(innerPath));
            Assert.Equal("outer profile", File.ReadAllText(marker));
            outer.Dispose();
            Assert.Equal(original, AppDataPaths.Root);
        }
        Assert.Equal(original, AppDataPaths.Root);
        Assert.False(Directory.Exists(outerPath));
    }

    [Fact]
    public void SettingsWritesUseTheTemporaryProfileAndLeaveTheOuterFilesUntouched()
    {
        using var outer = AppDataPaths.UseTemporaryProfile();
        var outerFiles = Paths().ToDictionary(path => path, path => "existing " + Path.GetFileName(path));
        foreach (var (path, contents) in outerFiles) File.WriteAllText(path, contents);
        var environmentBefore = Environment.GetEnvironmentVariable("APPDATA");
        using (AppDataPaths.UseTemporaryProfile())
        {
            var temporary = AppDataPaths.Root;
            Assert.All(Paths(), path => Assert.Equal(temporary, Path.GetDirectoryName(path)));
            var project = Path.Combine(temporary, "中文测试.comp");
            Directory.CreateDirectory(project);
            Assert.Equal([project], new RecentProjects(RecentProjects.DefaultPath).Note(project));
            new ToolDefaults { ShowGrid = true }.Save(ToolDefaults.DefaultPath);
            Assert.True(ToolDefaults.Load(ToolDefaults.DefaultPath).ShowGrid);
            new ShortcutDefaults().Save(ShortcutDefaults.DefaultPath);
            Assert.True(UiText.SavePreference(UiText.SimplifiedChinese));
            Assert.Equal(UiText.SimplifiedChinese, UiText.ReadPreference(UiText.PreferencePath));
            Assert.All(Paths(), path => Assert.True(File.Exists(path)));
            Assert.Equal(environmentBefore, Environment.GetEnvironmentVariable("APPDATA"));
            foreach (var (path, contents) in outerFiles) Assert.Equal(contents, File.ReadAllText(path));
        }
        Assert.Equal(outerFiles.Keys.Order(), Paths().Order());
        foreach (var (path, contents) in outerFiles) Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public async Task ChildTasksInheritTheirProfileAndConcurrentScopesStaySeparate()
    {
        using var outer = AppDataPaths.UseTemporaryProfile();
        var outerPath = AppDataPaths.Root;
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(() => CheckChild(firstReady, secondReady));
        var second = Task.Run(() => CheckChild(secondReady, firstReady));
        var paths = await Task.WhenAll(first, second);
        Assert.NotEqual(paths[0], paths[1]);
        Assert.All(paths, path => Assert.False(Directory.Exists(path)));
        Assert.Equal(outerPath, AppDataPaths.Root);

        async Task<string> CheckChild(TaskCompletionSource ready, TaskCompletionSource other)
        {
            Assert.Equal(outerPath, AppDataPaths.Root);
            using var profile = AppDataPaths.UseTemporaryProfile();
            var path = AppDataPaths.Root;
            ready.SetResult();
            await other.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(path, AppDataPaths.Root);
            Assert.NotEqual(outerPath, path);
            return path;
        }
    }

    private static string[] Paths() =>
        [RecentProjects.DefaultPath, ToolDefaults.DefaultPath, ShortcutDefaults.DefaultPath, UiText.PreferencePath];
}
