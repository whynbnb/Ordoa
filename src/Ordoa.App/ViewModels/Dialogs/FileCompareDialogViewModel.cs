using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed class FileCompareRowViewModel
{
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.Parse("#33D9534F"));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#3328A745"));
    private static readonly IBrush ChangedBrush = new SolidColorBrush(Color.Parse("#33E0A800"));

    public FileCompareRowViewModel(CompareLine line)
    {
        LeftNumber = line.LeftNumber?.ToString() ?? string.Empty;
        RightNumber = line.RightNumber?.ToString() ?? string.Empty;
        LeftText = line.LeftText;
        RightText = line.RightText;
        LeftBackground = line.Kind switch
        {
            CompareLineKind.Removed => RemovedBrush,
            CompareLineKind.Changed => ChangedBrush,
            _ => Brushes.Transparent,
        };
        RightBackground = line.Kind switch
        {
            CompareLineKind.Added => AddedBrush,
            CompareLineKind.Changed => ChangedBrush,
            _ => Brushes.Transparent,
        };
    }

    public string LeftNumber { get; }
    public string RightNumber { get; }
    public string LeftText { get; }
    public string RightText { get; }
    public IBrush LeftBackground { get; }
    public IBrush RightBackground { get; }
}

public sealed partial class FileCompareDialogViewModel : ObservableObject
{
    private readonly IFileSystem _fileSystem;
    private readonly FileEntry _left;
    private readonly FileEntry _right;

    public FileCompareDialogViewModel(IFileSystem fileSystem, FileEntry left, FileEntry right)
    {
        _fileSystem = fileSystem;
        _left = left;
        _right = right;
        LeftHeader = $"{left.Name} · {Humanize.Size(left.Size)}";
        RightHeader = $"{right.Name} · {Humanize.Size(right.Size)}";
    }

    public ObservableCollection<FileCompareRowViewModel> Rows { get; } = [];
    public string LeftHeader { get; }
    public string RightHeader { get; }

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isTextResult;

    [ObservableProperty]
    private string? _error;

    public bool HasError => Error is not null;

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var result = await FileCompareService.CompareAsync(_fileSystem, _left, _right).ConfigureAwait(true);
            SummaryText = result.Summary;
            IsTextResult = result.Kind == FileCompareKind.Text;
            Rows.Clear();
            if (IsTextResult)
            {
                foreach (var line in result.Lines)
                    Rows.Add(new FileCompareRowViewModel(line));
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            SummaryText = L10n.T("比较失败");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasError));
        }
    }
}
