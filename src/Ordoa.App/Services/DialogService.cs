using Avalonia.Controls;
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

    public DialogService(Func<Window?> ownerProvider)
    {
        _ownerProvider = ownerProvider;
    }

    public async Task<ConnectionDialogResult?> ShowConnectionAsync(ConnectionProfile? preset = null)
    {
        var owner = _ownerProvider();
        var services = App.Services;
        if (owner is null || services is null)
            return null;

        string? password = null;
        if (preset is not null && !preset.Anonymous)
        {
            try { password = await services.FileSystems.Credentials.GetPasswordAsync(preset.Id); }
            catch { /* ignore */ }
        }

        var viewModel = new ConnectionDialogViewModel(preset, password, services.FileSystems);
        var window = new ConnectionWindow { DataContext = viewModel };
        return await window.ShowDialog<ConnectionDialogResult?>(owner);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, bool destructive = false)
    {
        var owner = _ownerProvider();
        if (owner is null)
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
        return await window.ShowDialog<bool>(owner);
    }

    public async Task<string?> PromptAsync(string title, string label, string initial = "",
        Func<string, string?>? validate = null)
    {
        var owner = _ownerProvider();
        if (owner is null)
            return null;

        var window = new TextPromptWindow
        {
            DataContext = new PromptDialogViewModel(title, label, initial, validate),
        };
        return await window.ShowDialog<string?>(owner);
    }

    public async Task ShowPropertiesAsync(FileEntry entry, IFileSystem fileSystem)
    {
        var owner = _ownerProvider();
        if (owner is null)
            return;

        var viewModel = new PropertiesDialogViewModel(entry, fileSystem);
        var window = new PropertiesWindow { DataContext = viewModel };
        await window.ShowDialog(owner);
    }

    public async Task ShowChecksumAsync(IReadOnlyList<OperationSource> sources)
    {
        var owner = _ownerProvider();
        var services = App.Services;
        if (owner is null || services is null || sources.Count == 0)
            return;

        var viewModel = new ChecksumDialogViewModel(sources, services.Clipboard);
        var window = new ChecksumWindow { DataContext = viewModel };
        await window.ShowDialog(owner);
    }

    public async Task<AppSettings?> ShowSettingsAsync(AppSettings current)
    {
        var owner = _ownerProvider();
        if (owner is null)
            return null;

        var viewModel = new SettingsDialogViewModel(current);
        var window = new SettingsWindow { DataContext = viewModel };
        return await window.ShowDialog<AppSettings?>(owner);
    }

    public async Task ShowAboutAsync()
    {
        var owner = _ownerProvider();
        if (owner is null)
            return;

        var window = new AboutWindow { DataContext = new AboutDialogViewModel() };
        await window.ShowDialog(owner);
    }

    public async Task<ConflictChoice> ResolveConflictAsync(ConflictInfo info, CancellationToken cancellationToken = default)
    {
        var owner = _ownerProvider();
        if (owner is null)
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
        var result = await window.ShowDialog<ConflictChoice?>(owner);
        return result ?? ConflictChoice.Cancel;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var owner = _ownerProvider();
        if (owner is null)
            return;

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
        await window.ShowDialog<bool>(owner);
    }
}
