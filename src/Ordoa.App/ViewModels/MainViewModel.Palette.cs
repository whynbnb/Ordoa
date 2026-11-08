using Ordoa.App.Services;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.App.ViewModels;

/// <summary>Command palette construction and filtering. Split out of MainViewModel for maintainability.</summary>
public sealed partial class MainViewModel
{
    private void RecordPaletteUse(string title)
    {
        var settings = _services.Settings.Current;
        settings.PaletteRecent.RemoveAll(entry => string.Equals(entry, title, StringComparison.Ordinal));
        settings.PaletteRecent.Insert(0, title);
        while (settings.PaletteRecent.Count > 16)
            settings.PaletteRecent.RemoveAt(settings.PaletteRecent.Count - 1);
        settings.PaletteUsage[title] = settings.PaletteUsage.GetValueOrDefault(title) + 1;
        _services.Settings.Save();
    }

    private void FilterPalette()
    {
        var query = PaletteQuery.Trim();
        var settings = _services.Settings.Current;
        int Score(PaletteCommand command)
        {
            var score = command.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 1000 : 0;
            score += settings.PaletteUsage.GetValueOrDefault(command.Title) * 10;
            var recentIndex = settings.PaletteRecent.IndexOf(command.Title);
            if (recentIndex >= 0)
                score += Math.Max(1, 60 - recentIndex * 4);
            return score;
        }

        var matches = PaletteCommands
            .Where(c => query.Length == 0 ||
                        c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        (c.Subtitle?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderByDescending(Score)
            .ThenBy(c => c.Title, StringComparer.CurrentCulture)
            .ToList();

        FilteredCommands.Clear();
        foreach (var item in matches)
            FilteredCommands.Add(item);
        SelectedPaletteCommand = FilteredCommands.FirstOrDefault();
    }

    private void BuildPaletteCommands()
    {
        PaletteCommands.Clear();

        void Add(string title, string? subtitle, string? shortcut, string icon, Func<Task>? action)
        {
            PaletteCommands.Add(new PaletteCommand
            {
                Title = title,
                Subtitle = subtitle,
                Shortcut = shortcut,
                Icon = FileIconCatalog.Resource(icon),
                ExecuteAsync = action,
            });
        }

        Add(L10n.T("撤销"), L10n.T("撤销上一步文件操作"), _services.Shortcuts.GetGesture("undo"), "IconUndo", UndoAsync);
        Add(L10n.T("重做"), L10n.T("重做被撤销的文件操作"), _services.Shortcuts.GetGesture("redo"), "IconRedo", RedoAsync);
        Add(L10n.T("新建标签页"), L10n.T("打开一个新的文件浏览标签"), _services.Shortcuts.GetGesture("newTab"), "IconPlus", NewTabAsync);
        Add(L10n.T("新建窗口"), L10n.T("在新的窗口中打开文件浏览"), _services.Shortcuts.GetGesture("newWindow"), "IconPlus", NewWindowAsync);
        Add(L10n.T("复制标签页"), L10n.T("克隆当前标签页"), null, "IconCopy", DuplicateTabAsync);
        Add(L10n.T("恢复关闭的标签页"), L10n.T("重新打开最近关闭的标签页"), _services.Shortcuts.GetGesture("reopenTab"), "IconRestore", ReopenClosedTabAsync);
        Add(L10n.T("固定 / 取消固定标签页"), L10n.T("固定标签页并保持在标签栏前部"), null, "IconStar", () =>
        {
            TogglePinTab(ActiveTab);
            return Task.CompletedTask;
        });
        Add(L10n.T("关闭标签页"), L10n.T("关闭当前标签页"), _services.Shortcuts.GetGesture("closeTab"), "IconClose", () =>
        {
            CloseTab(ActiveTab);
            return Task.CompletedTask;
        });
        Add(L10n.T("切换分屏"), L10n.T("在当前标签页中显示两个面板"), _services.Shortcuts.GetGesture("toggleSplit"), "IconSplit", ToggleSplitAsync);
        Add(L10n.T("显示/隐藏侧栏"), L10n.T("切换左侧导航栏"), _services.Shortcuts.GetGesture("toggleSidebar"), "IconPanel", () =>
        {
            ToggleSidebar();
            return Task.CompletedTask;
        });
        Add(L10n.T("刷新"), L10n.T("重新读取当前文件夹"), _services.Shortcuts.GetGesture("refresh"), "IconRefresh", RefreshAllAsync);
        Add(L10n.T("主目录"), L10n.T("打开主目录"), "Alt+Home", "IconHome", OpenHomeAsync);
        Add(L10n.T("回收站"), L10n.T("查看已删除的项目"), null, "IconTrash", OpenTrashAsync);
        Add(L10n.T("新建文件夹"), L10n.T("在当前位置创建文件夹"), _services.Shortcuts.GetGesture("newFolder"), "IconPlusCircle", () => PaneAction(p => p.NewFolderAsync()));
        Add(L10n.T("新建空文件"), L10n.T("在当前位置创建空文件"), null, "IconFile", () => PaneAction(p => p.NewFileAsync()));
        Add(L10n.T("打开方式"), L10n.T("选择其他应用打开选中的文件"), _services.Shortcuts.GetGesture("openWith"), "IconApps", () => PaneAction(p => p.OpenWithAsync()));
        Add(L10n.T("重命名"), L10n.T("重命名选中的项目"), _services.Shortcuts.GetGesture("rename"), "IconRename", () => PaneAction(p => p.RenameAsync()));
        Add(L10n.T("批量重命名"), L10n.T("使用查找替换、序号和日期变量批量修改名称"), _services.Shortcuts.GetGesture("batchRename"), "IconRename", () => PaneAction(p => p.BatchRenameAsync()));
        Add(L10n.T("移到回收站"), L10n.T("删除选中的项目到回收站"), _services.Shortcuts.GetGesture("delete"), "IconTrash", () => PaneAction(p => p.DeleteSelectionAsync(false)));
        Add(L10n.T("以管理员身份删除"), L10n.T("使用管理员权限永久删除选中项目"), null, "IconDelete",
            () => PaneAction(p => p.DeleteElevatedAsync()));
        Add(L10n.T("以管理员身份复制到…"), L10n.T("选择目标文件夹并以管理员权限复制"), null, "IconCopy",
            () => PaneAction(p => p.CopyElevatedAsync(false)));
        Add(L10n.T("以管理员身份移动到…"), L10n.T("选择目标文件夹并以管理员权限移动"), null, "IconCut",
            () => PaneAction(p => p.CopyElevatedAsync(true)));
        Add(L10n.T("永久删除"), L10n.T("永久删除选中的项目"), _services.Shortcuts.GetGesture("permanentDelete"), "IconDelete", () => PaneAction(p => p.DeleteSelectionAsync(true)));
        Add(L10n.T("复制"), L10n.T("复制选中的项目"), _services.Shortcuts.GetGesture("copy"), "IconCopy", () => PaneAction(p => p.CopySelectionAsync(false)));
        Add(L10n.T("剪切"), L10n.T("剪切选中的项目"), _services.Shortcuts.GetGesture("cut"), "IconCut", () => PaneAction(p => p.CopySelectionAsync(true)));
        Add(L10n.T("粘贴"), L10n.T("将剪贴板中的项目粘贴到此处"), _services.Shortcuts.GetGesture("paste"), "IconPaste", () => PaneAction(p => p.PasteAsync()));
        Add(L10n.T("复制到…"), L10n.T("选择目标文件夹并复制选中的项目"), null, "IconCopy",
            () => PaneAction(p => p.CopySelectionToAsync(false)));
        Add(L10n.T("移动到…"), L10n.T("选择目标文件夹并移动选中的项目"), null, "IconCut",
            () => PaneAction(p => p.CopySelectionToAsync(true)));
        Add(L10n.T("创建副本"), L10n.T("在当前文件夹中复制选中的项目"), null, "IconCopy", () =>
        {
            ActivePane?.DuplicateSelection();
            return Task.CompletedTask;
        });
        Add(L10n.T("粘贴为符号链接"), L10n.T("为剪贴板中的项目创建符号链接"), null, "IconLink",
            () => PaneAction(p => p.PasteAsLinkAsync()));
        Add(L10n.T("全选"), L10n.T("选择当前视图中的所有项目"), _services.Shortcuts.GetGesture("selectAll"), "IconSelectAll", () =>
        {
            ActivePane?.RequestSelectAll();
            return Task.CompletedTask;
        });
        Add(L10n.T("列表视图"), L10n.T("以详细列表显示文件"), _services.Shortcuts.GetGesture("listView"), "IconList", () =>
        {
            SetView(ViewKind.List);
            return Task.CompletedTask;
        });
        Add(L10n.T("网格视图"), L10n.T("以图标网格显示文件"), _services.Shortcuts.GetGesture("gridView"), "IconGrid", () =>
        {
            SetView(ViewKind.Grid);
            return Task.CompletedTask;
        });
        Add(L10n.T("树视图"), L10n.T("在当前文件夹中展开子文件夹"), null, "IconList", () =>
        {
            ActivePane?.ToggleTreeCommand.Execute(null);
            return Task.CompletedTask;
        });
        Add(L10n.T("Miller 列视图"), L10n.T("按列浏览多级文件夹"), null, "IconPanel", () =>
        {
            ActivePane?.ToggleMillerCommand.Execute(null);
            return Task.CompletedTask;
        });
        Add(L10n.T("显示/隐藏隐藏文件"), L10n.T("切换点文件与隐藏属性"), _services.Shortcuts.GetGesture("toggleHidden"), "IconEye", () =>
        {
            ToggleHidden();
            return Task.CompletedTask;
        });
        Add(L10n.T("快速预览"), L10n.T("按空格预览选中的文件"), _services.Shortcuts.GetGesture("quickPreview"), "IconEye", TogglePreviewAsync);
        Add(L10n.T("预览面板"), L10n.T("显示或隐藏常驻预览面板"), _services.Shortcuts.GetGesture("previewPanel"), "IconEye", () =>
        {
            TogglePreviewPanel();
            return Task.CompletedTask;
        });
        Add(L10n.T("属性"), L10n.T("查看选中项目的详细信息"), "Alt+Enter", "IconInfo", () => PaneAction(p => p.PropertiesAsync()));
        Add(L10n.T("批量权限…"), L10n.T("批量修改八进制权限和所有者"), null, "IconInfo",
            () => PaneAction(p => p.BatchPermissionsAsync()));
        Add(L10n.T("计算校验和"), L10n.T("计算选中文件的校验和"), null, "IconCheckCircle", () => PaneAction(p => p.ChecksumAsync()));
        Add(L10n.T("磁盘占用分析"), L10n.T("用树图查看当前文件夹的空间占用"), null, "IconDisk", () => PaneAction(p => p.AnalyzeDiskUsageAsync()));
        Add(L10n.T("查找重复文件"), L10n.T("按内容查找当前文件夹中的重复文件"), null, "IconCopy", () => PaneAction(p => p.FindDuplicateFilesAsync()));
        Add(L10n.T("批量转换图片…"), L10n.T("转换格式、质量或最大尺寸"), null, "IconImage",
            () => PaneAction(p => p.ConvertImagesAsync()));
        Add(L10n.T("编辑标签…"), L10n.T("为选中的文件或文件夹设置标签"), null, "IconBookmark",
            () => PaneAction(p => p.EditTagsAsync()));
        Add(L10n.T("比较选中文件"), L10n.T("并排查看两个文本或二进制文件的差异"), null, "IconPanel",
            () => PaneAction(p => p.CompareSelectionAsync()));
        Add(L10n.T("比较文件夹…"), L10n.T("递归比较两个文件夹并同步差异"), null, "IconPanel",
            () => PaneAction(p => p.CompareFoldersAsync()));
        if (_services.Settings.Current.EnableGitIntegration)
        {
            Add(L10n.T("Git 状态"), L10n.T("查看当前仓库的分支与未提交变更"), null, "IconCode",
                () => PaneAction(p => p.ShowGitStatusAsync()));
        }
        Add(L10n.T("重新打开关闭的窗口"), L10n.T("恢复上次关闭窗口中的标签页"), null, "IconRestore",
            ReopenClosedWindowAsync);
        Add(L10n.T("保存工作区…"), L10n.T("保存当前标签页和分屏布局"), null, "IconBookmark",
            SaveWorkspaceAsync);
        foreach (var workspace in Workspaces)
        {
            var saved = workspace;
            Add(L10n.Format("工作区：{0}", saved.Name),
                L10n.Format("{0} · 恢复标签页", saved.SavedAt.ToLocalTime().ToString("MM-dd HH:mm")),
                null, "IconBookmark", () => RestoreWorkspaceAsync(saved));
            Add(L10n.Format("删除工作区：{0}", saved.Name), L10n.T("删除已保存的工作区"),
                null, "IconDelete", () => DeleteWorkspaceAsync(saved));
        }
        Add(L10n.T("创建符号链接"), L10n.T("在当前位置创建指向选中项目的链接"), null, "IconLink",
            () => PaneAction(p => p.CreateSymbolicLinkAsync(false)));
        Add(L10n.T("计算文件夹大小"), L10n.T("计算选中文件夹的递归大小"), null, "IconFolder",
            () => PaneAction(p => p.CalculateFolderSizesAsync()));
        Add(L10n.T("操作历史"), L10n.T("查看本次会话的复制、移动与删除记录"), null, "IconRestore",
            () => _services.Dialogs.ShowOperationHistoryAsync(_services.Operations));
        Add(L10n.T("撤销历史"), L10n.T("查看并执行可撤销和可重做的操作"), null, "IconUndo",
            () => _services.Dialogs.ShowUndoHistoryAsync(_services.Undo));
        Add(L10n.T("压缩为 ZIP"), L10n.T("将选中的项目压缩为 zip"), null, "IconArchive", () => PaneAction(p => p.CompressAsync(false)));
        Add(L10n.T("压缩为 tar.gz"), L10n.T("将选中的项目压缩为 tar.gz"), null, "IconArchive", () => PaneAction(p => p.CompressAsync(true)));
        Add(L10n.T("压缩为 7z"), L10n.T("使用 7z 压缩选中的项目"), null, "IconArchive", () => PaneAction(p => p.Compress7zAsync()));
        Add(L10n.T("解压到此处"), L10n.T("解压选中的压缩包"), null, "IconDownload", () => PaneAction(p => p.ExtractAsync()));
        Add(L10n.T("解压到…"), L10n.T("选择目标文件夹后解压"), null, "IconDownload", () => PaneAction(p => p.ExtractSelectionToAsync()));
        Add(L10n.T("以压缩包浏览"), L10n.T("像文件夹一样打开选中的压缩包"), null, "IconArchive", () => PaneAction(p => p.OpenArchiveAsync()));
        Add(L10n.T("在终端中打开"), L10n.T("在当前位置或选中的文件夹中启动终端"), null, "IconTerminal", () => PaneAction(p => p.OpenTerminalAsync()));
        if (_services.Terminals.GetEnabledHerdr() is not null)
        {
            Add(L10n.T("在 Herdr 中打开"), L10n.T("在当前文件夹中启动 Herdr 工作区"), null, "IconTerminal",
                () => PaneAction(p => p.OpenInHerdrAsync()));
        }
        foreach (var command in _services.CustomCommands.GetAll())
        {
            if (string.IsNullOrWhiteSpace(command.Command))
                continue;
            var id = command.Id;
            var name = string.IsNullOrWhiteSpace(command.Name) ? command.Command : command.Name;
            Add(name, L10n.T("自定义命令"), null,
                command.RunInTerminal ? "IconTerminal" : "IconCommand",
                () => PaneAction(p => p.RunCustomCommandAsync(id)));
        }
        Add(L10n.T("复制路径"), L10n.T("复制选中项目的完整路径"), null, "IconLink", () => PaneAction(p => p.CopyPathAsync()));
        Add(L10n.T("添加书签"), L10n.T("将当前文件夹加入侧栏"), _services.Shortcuts.GetGesture("bookmark"), "IconBookmark", AddBookmarkAsync);
        Add(L10n.T("新建远程连接"), L10n.T("连接到 FTP、SFTP、SMB 或 WebDAV"), null, "IconCloudAdd", () => ShowConnectionDialogAsync(null));
        Add(L10n.T("设置"), L10n.T("打开应用设置"), _services.Shortcuts.GetGesture("settings"), "IconSettings", OpenSettingsAsync);
        Add(L10n.T("键盘快捷键"), L10n.T("查看所有快捷键"), _services.Shortcuts.GetGesture("about"), "IconKeyboard", () => _services.Dialogs.ShowAboutAsync());
        Add(L10n.T("关闭分屏"), L10n.T("隐藏当前标签页的第二个面板"), null, "IconSplit", () =>
        {
            if (ActiveTab is { IsSplit: true })
            {
                ActiveTab.CloseSplit();
                OnPropertyChanged(nameof(ActivePane));
                UpdateWindowTitle();
            }
            return Task.CompletedTask;
        });
    }

    private Task PaneAction(Func<PaneViewModel, Task> action)
    {
        var pane = ActivePane;
        return pane is null ? Task.CompletedTask : action(pane);
    }
}
