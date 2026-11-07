using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Ordoa.App.ViewModels.Dialogs;
using Ordoa.App.Views;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly Func<Window?> _ownerProvider;
    private readonly List<(string Kind, Window Window)> _openDialogs = [];
    private readonly SemaphoreSlim _conflictGate = new(1, 1);

    public DialogService(Func<Window?> ownerProvider)
    {
        _ownerProvider = ownerProvider;
    }

    /// <summary>True while a dialog owned by the given main window is visible.</summary>
    public bool HasOpenDialogFor(Window owner)
    {
        foreach (var entry in _openDialogs)
        {
            if (entry.Window.IsVisible && IsOwnedBy(entry.Window, owner))
                return true;
        }

        return false;
    }

    public async Task<ConnectionDialogResult?> ShowConnectionAsync(ConnectionProfile? preset = null)
    {
        if (!TryPrepareDialog("connection", out var owner))
            return null;

        var services = App.Services;
        if (services is null)
            return null;

        string? password = null;
        if (preset is not null && !preset.Anonymous)
        {
            try { password = await services.FileSystems.Credentials.GetPasswordAsync(preset.Id); }
            catch { /* ignore */ }
        }

        var viewModel = new ConnectionDialogViewModel(preset, password, services.FileSystems);
        var window = new ConnectionWindow { DataContext = viewModel };
        return await ShowDialogAsync<ConnectionDialogResult>("connection", window, owner).ConfigureAwait(true);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, bool destructive = false)
    {
        if (!TryPrepareDialog("confirm", out var owner))
            return false;

        confirmText ??= L10n.T("确定");
        var window = new ConfirmWindow
        {
            DataContext = new ConfirmDialogViewModel
            {
                TitleText = title,
                Message = message,
                ConfirmText = confirmText,
                Destructive = destructive,
            },
        };
        return await ShowDialogAsync<bool?>("confirm", window, owner).ConfigureAwait(true) ?? false;
    }

    public async Task<string?> PromptAsync(string title, string label, string initial = "",
        Func<string, string?>? validate = null)
    {
        if (!TryPrepareDialog("prompt", out var owner))
            return null;

        var window = new TextPromptWindow
        {
            DataContext = new PromptDialogViewModel(title, label, initial, validate),
        };
        return await ShowDialogAsync<string>("prompt", window, owner).ConfigureAwait(true);
    }

    public async Task ShowPropertiesAsync(FileEntry entry, IFileSystem fileSystem)
    {
        if (!TryPrepareDialog("properties", out var owner))
            return;

        var viewModel = new PropertiesDialogViewModel(entry, fileSystem);
        var window = new PropertiesWindow { DataContext = viewModel };
        await ShowDialogAsync("properties", window, owner).ConfigureAwait(true);
    }

    public async Task ShowChecksumAsync(IReadOnlyList<OperationSource> sources)
    {
        if (!TryPrepareDialog("checksum", out var owner))
            return;

        var services = App.Services;
        if (services is null || sources.Count == 0)
            return;

        var viewModel = new ChecksumDialogViewModel(sources, services.Clipboard);
        var window = new ChecksumWindow { DataContext = viewModel };
        await ShowDialogAsync("checksum", window, owner).ConfigureAwait(true);
    }

    public async Task<OpenWithChoice?> ShowOpenWithAsync(FileEntry entry,
        IReadOnlyList<DesktopApplication> applications, string? currentDefaultId)
    {
        if (!TryPrepareDialog("openwith", out var owner))
            return null;

        var viewModel = new OpenWithDialogViewModel(entry, applications, currentDefaultId);
        var window = new OpenWithWindow { DataContext = viewModel };
        return await ShowDialogAsync<OpenWithChoice>("openwith", window, owner).ConfigureAwait(true);
    }

    public async Task<BatchRenamePlan?> ShowBatchRenameAsync(BatchRenameRequest request)
    {
        if (!TryPrepareDialog("batchrename", out var owner))
            return null;

        var viewModel = new BatchRenameDialogViewModel(request);
        var window = new BatchRenameWindow { DataContext = viewModel };
        return await ShowDialogAsync<BatchRenamePlan>("batchrename", window, owner).ConfigureAwait(true);
    }

    public async Task<AppSettings?> ShowSettingsAsync(AppSettings current)
    {
        if (!TryPrepareDialog("settings", out var owner))
            return null;

        var viewModel = new SettingsDialogViewModel(current, App.Services?.FileSystems, App.Services?.Terminals);
        var window = new SettingsWindow { DataContext = viewModel };
        return await ShowDialogAsync<AppSettings>("settings", window, owner).ConfigureAwait(true);
    }

    public async Task ShowAboutAsync()
    {
        if (!TryPrepareDialog("about", out var owner))
            return;

        var window = new AboutWindow { DataContext = new AboutDialogViewModel() };
        await ShowDialogAsync("about", window, owner).ConfigureAwait(true);
    }

    public async Task<ConflictChoice> ResolveConflictAsync(ConflictInfo info, CancellationToken cancellationToken = default)
    {
        // Concurrent operations may need a conflict decision at the same
        // time. Serialize them instead of stacking modal dialogs.
        await _conflictGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!TryPrepareDialog("conflict", out var owner))
                return ConflictChoice.Rename;

            var viewModel = new ConflictDialogViewModel
            {
                Name = info.Name,
                SourcePath = info.SourcePath,
                DestinationPath = info.DestinationPath,
                SourceIsDirectory = info.SourceIsDirectory,
                DestinationIsDirectory = info.DestinationIsDirectory,
                SourceSizeText = info.SourceIsDirectory ? L10n.T("文件夹") : Humanize.Size(info.SourceSize),
                DestinationSizeText = info.DestinationIsDirectory ? L10n.T("文件夹")
                    : info.DestinationSize is { } size ? Humanize.Size(size) : L10n.T("未知"),
                SourceKindText = info.SourceIsDirectory ? L10n.T("文件夹") : L10n.T("文件"),
                DestinationKindText = info.DestinationIsDirectory ? L10n.T("文件夹") : L10n.T("文件"),
            };

            var window = new ConflictWindow { DataContext = viewModel };
            var result = await ShowDialogAsync<ConflictChoice?>("conflict", window, owner).ConfigureAwait(true);
            return result ?? ConflictChoice.Cancel;
        }
        finally
        {
            _conflictGate.Release();
        }
    }

    public async Task ShowDiskUsageAsync(string path)
    {
        if (!TryPrepareDialog("diskusage", out var owner))
            return;

        var viewModel = new DiskUsageDialogViewModel(path);
        var window = new DiskUsageWindow { DataContext = viewModel };
        await ShowDialogAsync("diskusage", window, owner).ConfigureAwait(true);
    }

    public async Task ShowDuplicateFinderAsync(string path)
    {
        if (!TryPrepareDialog("duplicates", out var owner))
            return;

        var services = App.Services;
        if (services is null)
            return;

        var viewModel = new DuplicateFinderDialogViewModel(path, services.Dialogs, services.Trash);
        var window = new DuplicateFinderWindow { DataContext = viewModel };
        await ShowDialogAsync("duplicates", window, owner).ConfigureAwait(true);
    }

    public async Task<string?> ShowGitStatusAsync(string path)
    {
        if (!TryPrepareDialog("gitstatus", out var owner))
            return null;

        var viewModel = new GitStatusDialogViewModel(path);
        var window = new GitStatusWindow { DataContext = viewModel };
        return await ShowDialogAsync<string>("gitstatus", window, owner).ConfigureAwait(true);
    }

    public async Task<ConflictChoice?> ShowTransferPreviewAsync(TransferPreviewRequest request)
    {
        if (!TryPrepareDialog("transferpreview", out var owner))
            return null;

        var viewModel = new TransferPreviewDialogViewModel(request);
        var window = new TransferPreviewWindow { DataContext = viewModel };
        return await ShowDialogAsync<ConflictChoice?>("transferpreview", window, owner).ConfigureAwait(true);
    }

    public async Task ShowOperationHistoryAsync(FileOperationService operations)
    {
        if (!TryPrepareDialog("history", out var owner))
            return;

        var window = new OperationHistoryWindow
        {
            DataContext = new OperationHistoryDialogViewModel(operations),
        };
        await ShowDialogAsync("history", window, owner).ConfigureAwait(true);
    }

    public async Task<string?> PickFolderAsync(string title, string? initialPath = null)
    {
        var owner = _ownerProvider();
        if (owner?.StorageProvider is not { } storage)
            return null;

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            try { start = await storage.TryGetFolderFromPathAsync(initialPath); }
            catch { /* ignore invalid start folder */ }
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        }).ConfigureAwait(true);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task ShowFileCompareAsync(IFileSystem fileSystem, FileEntry left, FileEntry right)
    {
        if (!TryPrepareDialog("compare", out var owner))
            return;

        var window = new FileCompareWindow
        {
            DataContext = new FileCompareDialogViewModel(fileSystem, left, right),
        };
        await ShowDialogAsync("compare", window, owner).ConfigureAwait(true);
    }

    public async Task ShowUndoHistoryAsync(UndoService undo)
    {
        if (!TryPrepareDialog("undohistory", out var owner))
            return;

        var window = new UndoHistoryWindow { DataContext = new UndoHistoryDialogViewModel(undo) };
        await ShowDialogAsync("undohistory", window, owner).ConfigureAwait(true);
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var kind = "error:" + title;
        if (!TryPrepareDialog(kind, out var owner))
        {
            Log.Warn($"已有同类错误对话框或没有可用窗口，忽略新的错误: {title} - {message}");
            return;
        }

        var window = new ConfirmWindow
        {
            DataContext = new ConfirmDialogViewModel
            {
                TitleText = title,
                Message = message,
                ConfirmText = L10n.T("关闭"),
                CancelText = L10n.T("关闭"),
                ShowCancel = false,
                Destructive = true,
            },
        };
        await ShowDialogAsync<bool?>(kind, window, owner).ConfigureAwait(true);
    }

    private bool TryPrepareDialog(string kind, out Window owner)
    {
        owner = null!;
        var root = _ownerProvider();
        if (root is null)
            return false;

        if (TryActivateOpen(kind, root))
            return false;

        owner = ResolveOwner(root);
        return true;
    }

    private Window ResolveOwner(Window root)
    {
        // Keep nested dialogs above the dialog that requested them, but only
        // consider dialogs owned by this main window.
        for (var i = _openDialogs.Count - 1; i >= 0; i--)
        {
            var dialog = _openDialogs[i].Window;
            if (dialog.IsVisible && IsOwnedBy(dialog, root))
                return dialog;
        }

        return root;
    }

    private static bool IsOwnedBy(Window dialog, Window root)
    {
        for (var current = dialog.Owner; current is not null; current = current.Owner)
        {
            if (ReferenceEquals(current, root))
                return true;
        }

        return false;
    }

    private bool TryActivateOpen(string kind, Window root)
    {
        for (var i = _openDialogs.Count - 1; i >= 0; i--)
        {
            var entry = _openDialogs[i];
            if (entry.Kind == kind && entry.Window.IsVisible && IsOwnedBy(entry.Window, root))
            {
                entry.Window.Activate();
                return true;
            }
        }

        return false;
    }

    private async Task<TResult?> ShowDialogAsync<TResult>(string kind, Window dialog, Window owner)
    {
        _openDialogs.Add((kind, dialog));
        try
        {
            return await dialog.ShowDialog<TResult?>(owner).ConfigureAwait(true);
        }
        finally
        {
            RemoveDialog(dialog);
        }
    }

    private async Task ShowDialogAsync(string kind, Window dialog, Window owner)
    {
        _openDialogs.Add((kind, dialog));
        try
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        finally
        {
            RemoveDialog(dialog);
        }
    }

    private void RemoveDialog(Window dialog)
    {
        for (var i = _openDialogs.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_openDialogs[i].Window, dialog))
                _openDialogs.RemoveAt(i);
        }
    }
}
