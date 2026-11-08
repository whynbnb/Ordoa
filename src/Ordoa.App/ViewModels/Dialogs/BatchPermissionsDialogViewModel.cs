using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels.Dialogs;

/// <summary>Applies one permission and/or ownership change to a multi-selection.</summary>
public sealed partial class BatchPermissionsDialogViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<FileEntry> _entries;
    private readonly IFileSystem _fileSystem;
    private readonly CancellationTokenSource _cts = new();

    public BatchPermissionsDialogViewModel(IReadOnlyList<FileEntry> entries, IFileSystem fileSystem)
    {
        _entries = entries;
        _fileSystem = fileSystem;
        CanEditPermissions = fileSystem.Capabilities.Has(FileSystemCapabilities.UnixPermissions) &&
                             entries.Any(entry => entry.UnixMode is not null);
        CanEditOwnership = fileSystem.IsLocal &&
                           entries.Any(entry => entry.UnixMode is not null);

        var modes = entries
            .Select(entry => entry.UnixMode)
            .Where(mode => mode is not null)
            .Select(mode => mode!.Value)
            .Distinct()
            .ToList();
        _octalInput = modes.Count == 1 ? Humanize.OctalPermissions(modes[0]) : string.Empty;
        _ownerInput = entries.Select(entry => entry.Owner).Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal).Count() == 1
            ? entries[0].Owner ?? string.Empty
            : string.Empty;
        _groupInput = entries.Select(entry => entry.Group).Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal).Count() == 1
            ? entries[0].Group ?? string.Empty
            : string.Empty;
    }

    public int Count => _entries.Count;
    public string Summary => L10n.Format("将对 {0} 个项目应用更改", Count);
    public bool CanEditPermissions { get; }
    public bool CanEditOwnership { get; }

    [ObservableProperty]
    private string _octalInput = string.Empty;

    [ObservableProperty]
    private string _ownerInput = string.Empty;

    [ObservableProperty]
    private string _groupInput = string.Empty;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isApplying;

    public async Task<bool> ApplyAsync()
    {
        if (IsApplying)
            return false;

        var modeText = OctalInput.Trim();
        UnixFileMode? mode = null;
        if (modeText.Length > 0)
        {
            if (modeText.Length is < 3 or > 4 || modeText.Any(c => c is < '0' or > '7'))
            {
                Status = L10n.T("请输入 3 或 4 位八进制数字，例如 755");
                return false;
            }

            var bits = Convert.ToInt32(modeText, 8);
            mode = (UnixFileMode)(bits & 0xFFF);
        }

        var owner = OwnerInput.Trim();
        var group = GroupInput.Trim();
        var changeOwnership = CanEditOwnership && (owner.Length > 0 || group.Length > 0);
        if (mode is null && !changeOwnership)
        {
            Status = L10n.T("没有需要修改的内容");
            return false;
        }

        IsApplying = true;
        Status = null;
        var failures = new List<string>();
        try
        {
            foreach (var entry in _entries)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if (mode is { } value)
                        await _fileSystem.SetUnixModeAsync(entry.Path, value, _cts.Token).ConfigureAwait(true);
                    if (changeOwnership)
                        await ExtendedMetadata.SetOwnershipAsync(entry.Path, owner, group, _cts.Token)
                            .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    failures.Add($"{entry.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsApplying = false;
        }

        if (failures.Count == 0)
        {
            Status = L10n.T("已应用更改");
            return true;
        }

        Status = L10n.Format("{0} 个项目失败：{1}", failures.Count,
            string.Join("; ", failures.Take(3)));
        return false;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
