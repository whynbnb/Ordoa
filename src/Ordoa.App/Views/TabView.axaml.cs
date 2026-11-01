using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ordoa.App.ViewModels;

namespace Ordoa.App.Views;

public partial class TabView : UserControl
{
    private TabViewModel? _tab;

    public TabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_tab is not null)
            _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab = DataContext as TabViewModel;
        if (_tab is not null)
            _tab.PropertyChanged += OnTabPropertyChanged;
        UpdateSplitLayout();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.IsSplit))
            UpdateSplitLayout();
    }

    private void UpdateSplitLayout()
    {
        var split = _tab?.IsSplit == true;
        Splitter.IsVisible = split;
        SecondaryHost.IsVisible = split;
        if (Content is Grid grid && grid.ColumnDefinitions.Count >= 3)
        {
            grid.ColumnDefinitions[1].Width = split ? new GridLength(1) : new GridLength(0);
            grid.ColumnDefinitions[2].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
    }

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TabViewModel tab)
            return;
        var paneView = (e.Source as Visual)?.FindAncestorOfType<PaneView>(includeSelf: true);
        if (paneView?.DataContext is PaneViewModel pane)
            tab.ActivatePane(pane);
    }
}
