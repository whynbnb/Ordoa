using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ordoa.App.Services;
using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed record PropertyItem(string Label, string Value, bool IsMono = false);

public sealed partial class PropertiesDialogViewModel : ObservableObject, IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly CancellationTokenSource _cts = new();
    private UnixFileMode? _mode;

    public PropertiesDialogViewModel(FileEntry entry, IFileSystem fileSystem)
    {
        Entry = entry;
        _fileSystem = fileSystem;
        _mode = entry.UnixMode;
        _octalInput = entry.UnixMode is { } initialMode ? Humanize.OctalPermissions(initialMode) : string.Empty;
        var visual = FileIconCatalog.For(entry);
        Icon = visual.Geometry;
        IconBrush = visual.Brush;
        IsDirectory = entry.IsDirectory;
        IsTrash = fileSystem is TrashFileSystem;
        IsRemote = !fileSystem.IsLocal;
        CanMeasure = IsDirectory && fileSystem.Capabilities.Has(FileSystemCapabilities.List);
        HasUnixMode = entry.UnixMode is not null;
        HasLink = !string.IsNullOrWhiteSpace(entry.LinkTarget);
        HasOriginalPath = !string.IsNullOrWhiteSpace(entry.OriginalPath);
        HasOwner = !string.IsNullOrWhiteSpace(entry.Owner);
        HasGroup = !string.IsNullOrWhiteSpace(entry.Group);
        CanEditPermissions = fileSystem.Capabilities.Has(FileSystemCapabilities.UnixPermissions) && entry.UnixMode is not null;
        CanEditOwnership = fileSystem.IsLocal && !IsTrash && entry.UnixMode is not null;
        _ownerInput = entry.Owner ?? string.Empty;
        _groupInput = entry.Group ?? string.Empty;
        BuildItems();
    }

    public ObservableCollection<PropertyItem> Items { get; } = [];

    private void BuildItems()
    {
        Items.Add(new PropertyItem(L10n.T("类型"), KindText));
        Items.Add(new PropertyItem(L10n.T("位置"), LocationText));
        Items.Add(new PropertyItem(L10n.T("完整路径"), PathText, true));
        Items.Add(new PropertyItem(L10n.T("大小"), SizeText));
        if (!IsDirectory)
            Items.Add(new PropertyItem(L10n.T("精确大小"), ExactSizeText));
        Items.Add(new PropertyItem(L10n.T("修改时间"), ModifiedText));
        Items.Add(new PropertyItem(L10n.T("创建时间"), CreatedText));
        Items.Add(new PropertyItem(L10n.T("访问时间"), AccessedText));
        if (!IsDirectory)
            Items.Add(new PropertyItem(L10n.T("MIME 类型"), MimeText, true));
        if (HasUnixMode)
        {
            Items.Add(new PropertyItem(L10n.T("权限"), PermissionsText, true));
            Items.Add(new PropertyItem(L10n.T("八进制"), OctalText, true));
        }
        if (HasOwner)
            Items.Add(new PropertyItem(L10n.T("所有者"), OwnerText));
        if (HasGroup)
            Items.Add(new PropertyItem(L10n.T("用户组"), GroupText));
        if (HasLink)
            Items.Add(new PropertyItem(L10n.T("链接目标"), LinkTarget!, true));
        if (HasOriginalPath)
            Items.Add(new PropertyItem(L10n.T("原位置"), OriginalPath!, true));
        if (DeletedText is not null)
            Items.Add(new PropertyItem(L10n.T("删除时间"), DeletedText));
        Items.Add(new PropertyItem(L10n.T("文件系统"), FsLabel));
    }

    public FileEntry Entry { get; private set; }
    public Geometry Icon { get; }
    public IBrush IconBrush { get; }
    public string Name => Entry.Name;
    public string KindText => Entry.TypeDescription;
    public string PathText => _fileSystem.GetDisplayPath(Entry.Path);
    public string LocationText => _fileSystem.GetDisplayPath(_fileSystem.GetParent(Entry.Path));
    public string SizeText => Entry.IsDirectory ? "—" : Humanize.Size(Entry.Size);
    public string ExactSizeText => Entry.IsDirectory ? "—" : L10n.Format("{0:N0} 字节", Entry.Size);
    public string ModifiedText => Humanize.DateTimeFull(Entry.Modified);
    public string CreatedText => Humanize.DateTimeFull(Entry.Created);
    public string AccessedText => Humanize.DateTimeFull(Entry.Accessed);
    public string PermissionsText => _mode is { } mode ? Humanize.Permissions(mode) : "—";
    public string OctalText => _mode is { } mode ? Humanize.OctalPermissions(mode) : "—";
    public string OwnerText => Entry.Owner ?? "—";
    public string GroupText => Entry.Group ?? "—";
    public string? LinkTarget => Entry.LinkTarget;
    public string? OriginalPath => Entry.OriginalPath;
    public string? DeletedText => Entry.DeletedAt is null ? null : Humanize.DateTimeFull(Entry.DeletedAt);
    public bool IsDirectory { get; }
    public bool IsTrash { get; }
    public bool IsRemote { get; }
    public bool CanMeasure { get; }
    public bool CanEditPermissions { get; }
    public bool CanEditOwnership { get; }
    public ObservableCollection<PropertyItem> ExtendedItems { get; } = [];
    public bool HasUnixMode { get; }
    public bool HasLink { get; }
    public bool HasOriginalPath { get; }
    public bool HasOwner { get; }
    public bool HasGroup { get; }
    public string FsLabel => IsTrash ? L10n.T("回收站") : IsRemote ? L10n.T("远程") : L10n.T("本机");
    public string MimeText => Entry.MimeType ?? "—";

    private string _octalInput;
    public string OctalInput
    {
        get => _octalInput;
        set => SetProperty(ref _octalInput, value);
    }

    [ObservableProperty]
    private string? _permissionStatus;

    [ObservableProperty]
    private string _ownerInput = string.Empty;

    [ObservableProperty]
    private string _groupInput = string.Empty;

    [ObservableProperty]
    private string? _ownershipStatus;

    [ObservableProperty]
    private bool _isApplyingOwnership;

    [ObservableProperty]
    private bool _isLoadingMetadata;

    [ObservableProperty]
    private string? _aclText;

    public bool HasAcl => !string.IsNullOrWhiteSpace(AclText);
    public bool HasExtendedMetadata => ExtendedItems.Count > 0 || HasAcl;
    public bool ShowExtendedMetadata => !IsRemote && !IsTrash;
    public bool ShowNoExtendedMetadata => !IsLoadingMetadata && !HasExtendedMetadata;

    public async Task LoadMetadataAsync()
    {
        if (!_fileSystem.IsLocal || IsTrash)
            return;

        IsLoadingMetadata = true;
        try
        {
            var attributes = await Task.Run(() => ExtendedMetadata.ReadAttributes(Entry.Path)).ConfigureAwait(true);
            ExtendedItems.Clear();
            foreach (var attribute in attributes)
                ExtendedItems.Add(new PropertyItem(attribute.Name, attribute.Value, true));
            AclText = await ExtendedMetadata.ReadAclAsync(Entry.Path, _cts.Token).ConfigureAwait(true);
        }
        finally
        {
            IsLoadingMetadata = false;
            OnPropertyChanged(nameof(HasAcl));
            OnPropertyChanged(nameof(HasExtendedMetadata));
            OnPropertyChanged(nameof(ShowNoExtendedMetadata));
        }
    }

    [RelayCommand]
    private async Task ApplyOwnershipAsync()
    {
        if (!CanEditOwnership)
            return;

        IsApplyingOwnership = true;
        OwnershipStatus = null;
        try
        {
            await ExtendedMetadata.SetOwnershipAsync(Entry.Path, OwnerInput, GroupInput, _cts.Token)
                .ConfigureAwait(true);
            Entry = await _fileSystem.GetEntryAsync(Entry.Path, _cts.Token).ConfigureAwait(true) ?? Entry;
            OwnerInput = Entry.Owner ?? OwnerInput;
            GroupInput = Entry.Group ?? GroupInput;
            OwnershipStatus = L10n.T("所有者已更新");
            Items.Clear();
            BuildItems();
            OnPropertyChanged(nameof(OwnerText));
            OnPropertyChanged(nameof(GroupText));
        }
        catch (Exception ex)
        {
            OwnershipStatus = ex.Message;
        }
        finally
        {
            IsApplyingOwnership = false;
        }
    }

    [RelayCommand]
    private async Task ApplyPermissionsAsync()
    {
        if (!CanEditPermissions)
            return;

        var text = OctalInput.Trim();
        if (text.Length is < 3 or > 4 || text.Any(c => c is < '0' or > '7'))
        {
            PermissionStatus = L10n.T("请输入 3 或 4 位八进制数字，例如 755");
            return;
        }

        try
        {
            var bits = Convert.ToInt32(text, 8);
            _mode = (UnixFileMode)(bits & 0xFFF);
            await _fileSystem.SetUnixModeAsync(Entry.Path, _mode.Value, _cts.Token).ConfigureAwait(true);
            Entry = Entry with { UnixMode = _mode };
            PermissionStatus = L10n.T("权限已更新");
            OnPropertyChanged(nameof(PermissionsText));
            OnPropertyChanged(nameof(OctalText));
            Items.Clear();
            BuildItems();
        }
        catch (OperationCanceledException)
        {
            // Dialog closed.
        }
        catch (Exception ex)
        {
            PermissionStatus = L10n.T("更新失败: ") + ex.Message;
        }
    }

    [ObservableProperty]
    private bool _isMeasuring;

    [ObservableProperty]
    private string? _contentSummary;

    [ObservableProperty]
    private string? _measureError;

    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (!CanMeasure || IsMeasuring)
            return;

        IsMeasuring = true;
        MeasureError = null;
        try
        {
            var result = await ChecksumService.MeasureAsync(_fileSystem, Entry, _cts.Token).ConfigureAwait(true);
            ContentSummary = L10n.Format("{0:N0} 个文件 · {1:N0} 个文件夹 · {2}", result.Files, result.Directories, Humanize.Size(result.Bytes));
        }
        catch (OperationCanceledException)
        {
            // Dialog was closed.
        }
        catch (Exception ex)
        {
            MeasureError = ex.Message;
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

public sealed partial class ChecksumRowViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string SizeText { get; init; }

    [ObservableProperty]
    private string _hash = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    private bool _isDone;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasHash => !string.IsNullOrEmpty(Hash);
}

