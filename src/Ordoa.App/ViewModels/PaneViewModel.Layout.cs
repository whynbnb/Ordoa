using System.Globalization;
using Ordoa.App.Services;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels;

/// <summary>Column layout, view density, grouping and per-folder view state.</summary>
public sealed partial class PaneViewModel
{
    public string WideColumnLayout
    {
        get => _wideColumnLayout;
        private set => SetProperty(ref _wideColumnLayout, value);
    }

    public bool ShowSizeColumn => _services.Settings.Current.ShowSizeColumn;
    public bool ShowModifiedColumn => _services.Settings.Current.ShowModifiedColumn;
    public bool ShowTypeColumn => _services.Settings.Current.ShowTypeColumn;
    public bool ShowPermissionColumn => _services.Settings.Current.ShowPermissionColumn;
    public bool ShowOwnerColumn => _services.Settings.Current.ShowOwnerColumn;
    public bool ShowTagsColumn => _services.Settings.Current.ShowTagsColumn;

    public int NameColumnIndex => ColumnIndex(FileListColumn.Name);
    public int SizeColumnIndex => ColumnIndex(FileListColumn.Size);
    public int ModifiedColumnIndex => ColumnIndex(FileListColumn.Modified);
    public int TypeColumnIndex => ColumnIndex(FileListColumn.Type);
    public int TagsColumnIndex => ColumnIndex(FileListColumn.Tags);
    public int PermissionsColumnIndex => ColumnIndex(FileListColumn.Permissions);
    public int OwnerColumnIndex => ColumnIndex(FileListColumn.Owner);

    public GroupByMode GroupBy
    {
        get => _groupBy;
        set
        {
            if (!SetProperty(ref _groupBy, value))
                return;
            _services.Settings.Current.GroupBy = value;
            _services.Settings.Save();
            OnPropertyChanged(nameof(IsGrouped));
            ApplySortAndFilter();
        }
    }

    public bool IsGrouped => _groupBy != GroupByMode.None && !IsGrid && !IsTree && !IsMiller;

    public void SetGridThumbnailSize(GridThumbnailSize size)
    {
        var settings = _services.Settings.Current;
        if (settings.GridThumbnailSize == size)
            return;
        settings.GridThumbnailSize = size;
        _services.Settings.Save();
    }

    public void SetListDensity(ListDensity density)
    {
        var settings = _services.Settings.Current;
        if (settings.ListDensity == density)
            return;
        settings.ListDensity = density;
        _services.Settings.Save();
    }

    public void ToggleTagsColumn()
    {
        var settings = _services.Settings.Current;
        settings.ShowTagsColumn = !settings.ShowTagsColumn;
        _services.Settings.Save();
        RefreshColumnLayout();
    }

    public bool IsDensityCompact => _listDensity == ListDensity.Compact;
    public bool IsDensitySpacious => _listDensity == ListDensity.Spacious;

    private void NotifyGridMetrics()
    {
        OnPropertyChanged(nameof(GridItemWidth));
        OnPropertyChanged(nameof(GridItemHeight));
        OnPropertyChanged(nameof(TileWidth));
        OnPropertyChanged(nameof(TileImageHeight));
        OnPropertyChanged(nameof(TileIconSize));
        OnPropertyChanged(nameof(TileFontSize));
        OnPropertyChanged(nameof(GridThumbnailDecodeWidth));
    }

    public double GridItemWidth => _gridThumbnailSize switch
    {
        GridThumbnailSize.Small => 98,
        GridThumbnailSize.Large => 156,
        _ => 118,
    };

    public double GridItemHeight => _gridThumbnailSize switch
    {
        GridThumbnailSize.Small => 92,
        GridThumbnailSize.Large => 152,
        _ => 108,
    };

    public double TileWidth => GridItemWidth - 14;
    public double TileImageHeight => _gridThumbnailSize switch
    {
        GridThumbnailSize.Small => 44,
        GridThumbnailSize.Large => 96,
        _ => 62,
    };

    public double TileIconSize => _gridThumbnailSize switch
    {
        GridThumbnailSize.Small => 30,
        GridThumbnailSize.Large => 64,
        _ => 44,
    };

    public double TileFontSize => _gridThumbnailSize switch
    {
        GridThumbnailSize.Small => 10.5,
        GridThumbnailSize.Large => 12.5,
        _ => 11.5,
    };

    public int GridThumbnailDecodeWidth => _gridThumbnailSize switch
    {
        GridThumbnailSize.Small => 48,
        GridThumbnailSize.Large => 128,
        _ => 72,
    };

    /// <summary>
    /// Below this width only the name and size columns are shown. It grows with
    /// the fixed column widths so columns never get squeezed into unusable slivers.
    /// </summary>
    public double NarrowThreshold
    {
        get
        {
            var fixedWidth = _columnOrder
                .Where(IsColumnVisible)
                .Sum(GetColumnWidth);
            return Math.Max(560, fixedWidth + 220);
        }
    }

