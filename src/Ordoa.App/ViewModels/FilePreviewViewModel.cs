using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.Localization;
using Ordoa.Core.Util;

using Ordoa.Core.Services;

namespace Ordoa.App.ViewModels;

/// <summary>Preview state shared by the Space-bar overlay and the persistent panel.</summary>
public sealed partial class FilePreviewViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _pdfCts;
    private int _loadVersion;
    private string? _pdfLocalPath;
    private PaneViewModel? _pane;
    private FileItemViewModel? _item;
    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux()
        ? StringComparer.Ordinal
        : StringComparer.OrdinalIgnoreCase;

    public FilePreviewViewModel(AppServices services)
    {
        _services = services;
    }

    public PaneViewModel? Pane => _pane;
    public FileItemViewModel? Item => _item;

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _pathText = string.Empty;

    [ObservableProperty]
    private Geometry? _icon;

    [ObservableProperty]
    private IBrush? _iconBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindLabel))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private PreviewKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowPlainImage))]
    private Bitmap? _image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private string? _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private IReadOnlyList<PreviewRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarkdown))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private IReadOnlyList<MarkdownBlock> _markdownBlocks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private CodePreview? _code;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCsv))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private CsvTable? _csv;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPdf))]
    [NotifyPropertyChangedFor(nameof(HasPdfControls))]
    [NotifyPropertyChangedFor(nameof(CanPreviousPdfPage))]
    [NotifyPropertyChangedFor(nameof(CanNextPdfPage))]
    [NotifyPropertyChangedFor(nameof(PdfPageText))]
    [NotifyPropertyChangedFor(nameof(ShowPlainImage))]
    private int _pdfPageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviousPdfPage))]
    [NotifyPropertyChangedFor(nameof(CanNextPdfPage))]
    [NotifyPropertyChangedFor(nameof(PdfPageText))]
    private int _pdfPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPdfControls))]
    [NotifyPropertyChangedFor(nameof(CanPreviousPdfPage))]
    [NotifyPropertyChangedFor(nameof(CanNextPdfPage))]
    private bool _isPdfLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PdfImageWidth))]
    private int _pdfZoom = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    private IReadOnlyList<PreviewDetail> _details = [];

    public bool HasDetails => Details.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitInfo))]
    [NotifyPropertyChangedFor(nameof(GitRepositoryName))]
    [NotifyPropertyChangedFor(nameof(GitBranchText))]
    [NotifyPropertyChangedFor(nameof(GitStatusSummary))]
    [NotifyPropertyChangedFor(nameof(GitChangeText))]
    [NotifyPropertyChangedFor(nameof(GitChangeCode))]
    [NotifyPropertyChangedFor(nameof(GitChangeBrush))]
    [NotifyPropertyChangedFor(nameof(GitStatusBrush))]
    [NotifyPropertyChangedFor(nameof(HasGitChangeText))]
    [NotifyPropertyChangedFor(nameof(HasGitCommit))]
    [NotifyPropertyChangedFor(nameof(GitCommitMeta))]
    [NotifyPropertyChangedFor(nameof(GitCommitSubject))]
    private GitPreviewInfo? _gitInfo;

    public bool HasGitInfo => GitInfo is not null;
    public string GitRepositoryName => GitInfo?.RepositoryName ?? string.Empty;
    public string GitBranchText => GitInfo?.BranchText ?? string.Empty;
    public string GitStatusSummary => GitInfo?.StatusSummary ?? string.Empty;
    public string GitChangeText => GitInfo?.ChangeText ?? string.Empty;
    public string GitChangeCode => GitInfo?.ChangeCode ?? string.Empty;
    public IBrush? GitChangeBrush => GitInfo?.ChangeBrush;
    public IBrush? GitStatusBrush => GitInfo is null
        ? null
        : GitInfo.IsClean ? GitBrushes.Added : GitBrushes.Modified;
    public bool HasGitChangeText => !string.IsNullOrEmpty(GitInfo?.ChangeText);
    public bool HasGitCommit => !string.IsNullOrWhiteSpace(GitInfo?.CommitMeta);
    public string GitCommitMeta => GitInfo?.CommitMeta ?? string.Empty;
    public string GitCommitSubject => GitInfo?.CommitSubject ?? string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _isLoading;

    public bool HasImage => Image is not null;
    public bool ShowPlainImage => HasImage && !HasPdf;
    public bool HasText => !string.IsNullOrEmpty(Text);
    public bool HasRows => Rows.Count > 0;
    public bool HasMarkdown => MarkdownBlocks.Count > 0;
    public bool HasCode => Code is { Lines.Count: > 0 };
    public bool HasCsv => Csv is { Headers.Count: > 0 };
    public bool HasPdf => PdfPageCount > 0;
    public bool HasPdfControls => HasPdf && !IsPdfLoading;
    public bool CanPreviousPdfPage => HasPdf && PdfPage > 1 && !IsPdfLoading;
    public bool CanNextPdfPage => HasPdf && PdfPage < PdfPageCount && !IsPdfLoading;
    public string PdfPageText => HasPdf ? $"{PdfPage} / {PdfPageCount}" : string.Empty;
    public double PdfImageWidth => 760d * Math.Clamp(PdfZoom, 50, 300) / 100d;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool ShowPlaceholder => !HasImage && !HasText && !HasRows && !HasMarkdown && !HasCode &&
                                   !HasCsv && !IsLoading && !HasError;

    public string KindLabel => Kind switch
    {
        PreviewKind.Image => L10n.T("图片"),
        PreviewKind.Text => L10n.T("文本"),
        PreviewKind.Markdown => L10n.T("Markdown"),
        PreviewKind.Code => L10n.T("代码"),
        PreviewKind.Table => L10n.T("表格"),
        PreviewKind.Video => L10n.T("视频"),
        PreviewKind.Audio => L10n.T("音频"),
        PreviewKind.Archive => L10n.T("压缩包"),
        PreviewKind.Folder => L10n.T("文件夹"),
        PreviewKind.Document => L10n.T("文档"),
        PreviewKind.Info => L10n.T("文件信息"),
        _ => L10n.T("正在预览"),
    };

    public async Task OpenAsync(PaneViewModel pane, FileItemViewModel item)
    {
        _pane = pane;
        _item = item;
        IsOpen = true;
        Title = item.Name;
        PathText = pane.FileSystem.GetDisplayPath(item.Path);
        Icon = item.Icon;
        IconBrush = item.IconBrush;
        Kind = PreviewKind.Loading;
        Subtitle = BuildSubtitle(item);
        await ReloadAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        if (IsOpen && _pane is not null && _item is not null)
            await ReloadAsync().ConfigureAwait(true);
    }

    public void Close()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _pdfCts?.Cancel();
        _pdfCts?.Dispose();
        _pdfCts = null;
        _pdfLocalPath = null;
        _loadVersion++;

        IsOpen = false;
        _pane = null;
        _item = null;
        Title = string.Empty;
        Subtitle = string.Empty;
        PathText = string.Empty;
        Icon = null;
        IconBrush = null;
        Kind = PreviewKind.Loading;
        ReplaceImage(null);
        Text = null;
        Rows = [];
        MarkdownBlocks = [];
        Code = null;
        Csv = null;
        PdfPageCount = 0;
        PdfPage = 0;
        PdfZoom = 100;
        IsPdfLoading = false;
        Details = [];
        GitInfo = null;
        Notice = null;
        Error = null;
        IsLoading = false;
    }

    private async Task ReloadAsync()
    {
        if (_pane is null || _item is null)
            return;

        var pane = _pane;
        var item = _item;
        var version = ++_loadVersion;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var cancellationToken = _loadCts.Token;

        IsLoading = true;
        Error = null;
        Notice = null;
        Kind = PreviewKind.Loading;
        ReplaceImage(null);
        Text = null;
        Rows = [];
        MarkdownBlocks = [];
        Code = null;
        Csv = null;
        PdfPageCount = 0;
        PdfPage = 0;
        PdfZoom = 100;
        _pdfLocalPath = null;
        _pdfCts?.Cancel();
        _pdfCts?.Dispose();
        _pdfCts = null;
        IsPdfLoading = false;
        Details = [];
        GitInfo = null;
        Title = item.Name;
        PathText = pane.FileSystem.GetDisplayPath(item.Path);
        Icon = item.Icon;
        IconBrush = item.IconBrush;
        Subtitle = BuildSubtitle(item);

        try
        {
            var content = await _services.Previews
                .LoadAsync(pane.FileSystem, item.Entry, _services.Settings.Current.FfmpegPath, cancellationToken)
                .ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested || version != _loadVersion)
            {
                content.Image?.Dispose();
                return;
            }

            Kind = content.Kind;
            ReplaceImage(content.Image);
            Text = content.Text;
            Rows = content.Rows ?? [];
            MarkdownBlocks = content.Markdown ?? [];
            Code = content.Code;
            Csv = content.Table;
            _pdfLocalPath = content.PdfLocalPath;
            PdfPageCount = content.PdfPageCount;
            PdfPage = content.PdfPage;
            Details = content.Details ?? [];
            Notice = content.Notice;
            Subtitle = BuildSubtitle(item);
            _ = LoadGitInfoAsync(pane, item, version, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer selection or a closed overlay superseded this load.
        }
        catch (Exception ex)
        {
            if (version != _loadVersion)
                return;

            Error = ex.Message;
            Kind = PreviewKind.Info;
            Notice = L10n.T("无法生成预览。");
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    public async Task PreviousPdfPageAsync()
    {
        if (CanPreviousPdfPage)
            await LoadPdfPageAsync(PdfPage - 1).ConfigureAwait(true);
    }

    public async Task NextPdfPageAsync()
    {
        if (CanNextPdfPage)
            await LoadPdfPageAsync(PdfPage + 1).ConfigureAwait(true);
    }

    public async Task SetPdfZoomAsync(int zoom)
    {
        zoom = Math.Clamp(zoom, 50, 300);
        if (zoom == PdfZoom)
            return;

        PdfZoom = zoom;
        if (HasPdf)
            await LoadPdfPageAsync(PdfPage).ConfigureAwait(true);
    }

    private async Task LoadPdfPageAsync(int page)
    {
        if (_pdfLocalPath is null || page < 1 || page > PdfPageCount)
            return;

        _pdfCts?.Cancel();
        _pdfCts?.Dispose();
        _pdfCts = new CancellationTokenSource();
        var cancellationToken = _pdfCts.Token;
        var version = _loadVersion;

        IsPdfLoading = true;
        Notice = null;
        try
        {
            var bitmap = await _services.Previews
                .RenderPdfPageAsync(_pdfLocalPath, page, PdfZoom, cancellationToken)
                .ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || version != _loadVersion)
            {
                bitmap?.Dispose();
                return;
            }

            if (bitmap is null)
            {
                Notice = L10n.T("无法渲染此页；安装 poppler-utils（pdftoppm）后可预览。");
                return;
            }

            ReplaceImage(bitmap);
            PdfPage = page;
        }
        catch (OperationCanceledException)
        {
            // A newer page or selection superseded this render.
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                Notice = ex.Message;
        }
        finally
        {
            if (version == _loadVersion)
                IsPdfLoading = false;
        }
    }

    private async Task LoadGitInfoAsync(PaneViewModel pane, FileItemViewModel item, int version,
        CancellationToken cancellationToken)
    {
        var settings = _services.Settings.Current;
        if (!settings.EnableGitIntegration || !settings.ShowGitPreview || !pane.CanShowGitStatus)
            return;

        try
        {
            var root = GitService.FindRepository(item.Path);
            if (root is null)
                return;

            var status = pane.GitStatus;
            if (item.IsDirectory || status is null || !PathComparer.Equals(
                    Path.TrimEndingDirectorySeparator(status.Root), root))
            {
                status = await GitService.GetStatusAsync(item.Path, cancellationToken)
                    .ConfigureAwait(true);
            }
            if (status is null)
                return;
            if (cancellationToken.IsCancellationRequested || version != _loadVersion)
                return;

            if (item.IsDirectory && Kind == PreviewKind.Folder)
                ApplyGitRows(status);

            var commit = await GitService.GetLastCommitAsync(item.Path, cancellationToken)
                .ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || version != _loadVersion)
                return;

            GitFileChange? change = null;
            var itemPath = Path.TrimEndingDirectorySeparator(item.Path);
            status.FilesByPath.TryGetValue(itemPath, out change);
            GitInfo = BuildGitInfo(status, change, commit, item);
        }
        catch (OperationCanceledException)
        {
            // A newer selection or a closed preview superseded this request.
        }
        catch (Exception ex)
        {
            Log.Warn("读取预览 Git 信息失败", ex);
        }
    }

    private void ApplyGitRows(GitRepositoryStatus status)
    {
        if (Rows.Count == 0)
            return;

        var settings = _services.Settings.Current;
        var updated = new List<PreviewRow>(Rows.Count);
        foreach (var row in Rows)
            updated.Add(ApplyGitRow(row, status, settings));
        Rows = updated;
    }

    private static PreviewRow ApplyGitRow(PreviewRow row, GitRepositoryStatus status, AppSettings settings)
    {
        if (string.IsNullOrEmpty(row.FullPath))
            return row;

        var path = Path.TrimEndingDirectorySeparator(row.FullPath);
        status.FilesByPath.TryGetValue(path, out var change);
        var ignored = status.IgnoredPaths.Contains(path) ||
                      status.IgnoredDirectories.Any(directory => IsUnder(path, directory));
        var submodule = status.SubmodulePaths.Contains(path) || change?.IsSubmodule == true;
        var repository = row.IsDirectory && HasGitMarker(path);
        var dirtyDirectory = row.IsDirectory && status.ChangedDirectories.Contains(path);
        var trackedClean = change is null && !ignored && !submodule &&
                           (row.IsDirectory
                               ? status.TrackedDirectories.Contains(path) && !dirtyDirectory
                               : status.TrackedPaths.Contains(path));

        var tooltip = new List<string>();
        if (submodule && settings.ShowGitSubmoduleBadges)
            tooltip.Add(L10n.T("Git 子模块"));
        if (repository && settings.ShowGitRepositoryBadges)
            tooltip.Add(L10n.T("Git 仓库"));
        if (change is not null)
            tooltip.Add(change.Description);
        if (ignored)
            tooltip.Add(L10n.T("已被 Git 忽略"));
        if (trackedClean && settings.ShowGitCleanCheck)
            tooltip.Add(L10n.T("已跟踪，无变更"));
        if (dirtyDirectory)
            tooltip.Add(L10n.T("目录内含未提交变更"));

        string? text = null;
        IBrush? brush = null;
        if (settings.ShowGitFileBadges)
        {
            if (submodule && settings.ShowGitSubmoduleBadges)
            {
                text = "S";
                brush = change is not null ? ChangeBrush(change) : GitBrushes.Submodule;
            }
            else if (repository && settings.ShowGitRepositoryBadges)
            {
                text = "GIT";
                brush = change is not null ? ChangeBrush(change) : GitBrushes.Repository;
            }
            else if (change is not null)
            {
                text = change.Code;
                brush = ChangeBrush(change);
            }
            else if (ignored)
            {
                text = "I";
                brush = GitBrushes.Ignored;
            }
            else if (trackedClean && settings.ShowGitCleanCheck)
            {
                text = "✓";
                brush = GitBrushes.Added;
            }
            else if (dirtyDirectory)
            {
                text = "●";
                brush = GitBrushes.Modified;
            }
        }

        return row with
        {
            GitBadgeText = string.IsNullOrEmpty(text) ? null : text,
            GitBadgeBrush = brush,
            GitTooltip = tooltip.Count == 0 ? null : string.Join(Environment.NewLine, tooltip),
            Opacity = ignored && settings.DimGitIgnored ? 0.55 : 1d,
        };
    }

    private static bool HasGitMarker(string path)
        => Directory.Exists(Path.Combine(path, ".git")) ||
           File.Exists(Path.Combine(path, ".git"));

    private static GitPreviewInfo BuildGitInfo(GitRepositoryStatus status, GitFileChange? change,
        GitCommitInfo? commit, FileItemViewModel item)
    {
        var itemPath = Path.TrimEndingDirectorySeparator(item.Path);
        var ignored = status.IgnoredPaths.Contains(itemPath) ||
                      status.IgnoredDirectories.Any(directory => IsUnder(itemPath, directory));
        var dirtyDirectory = item.IsDirectory && status.ChangedDirectories.Contains(itemPath);

        var changeText = change is not null
            ? L10n.Format("当前项：{0}", change.Description)
            : ignored
                ? L10n.T("当前项：已被 Git 忽略")
                : dirtyDirectory
                    ? L10n.T("当前项：目录内含未提交变更")
                    : L10n.T("当前项：无变更");

        var code = change?.Code;
        if (string.IsNullOrEmpty(code))
            code = ignored ? "I" : dirtyDirectory ? "●" : "✓";

        var changeBrush = change is not null
            ? ChangeBrush(change)
            : ignored
                ? GitBrushes.Ignored
                : dirtyDirectory ? GitBrushes.Modified : GitBrushes.Added;

        var branch = status.BranchLabel;
        if (status.Ahead > 0)
            branch += $" ↑{status.Ahead}";
        if (status.Behind > 0)
            branch += $" ↓{status.Behind}";

        var summary = status.IsClean
            ? L10n.T("工作区干净")
            : L10n.Format("{0} 个变更 · 已暂存 {1} · 未暂存 {2} · 未跟踪 {3}",
                status.ChangedCount, status.StagedCount, status.UnstagedCount,
                status.UntrackedCount);
        if (status.ConflictedCount > 0)
            summary += " · " + L10n.Format("冲突 {0}", status.ConflictedCount);

        var repository = Path.GetFileName(Path.TrimEndingDirectorySeparator(status.Root));
        if (string.IsNullOrEmpty(repository))
            repository = status.Root;

        return new GitPreviewInfo(
            RepositoryName: repository,
            BranchText: branch,
            StatusSummary: summary,
            ChangeText: changeText,
            ChangeCode: code,
            IsClean: status.IsClean,
            ChangeBrush: changeBrush,
            CommitMeta: commit is null
                ? null
                : $"{commit.Hash} · {commit.Author} · {Humanize.RelativeTime(commit.Date)}",
            CommitSubject: commit?.Subject);
    }

    private static IBrush ChangeBrush(GitFileChange change) => change.Kind switch
    {
        GitChangeKind.Untracked or GitChangeKind.Added => GitBrushes.Added,
        GitChangeKind.Deleted or GitChangeKind.Conflicted => GitBrushes.Deleted,
        GitChangeKind.Renamed or GitChangeKind.Copied => GitBrushes.Renamed,
        GitChangeKind.TypeChanged => GitBrushes.Renamed,
        _ => GitBrushes.Modified,
    };

    private static bool IsUnder(string path, string directory)
        => path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private string BuildSubtitle(FileItemViewModel item)
    {
        var kind = Kind switch
        {
            PreviewKind.Loading => L10n.T("正在读取…"),
            _ => KindLabel,
        };
        var size = item.IsDirectory ? null : Humanize.Size(item.Entry.Size);
        var selected = _pane?.SelectedCount > 1
            ? L10n.Format("已选择 {0} 项，预览第一项", _pane.SelectedCount)
            : null;
        var subtitle = size is null ? kind : $"{kind} · {size}";
        return string.IsNullOrWhiteSpace(selected) ? subtitle : $"{subtitle} · {selected}";
    }

    private void ReplaceImage(Bitmap? next)
    {
        var previous = Image;
        Image = next;
        if (previous is not null && !ReferenceEquals(previous, next))
        {
            // Let the Image control drop its reference before the native bitmap is freed.
            Dispatcher.UIThread.Post(() =>
            {
                try { previous.Dispose(); } catch { /* ignore */ }
            }, DispatcherPriority.Background);
        }
    }

    public void Dispose()
    {
        Close();
    }

/// <summary>Resolved Git details shown by the preview Git card.</summary>
public sealed record GitPreviewInfo(
    string RepositoryName,
    string BranchText,
    string StatusSummary,
    string ChangeText,
    string ChangeCode,
    bool IsClean,
    IBrush ChangeBrush,
    string? CommitMeta,
    string? CommitSubject);
}
