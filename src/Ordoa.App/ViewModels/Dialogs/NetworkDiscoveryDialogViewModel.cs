using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed partial class NetworkDiscoveryDialogViewModel : ObservableObject, IDisposable
{
    private readonly NetworkDiscoveryService _discovery = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<DiscoveredService> Services { get; } = [];

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _status = L10n.T("准备扫描局域网…");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseSelected))]
    private DiscoveredService? _selectedService;

    public bool CanUseSelected => SelectedService?.CanUse == true && !IsScanning;

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanUseSelected));

    public async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsScanning = true;
        Services.Clear();
        SelectedService = null;
        Status = L10n.T("正在通过 mDNS 与 WS-Discovery 扫描…");
        try
        {
            var found = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(2.5), token).ConfigureAwait(true);
            foreach (var service in found)
                Services.Add(service);
            SelectedService = Services.FirstOrDefault(service => service.CanUse) ?? Services.FirstOrDefault();
            Status = Services.Count == 0
                ? L10n.T("没有发现可用的局域网服务。")
                : L10n.Format("发现 {0} 个服务；选择后会自动填入主机和端口。", Services.Count);
        }
        catch (OperationCanceledException)
        {
            Status = L10n.T("扫描已取消。");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