    public double GetColumnWidth(FileListColumn column)
    {
        if (column == FileListColumn.Name)
            return _nameColumnWidth ?? 0;
        return _columnWidths.TryGetValue(column, out var width)
            ? width
            : DefaultColumnWidth(column);
    }

    /// <summary>Resizes a fixed column and optionally persists the new layout.</summary>
    public void SetColumnWidth(FileListColumn column, double width, bool commit = false)
    {
        width = Math.Clamp(width, column == FileListColumn.Name ? 120 : 40, 800);
        if (Math.Abs(GetColumnWidth(column) - width) < 0.5)
        {
            if (commit)
                PersistColumnLayout();
            return;
        }

        if (column == FileListColumn.Name)
            _nameColumnWidth = width;
        else
            _columnWidths[column] = width;

        WideColumnLayout = BuildColumnLayout(_services.Settings.Current);
        OnPropertyChanged(nameof(NarrowThreshold));
        if (commit)
            PersistColumnLayout();
    }

    /// <summary>Moves a column before or after the target, depending on the drag direction.</summary>
    public void MoveColumn(FileListColumn source, FileListColumn target)
    {
        if (source == target)
            return;

        var from = _columnOrder.IndexOf(source);
        var to = _columnOrder.IndexOf(target);
        if (from < 0 || to < 0)
            return;

        _columnOrder.RemoveAt(from);
        var insert = _columnOrder.IndexOf(target);
        if (from < to)
            insert++;
        _columnOrder.Insert(Math.Clamp(insert, 0, _columnOrder.Count), source);

        WideColumnLayout = BuildColumnLayout(_services.Settings.Current);
        NotifyColumnProperties();
        PersistColumnLayout();
    }

    public void CommitColumnLayout() => PersistColumnLayout();

    private int ColumnIndex(FileListColumn column)
    {
        var index = _columnOrder.IndexOf(column);
        return index >= 0 ? index : (int)column;
    }

    private static double DefaultColumnWidth(FileListColumn column) => column switch
    {
        FileListColumn.Size => 74,
        FileListColumn.Modified => 116,
        FileListColumn.Type => 88,
        FileListColumn.Tags => 110,
        FileListColumn.Permissions => 86,
        FileListColumn.Owner => 92,
        _ => 260,
    };

    private bool IsColumnVisible(FileListColumn column) => column switch
    {
        FileListColumn.Name => true,
        FileListColumn.Size => ShowSizeColumn,
        FileListColumn.Modified => ShowModifiedColumn,
        FileListColumn.Type => ShowTypeColumn,
        FileListColumn.Tags => ShowTagsColumn,
        FileListColumn.Permissions => ShowPermissionColumn,
        FileListColumn.Owner => ShowOwnerColumn,
        _ => false,
    };

