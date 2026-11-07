using CommunityToolkit.Mvvm.ComponentModel;

namespace Ordoa.App.ViewModels;

public sealed partial class MillerColumnViewModel : ObservableObject
{
    public MillerColumnViewModel(string path, string header, IReadOnlyList<FileItemViewModel> items)
    {
        Path = path;
        Header = header;
        Items = items;
    }

    public string Path { get; }
    public string Header { get; }
    public IReadOnlyList<FileItemViewModel> Items { get; }

    [ObservableProperty]
    private FileItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isActive;
}
