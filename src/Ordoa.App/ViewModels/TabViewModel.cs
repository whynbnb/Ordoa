using Ordoa.Core.Abstractions;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.FileSystems;

namespace Ordoa.App.ViewModels;

public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private bool _disposed;

    public TabViewModel(AppServices services, PaneViewModel primary)
    {
        _services = services;
        PrimaryPane = primary;
        _activePane = primary;
        primary.IsActive = true;
        Subscribe(primary);
    }

    public PaneViewModel PrimaryPane { get; }

    [ObservableProperty]
    private PaneViewModel? _secondaryPane;

    [ObservableProperty]
    private bool _isSplit;

    [ObservableProperty]
    private PaneViewModel _activePane;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isPinned;

    public string Title => ActivePane.Title;
    public string Tooltip
    {
        get
        {
            var text = PrimaryPane.DisplayPath;
            if (IsSplit && SecondaryPane is not null)
                text += Environment.NewLine + SecondaryPane.DisplayPath;
            return text;
        }
    }

    public Geometry? Icon => FileIconCatalog.Resource(
        ActivePane.FileSystem is TrashFileSystem ? "IconTrash" : ActivePane.IsRemote ? "IconCloud" : "IconFolder");

    public bool IsBusy => PrimaryPane.IsLoading || SecondaryPane?.IsLoading == true;
    public bool IsTrash => ActivePane.FileSystem is TrashFileSystem;

    public IEnumerable<PaneViewModel> Panes
    {
        get
        {
            yield return PrimaryPane;
            if (SecondaryPane is not null)
                yield return SecondaryPane;
        }
    }

    partial void OnActivePaneChanged(PaneViewModel value)
    {
        foreach (var pane in Panes)
            pane.IsActive = ReferenceEquals(pane, value);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(IsTrash));
    }

    partial void OnIsSplitChanged(bool value)
    {
        foreach (var pane in Panes)
            pane.IsInSplit = value;
        OnPropertyChanged(nameof(Tooltip));
    }

    public void ActivatePane(PaneViewModel pane)
    {
        if (ReferenceEquals(pane, ActivePane) || !Panes.Contains(pane))
            return;
        ActivePane = pane;
        pane.Activate();
    }

    public async Task SplitAsync()
    {
        if (IsSplit)
            return;

        var source = ActivePane;
        var fileSystem = source.FileSystem;
        var path = source.CurrentPath;
        if (fileSystem is TrashFileSystem)
        {
            fileSystem = _services.FileSystems.Local;
            path = Ordoa.Core.Util.KnownFolders.DefaultStartupPath;
        }

        var pane = new PaneViewModel(_services, fileSystem, path);
        Subscribe(pane);
        SecondaryPane = pane;
        IsSplit = true;
        ActivePane = pane;
        pane.Activate();
        await pane.InitializeAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tooltip));
    }

    public async Task AddSecondaryPaneAsync(IFileSystem fileSystem, string path, bool activate)
    {
        if (IsSplit || SecondaryPane is not null)
            return;

        var pane = new PaneViewModel(_services, fileSystem, path);
        Subscribe(pane);
        SecondaryPane = pane;
        IsSplit = true;
        if (activate)
        {
            ActivePane = pane;
            pane.Activate();
        }
        await pane.InitializeAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tooltip));
    }

    public void CloseSplit()
    {
        if (!IsSplit || SecondaryPane is null)
            return;

        var secondary = SecondaryPane;
        Unsubscribe(secondary);
        SecondaryPane = null;
        IsSplit = false;
        if (ReferenceEquals(ActivePane, secondary))
            ActivePane = PrimaryPane;
        secondary.Dispose();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tooltip));
    }

    public async Task ToggleSplitAsync()
    {
        if (IsSplit)
            CloseSplit();
        else
            await SplitAsync().ConfigureAwait(true);
    }

    public void RefreshTitle()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(IsTrash));
    }

    private void Subscribe(PaneViewModel pane)
    {
        pane.PropertyChanged += OnPanePropertyChanged;
    }

    private void Unsubscribe(PaneViewModel pane)
    {
        pane.PropertyChanged -= OnPanePropertyChanged;
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.Title) or nameof(PaneViewModel.IsLoading) or
            nameof(PaneViewModel.DisplayPath) or nameof(PaneViewModel.CurrentPath))
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Tooltip));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(IsTrash));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var pane in Panes.ToList())
        {
            Unsubscribe(pane);
            pane.Dispose();
        }
    }
}
