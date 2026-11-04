using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class DesktopEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoa-desktop-" + Guid.NewGuid().ToString("N"));

    public DesktopEntryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private string WriteEntry(string contents)
    {
        var path = Path.Combine(_root, "test.desktop");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void ParsesApplicationEntry()
    {
        var path = WriteEntry("""
            [Desktop Entry]
            Type=Application
            Name=Test Editor
            Comment=Edit things
            Exec=test-editor --new %U
            TryExec=test-editor
            Icon=test-editor
            MimeType=text/plain;text/markdown;
            Terminal=false
            NoDisplay=false
            """);

        var app = DesktopEntryService.Parse(path, "test.desktop");

        Assert.NotNull(app);
        Assert.Equal("test.desktop", app.Id);
        Assert.Equal("Test Editor", app.Name);
        Assert.Equal("Edit things", app.Comment);
        Assert.Equal("test-editor --new %U", app.Exec);
        Assert.Equal("test-editor", app.TryExec);
        Assert.Equal("test-editor", app.Icon);
        Assert.Contains("text/plain", app.MimeTypes);
        Assert.Contains("text/markdown", app.MimeTypes);
        Assert.False(app.Terminal);
    }

    [Fact]
    public void SkipsHiddenAndNonApplicationEntries()
    {
        Assert.Null(DesktopEntryService.Parse(WriteEntry("""
            [Desktop Entry]
            Type=Application
            Name=Hidden
            Exec=hidden %f
            Hidden=true
            """), "hidden.desktop"));

        Assert.Null(DesktopEntryService.Parse(WriteEntry("""
            [Desktop Entry]
            Type=Link
            Name=Link
            Exec=link %f
            """), "link.desktop"));
    }

    [Fact]
    public void ParsesNoDisplayApplications()
    {
        var path = WriteEntry("""
            [Desktop Entry]
            Type=Application
            Name=Hidden Handler
            Exec=hidden-handler %f
            MimeType=text/plain;
            NoDisplay=true
            """);

        var app = DesktopEntryService.Parse(path, "hidden-handler.desktop");
        Assert.NotNull(app);
        Assert.True(app.NoDisplay);
    }

    [Fact]
    public void UsesChineseLocalizedNameWhenAvailable()
    {
        var previous = L10n.Language;
        try
        {
            L10n.SetLanguage(UiLanguage.Chinese);
            var path = WriteEntry("""
                [Desktop Entry]
                Type=Application
                Name=Text Editor
                Name[zh_CN]=文本编辑器
                Comment=Edit text
                Comment[zh_CN]=编辑文本
                Exec=editor %f
                """);

            var app = DesktopEntryService.Parse(path, "editor.desktop");
            Assert.NotNull(app);
            Assert.Equal("文本编辑器", app.Name);
            Assert.Equal("编辑文本", app.Comment);
        }
        finally
        {
            L10n.SetLanguage(previous);
        }
    }

    [Fact]
    public void BuildsArgumentsFromDesktopFieldCodes()
    {
        var args = DesktopEntryService.BuildArguments(
            "test-editor --new %f --title \"hello world\"",
            "/tmp/a file.txt");

        Assert.Equal(
            ["test-editor", "--new", "/tmp/a file.txt", "--title", "hello world"],
            args);
    }

    [Fact]
    public void BuildsUriForUrlFieldCode()
    {
        var args = DesktopEntryService.BuildArguments(
            "test-editor %U",
            "/tmp/a file.txt");

        Assert.Equal(2, args.Count);
        Assert.Equal("test-editor", args[0]);
        Assert.StartsWith("file://", args[1]);
        Assert.Contains("a%20file.txt", args[1]);
    }

    [Fact]
    public void AppendsPathWhenExecHasNoFileField()
    {
        var args = DesktopEntryService.BuildArguments("test-editor --reuse", "/tmp/a.txt");
        Assert.Equal(["test-editor", "--reuse", "/tmp/a.txt"], args);
    }

    [Fact]
    public void RemovesIconField()
    {
        var args = DesktopEntryService.BuildArguments("test-editor --icon %i %f", "/tmp/a.txt");
        Assert.Equal(["test-editor", "/tmp/a.txt"], args);
    }

    [Fact]
    public void HandlesPercentEscape()
    {
        var args = DesktopEntryService.BuildArguments("test-editor --progress 100%% %f", "/tmp/a.txt");
        Assert.Equal(["test-editor", "--progress", "100%", "/tmp/a.txt"], args);
    }
}
