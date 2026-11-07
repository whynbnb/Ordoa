using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.ViewModels;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed class GitFileItemViewModel
{
    public GitFileItemViewModel(GitFileChange change)
    {
        Change = change;
        StatusLabel = change.IsSubmodule ? "S" : string.IsNullOrWhiteSpace(change.Code) ? "M" : change.Code;
        StatusText = change.Description;
        LabelBrush = change.IsSubmodule ? GitBrushes.Submodule : change.Kind switch
        {
            GitChangeKind.Untracked or GitChangeKind.Added => GitBrushes.Added,
            GitChangeKind.Deleted or GitChangeKind.Conflicted => GitBrushes.Deleted,
            GitChangeKind.Renamed or GitChangeKind.Copied => GitBrushes.Renamed,
            GitChangeKind.TypeChanged => GitBrushes.Renamed,
            _ => GitBrushes.Modified,
        };
    }

    public GitFileChange Change { get; }
    public string StatusLabel { get; }
    public string StatusText { get; }
    public IBrush LabelBrush { get; }
    public string RelativePath => Change.RelativePath;
    public string FullPath => Change.FullPath;
    public string StageText => Change.IsUntracked
        ? L10n.T("未跟踪")
        : Change.IsStaged
            ? L10n.T("已暂存")
            : Change.IsUnstaged ? L10n.T("未暂存") : string.Empty;
}

public sealed partial class GitStatusDialogViewModel : ObservableObject, IDisposable
{
    private readonly string _path;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public GitStatusDialogViewModel(string path)
    {
        _path = path;
        StatusText = L10n.T("正在读取 Git 状态…");
    }

    public ObservableCollection<GitFileItemViewModel> Files { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isRepository;

    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _repositoryText = string.Empty;

    [ObservableProperty]
    private string _branchText = string.Empty;

    [ObservableProperty]
    private string _countsText = string.Empty;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private GitFileItemViewModel? _selectedFile;

    public string? SelectedPath { get; private set; }
    public bool ShowNoRepository => !IsLoading && !IsRepository;
    public bool ShowClean => !IsLoading && IsRepository && !HasChanges;

    partial void OnIsRepositoryChanged(bool value) => OnPropertyChanged(nameof(ShowNoRepository));

    partial void OnHasChangesChanged(bool value) => OnPropertyChanged(nameof(ShowClean));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoRepository));
        OnPropertyChanged(nameof(ShowClean));
    }

    public async Task StartAsync()
    {
        if (Files.Count > 0 || IsRepository)
            return;
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        if (IsLoading)
            return;

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsLoading = true;
        StatusText = L10n.T("正在读取 Git 状态…");
        try
        {
            var status = await GitService.GetStatusAsync(_path, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed)
                return;

            IsRepository = status is not null;
            HasChanges = status is { IsClean: false };
            Files.Clear();
            if (status is null)
            {
                SummaryText = L10n.T("当前文件夹不在 Git 仓库中");
                RepositoryText = _path;
                BranchText = string.Empty;
                CountsText = string.Empty;
                StatusText = L10n.T("未找到 .git 目录");
                UpdatedText = string.Empty;
                return;
            }

            foreach (var file in status.Files)
                Files.Add(new GitFileItemViewModel(file));
            SelectedFile = Files.FirstOrDefault();

            var branch = status.BranchLabel;
            if (!string.IsNullOrEmpty(status.Upstream))
                branch += $" → {status.Upstream}";
            if (status.Ahead > 0 || status.Behind > 0)
                branch += $"  ↑{status.Ahead} ↓{status.Behind}";

            SummaryText = status.IsClean
                ? L10n.T("工作区干净，没有未提交的变更")
                : L10n.Format("有 {0} 个未提交的变更", status.ChangedCount);
            RepositoryText = status.Root;
            BranchText = branch;
            CountsText = L10n.Format("已暂存 {0} · 未暂存 {1} · 未跟踪 {2} · 冲突 {3}",
                status.StagedCount, status.UnstagedCount, status.UntrackedCount,
                status.ConflictedCount);
            StatusText = status.IsClean ? L10n.T("没有未提交的变更") : L10n.T("以下项目尚未提交");
            UpdatedText = L10n.Format("更新于 {0}", DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (OperationCanceledException)
        {
            // A newer refresh superseded this request.
        }
        catch (Exception ex)
        {
            IsRepository = false;
            HasChanges = false;
            Files.Clear();
            StatusText = ex.Message;
            SummaryText = L10n.T("无法读取 Git 状态");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                IsLoading = false;
            }
            cts.Dispose();
        }
    }

    public void RevealSelected()
    {
        if (SelectedFile is not null)
            SelectedPath = SelectedFile.FullPath;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
