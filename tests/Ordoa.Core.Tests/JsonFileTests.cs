using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class JsonFileTests
{
    [Fact]
    public void AppSettings_RoundTripsThroughSourceGeneratedMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings
            {
                Language = UiLanguage.English,
                FfmpegPath = "/usr/bin/ffmpeg",
                HiddenPlaces = ["music", "videos"],
                WindowWidth = 1280,
                UseSystemWindowFrame = true,
                WindowButtonMode = WindowButtonMode.Show,
                ShowVolumes = false,
                ShowHiddenVolumes = true,
                ShowTagsColumn = true,
                WindowsShellIntegrationEnabled = true,
                MacFinderServiceEnabled = true,
                ColumnOrder =
                [
                    nameof(FileListColumn.Name),
                    nameof(FileListColumn.Owner),
                    nameof(FileListColumn.Tags),
                    nameof(FileListColumn.Size),
                    nameof(FileListColumn.Modified),
                    nameof(FileListColumn.Type),
                    nameof(FileListColumn.Permissions),
                ],
                ColumnWidths = new Dictionary<string, double> { ["Size"] = 91, ["Tags"] = 132 },
                GridThumbnailSize = GridThumbnailSize.Large,
                ListDensity = ListDensity.Compact,
                GroupBy = GroupByMode.Modified,
                FolderViews = new Dictionary<string, FolderViewState>
                {
                    ["local|/tmp"] = new FolderViewState
                    {
                        ColumnOrder = [nameof(FileListColumn.Name), nameof(FileListColumn.Size)],
                        ColumnWidths = new Dictionary<string, double> { ["Size"] = 120 },
                    },
                },
                CustomSidebarPaths = ["/home/why/Projects"],
                TerminalMode = TerminalDetectionMode.Manual,
                ManualTerminalPath = "/usr/bin/kitty",
                TerminalPreferences =
                [
                    new TerminalPreference
                    {
                        Id = "exe:kitty",
                        IsDefault = true,
                        ShowInContextMenu = true,
                    },
                ],
                CustomCommands =
                [
                    new CustomCommand
                    {
                        Id = "deploy",
                        Name = "Deploy",
                        Command = "./scripts/deploy.sh %d",
                        WorkingDirectory = "%d",
                        ShowInContextMenu = true,
                        RunInTerminal = true,
                        RequiresSelection = false,
                    },
                ],
            };

            JsonFile.Save(path, settings);
            var loaded = JsonFile.Load<AppSettings>(path);

            Assert.NotNull(loaded);
            Assert.Equal(UiLanguage.English, loaded!.Language);
            Assert.Equal("/usr/bin/ffmpeg", loaded.FfmpegPath);
            Assert.Equal(["music", "videos"], loaded.HiddenPlaces);
            Assert.Equal(1280, loaded.WindowWidth);
            Assert.True(loaded.UseSystemWindowFrame);
            Assert.Equal(WindowButtonMode.Show, loaded.WindowButtonMode);
            Assert.False(loaded.ShowVolumes);
            Assert.True(loaded.ShowHiddenVolumes);
            Assert.True(loaded.ShowTagsColumn);
            Assert.True(loaded.WindowsShellIntegrationEnabled);
            Assert.True(loaded.MacFinderServiceEnabled);
            Assert.Equal(7, loaded.ColumnOrder.Count);
            Assert.Equal(nameof(FileListColumn.Owner), loaded.ColumnOrder[1]);
            Assert.Equal(91, loaded.ColumnWidths["Size"]);
            Assert.Equal(132, loaded.ColumnWidths["Tags"]);
            Assert.Equal(GridThumbnailSize.Large, loaded.GridThumbnailSize);
            Assert.Equal(ListDensity.Compact, loaded.ListDensity);
            Assert.Equal(GroupByMode.Modified, loaded.GroupBy);
            Assert.Equal(120, loaded.FolderViews["local|/tmp"].ColumnWidths!["Size"]);
            Assert.Equal(2, loaded.FolderViews["local|/tmp"].ColumnOrder!.Count);
            Assert.Equal(["/home/why/Projects"], loaded.CustomSidebarPaths);
            Assert.Equal(TerminalDetectionMode.Manual, loaded.TerminalMode);
            Assert.Equal("/usr/bin/kitty", loaded.ManualTerminalPath);
            Assert.Single(loaded.TerminalPreferences);
            Assert.True(loaded.TerminalPreferences[0].IsDefault);
            Assert.Single(loaded.CustomCommands);
            Assert.Equal("Deploy", loaded.CustomCommands[0].Name);
            Assert.True(loaded.CustomCommands[0].RunInTerminal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Session_RoundTripsThroughSourceGeneratedMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-session-{Guid.NewGuid():N}.json");
        try
        {
            var state = new SessionState
            {
                ActiveTab = 1,
                Tabs =
                [
                    new SessionTab
                    {
                        ActivePane = 0,
                        Panes = [new SessionPane { FileSystemId = "local", Path = "/home" }],
                    },
                ],
            };

            JsonFile.Save(path, state);
            var loaded = JsonFile.Load<SessionState>(path);

            Assert.NotNull(loaded);
            Assert.Equal(1, loaded!.ActiveTab);
            Assert.Single(loaded.Tabs);
            Assert.Equal("/home", loaded.Tabs[0].Panes[0].Path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
