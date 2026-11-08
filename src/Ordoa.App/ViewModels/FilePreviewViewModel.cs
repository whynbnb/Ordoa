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
}