    private string BuildColumnLayout(AppSettings settings)
    {
        var parts = new List<string>(_columnOrder.Count);
        foreach (var column in _columnOrder)
        {
            if (column == FileListColumn.Name)
            {
                parts.Add(_nameColumnWidth is { } nameWidth
                    ? Math.Clamp(nameWidth, 120, 800).ToString("0.##", CultureInfo.InvariantCulture)
                    : "*");
                continue;
            }

            if (!IsColumnVisible(column))
            {
                parts.Add("0");
                continue;
            }

            var width = Math.Clamp(GetColumnWidth(column), 40, 800);
            parts.Add(width.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return string.Join(",", parts);
    }

    private static List<FileListColumn> ParseColumnOrder(IEnumerable<string>? raw)
    {
        var order = new List<FileListColumn>();
        if (raw is not null)
        {
            foreach (var value in raw)
            {
                if (Enum.TryParse<FileListColumn>(value, ignoreCase: true, out var column) &&
                    !order.Contains(column))
                {
                    order.Add(column);
                }
            }
        }

        foreach (var column in Enum.GetValues<FileListColumn>())
        {
            if (!order.Contains(column))
                order.Add(column);
        }

        return order;
    }

    private void ReloadColumnSettings(AppSettings settings)
    {
        _columnOrder.Clear();
        _columnOrder.AddRange(ParseColumnOrder(settings.ColumnOrder));

        _columnWidths.Clear();
        _nameColumnWidth = settings.ColumnWidths.TryGetValue(nameof(FileListColumn.Name), out var nameWidth)
            ? Math.Clamp(nameWidth, 120, 800)
            : null;
        foreach (var column in Enum.GetValues<FileListColumn>())
        {
            if (column == FileListColumn.Name)
                continue;

            var name = column.ToString();
            _columnWidths[column] = settings.ColumnWidths.TryGetValue(name, out var width)
                ? Math.Clamp(width, 40, 800)
                : DefaultColumnWidth(column);
        }

        if (settings.FolderViews.TryGetValue(FolderViewKey, out var state))
            ApplyColumnOverrides(state);
    }

    private void ApplyColumnOverrides(FolderViewState state)
    {
        if (state.ColumnOrder is { Count: > 0 } order)
        {
            _columnOrder.Clear();
            _columnOrder.AddRange(ParseColumnOrder(order));
        }

        if (state.ColumnWidths is { Count: > 0 } widths)
        {
            foreach (var pair in widths)
            {
                if (!Enum.TryParse<FileListColumn>(pair.Key, ignoreCase: true, out var column))
                    continue;

                if (column == FileListColumn.Name)
                    _nameColumnWidth = Math.Clamp(pair.Value, 120, 800);
                else
                    _columnWidths[column] = Math.Clamp(pair.Value, 40, 800);
            }
        }
    }

    private void RefreshColumnLayout()
    {
        ReloadColumnSettings(_services.Settings.Current);
        WideColumnLayout = BuildColumnLayout(_services.Settings.Current);
        NotifyColumnProperties();
    }

    private void NotifyColumnProperties()
    {
        OnPropertyChanged(nameof(ShowSizeColumn));
        OnPropertyChanged(nameof(ShowModifiedColumn));
        OnPropertyChanged(nameof(ShowTypeColumn));
        OnPropertyChanged(nameof(ShowPermissionColumn));
        OnPropertyChanged(nameof(ShowOwnerColumn));
        OnPropertyChanged(nameof(ShowTagsColumn));
        OnPropertyChanged(nameof(NameColumnIndex));
        OnPropertyChanged(nameof(SizeColumnIndex));
        OnPropertyChanged(nameof(ModifiedColumnIndex));
        OnPropertyChanged(nameof(TypeColumnIndex));
        OnPropertyChanged(nameof(TagsColumnIndex));
        OnPropertyChanged(nameof(PermissionsColumnIndex));
        OnPropertyChanged(nameof(OwnerColumnIndex));
        OnPropertyChanged(nameof(NarrowThreshold));
        OnPropertyChanged(nameof(IsGrouped));
    }

    private Dictionary<string, double> BuildColumnWidths()
    {
        var widths = _columnWidths.ToDictionary(
            pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal);
        if (_nameColumnWidth is { } nameWidth)
            widths[nameof(FileListColumn.Name)] = nameWidth;
        return widths;
    }

    private void PersistColumnLayout()
    {
        var settings = _services.Settings.Current;
        settings.ColumnOrder = _columnOrder.Select(column => column.ToString()).ToList();
        settings.ColumnWidths = _columnWidths.ToDictionary(
            pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal);
        if (_nameColumnWidth is { } nameWidth)
            settings.ColumnWidths[nameof(FileListColumn.Name)] = nameWidth;

        settings.FolderViews[FolderViewKey] = new FolderViewState
        {
            SortColumn = SortColumn,
            SortDescending = SortDescending,
            FoldersFirst = FoldersFirst,
            View = ViewMode,
            ColumnOrder = [.. settings.ColumnOrder],
            ColumnWidths = new Dictionary<string, double>(settings.ColumnWidths, StringComparer.Ordinal),
        };
        _services.Settings.Save();
    }

    private string FolderViewKey =>
        FileSystem.Id + "|" + FileSystem.NormalizePath(CurrentPath);

    private void ApplyFolderViewState()
    {
        var key = FolderViewKey;
        if (string.Equals(key, _appliedFolderViewKey, StringComparison.Ordinal))
            return;
        _appliedFolderViewKey = key;

        if (!_services.Settings.Current.FolderViews.TryGetValue(key, out var state))
            return;

        _sortColumn = state.SortColumn;
        _sortDescending = state.SortDescending;
        _foldersFirst = state.FoldersFirst;
        _viewMode = state.View;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(FoldersFirst));
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(SortColumnLabel));
        OnPropertyChanged(nameof(IsGrid));
        OnPropertyChanged(nameof(IsTree));
        OnPropertyChanged(nameof(IsMiller));
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(ViewIcon));
        OnPropertyChanged(nameof(ViewTooltip));
        RefreshColumnLayout();
    }

    private void SaveFolderViewState()
    {
        var settings = _services.Settings.Current;
        settings.SortColumn = SortColumn;
        settings.SortDescending = SortDescending;
        settings.FoldersFirst = FoldersFirst;
        settings.View = ViewMode;
        settings.FolderViews[FolderViewKey] = new FolderViewState
        {
            SortColumn = SortColumn,
            SortDescending = SortDescending,
            FoldersFirst = FoldersFirst,
            View = ViewMode,
            ColumnOrder = _columnOrder.Select(column => column.ToString()).ToList(),
            ColumnWidths = BuildColumnWidths(),
        };
        _services.Settings.Save();
    }
}
