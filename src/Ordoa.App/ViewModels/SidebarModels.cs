using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels;

public enum SidebarEntryKind
{
    Place,
    Volume,
    Connection,
    Recent,
    Bookmark,
    Trash,
    AddConnection,
    AddBookmark,
    Action,
}

public sealed partial class SidebarEntryViewModel : ObservableObject
{
    public required string Label { get; init; }
    public string? Detail { get; init; }
    public required SidebarEntryKind Kind { get; init; }
    public Geometry? Icon { get; init; }
    public IBrush? IconBrush { get; init; }
    public string? FileSystemId { get; init; }
    public string? Path { get; init; }
    public string? ConnectionId { get; init; }
    public string? BookmarkId { get; init; }
    public string? VolumeMountPoint { get; init; }
    public bool ShowUsage { get; init; }
    public double UsageFraction { get; init; }
    public Func<SidebarEntryViewModel, Task>? ActivateAsync { get; set; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isHovered;

    public Task InvokeAsync() => ActivateAsync?.Invoke(this) ?? Task.CompletedTask;

    public static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}

public sealed class SidebarSectionViewModel
{
    public required string Title { get; init; }
    public ObservableCollection<SidebarEntryViewModel> Items { get; } = [];
}

public enum ToastKind
{
    Info,
    Success,
    Error,
}

public sealed class ToastViewModel
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public ToastKind Kind { get; init; }

    public bool IsError => Kind == ToastKind.Error;
    public bool IsSuccess => Kind == ToastKind.Success;
    public Geometry? Icon => FileIconCatalog.Resource(
        Kind == ToastKind.Error ? "IconWarning" : Kind == ToastKind.Success ? "IconCheckCircle" : "IconInfo");
    public string KindLabel => Kind switch
    {
        ToastKind.Error => L10n.T("错误"),
        ToastKind.Success => L10n.T("完成"),
        _ => L10n.T("提示"),
    };
}

public sealed class PaletteCommand
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Shortcut { get; init; }
    public Geometry? Icon { get; init; }
    public Func<Task>? ExecuteAsync { get; init; }
    public bool IsEnabled { get; init; } = true;

    public string DisplaySubtitle => Subtitle ?? string.Empty;
}
