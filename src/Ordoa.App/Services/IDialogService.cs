using Ordoa.App.ViewModels.Dialogs;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

public sealed record ConnectionDialogResult(ConnectionProfile Profile, string Password, bool SaveProfile);

public sealed record TransferPreviewRequest(
    bool Move,
    int ItemCount,
    string Destination,
    int ConflictCount,
    IReadOnlyList<string> ConflictNames,
    int ScannedCount);

public interface IDialogService
{
    Task<ConnectionDialogResult?> ShowConnectionAsync(ConnectionProfile? preset = null);
    Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, bool destructive = false);
    Task<string?> PromptAsync(string title, string label, string initial = "", Func<string, string?>? validate = null);
    Task ShowPropertiesAsync(FileEntry entry, IFileSystem fileSystem);
    Task<bool> ShowBatchPermissionsAsync(IReadOnlyList<FileEntry> entries, IFileSystem fileSystem);
    Task ShowChecksumAsync(IReadOnlyList<OperationSource> sources);
    Task<BatchRenamePlan?> ShowBatchRenameAsync(BatchRenameRequest request);
    Task<OpenWithChoice?> ShowOpenWithAsync(FileEntry entry, IReadOnlyList<DesktopApplication> applications,
        string? currentDefaultId);
    Task<AppSettings?> ShowSettingsAsync(AppSettings current);
    Task ShowAboutAsync();
    Task<ConflictChoice> ResolveConflictAsync(ConflictInfo info, CancellationToken cancellationToken = default);
    Task ShowErrorAsync(string title, string message);
    Task ShowDiskUsageAsync(string path);
    Task ShowDuplicateFinderAsync(string path);
    Task<string?> ShowGitStatusAsync(string path);
    Task<ConflictChoice?> ShowTransferPreviewAsync(TransferPreviewRequest request);
    Task ShowOperationHistoryAsync(FileOperationService operations);
    Task<string?> PickFolderAsync(string title, string? initialPath = null);
    Task ShowFileCompareAsync(IFileSystem fileSystem, FileEntry left, FileEntry right);
    Task ShowFolderCompareAsync(IFileSystem leftFileSystem, string leftPath,
        IFileSystem rightFileSystem, string rightPath);
    Task ShowUndoHistoryAsync(UndoService undo);
    Task ShowImageConversionAsync(IReadOnlyList<string> paths, string outputDirectory);
    Task<IReadOnlyList<string>> PickFilesAsync(string title);
    Task<Ordoa.Core.Services.DiscoveredService?> ShowNetworkDiscoveryAsync();
}