public sealed partial class ChecksumDialogViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<OperationSource> _sources;
    private readonly ClipboardService _clipboard;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;

    public ChecksumDialogViewModel(IReadOnlyList<OperationSource> sources, ClipboardService clipboard)
    {
        _sources = sources;
        _clipboard = clipboard;
        foreach (var source in sources)
        {
            Rows.Add(new ChecksumRowViewModel
            {
                Name = source.Name,
                Path = source.FileSystem.GetDisplayPath(source.Path),
                SizeText = source.IsDirectory ? L10n.T("文件夹") : Humanize.Size(source.Entry.Size),
            });
        }
    }

    public ObservableCollection<ChecksumRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = L10n.T("准备计算…");

    [ObservableProperty]
    private string? _fatalError;

    public bool HasResults => Rows.Any(r => r.HasHash);

    public async Task StartAsync()
    {
        if (_started)
            return;
        _started = true;
        IsRunning = true;
        try
        {
            for (var i = 0; i < _sources.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var source = _sources[i];
                var row = Rows[i];
                Status = L10n.Format("正在计算 SHA-256 · {0} / {1}", i + 1, _sources.Count);
                try
                {
                    var progress = new Progress<double>(value => row.Progress = value);
                    row.Hash = await ChecksumService.Sha256Async(source.FileSystem, source.Path, source.Entry.Size,
                        progress, _cts.Token).ConfigureAwait(true);
                    row.IsDone = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    row.Error = ex.Message;
                    Core.Services.Log.Warn($"校验和计算失败: {source.Path}", ex);
                }
            }

            Status = _cts.Token.IsCancellationRequested ? L10n.T("已取消") : L10n.T("计算完成");
            OnPropertyChanged(nameof(HasResults));
        }
        catch (OperationCanceledException)
        {
            Status = L10n.T("已取消");
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        Status = L10n.T("已取消");
    }

    [RelayCommand]
    private async Task CopyRowAsync(ChecksumRowViewModel? row)
    {
        if (row is null || !row.HasHash)
            return;
        await _clipboard.SetTextAsync(row.Hash).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        var text = string.Join(Environment.NewLine,
            Rows.Where(r => r.HasHash).Select(r => $"{r.Hash}  {r.Name}"));
        if (text.Length == 0)
            return;
        await _clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

public sealed partial class ConflictDialogViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required string SourceSizeText { get; init; }
    public required string DestinationSizeText { get; init; }
    public required string SourceKindText { get; init; }
    public required string DestinationKindText { get; init; }
    public bool SourceIsDirectory { get; init; }
    public bool DestinationIsDirectory { get; init; }

    [ObservableProperty]
    private bool _applyAll;

    public bool BothDirectories => SourceIsDirectory && DestinationIsDirectory;
    public string MergeHint => BothDirectories ? L10n.T("两者都是文件夹。替换会先删除目标文件夹再复制，保留两者会使用新的名称。") : string.Empty;
}

public sealed class AboutDialogViewModel
{
    public string Version { get; } = typeof(AboutDialogViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string Runtime { get; } = $".NET {Environment.Version}";
    public string Platform { get; } = $"{Environment.OSVersion.Platform} · {Environment.OSVersion.Version}";
}
