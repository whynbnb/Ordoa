using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed class OperationHistoryRowViewModel
{
    public OperationHistoryRowViewModel(OperationHistoryEntry entry)
    {
        Title = entry.Title;
        Detail = entry.Detail;
        Error = entry.Error;
        HasError = !string.IsNullOrWhiteSpace(entry.Error);
        IsFailed = entry.State == OperationState.Failed;
        StateText = entry.State switch
        {
            OperationState.Completed => L10n.T("已完成"),
            OperationState.Failed => L10n.T("失败"),
            OperationState.Cancelled => L10n.T("已取消"),
            _ => entry.State.ToString(),
        };
        TimeText = entry.FinishedAt.ToLocalTime().ToString("HH:mm:ss");
        CountsText = entry.FailureCount > 0
            ? L10n.Format("成功 {0} · 失败 {1}", entry.SuccessCount, entry.FailureCount)
            : entry.SuccessCount > 0
                ? L10n.Format("成功 {0}", entry.SuccessCount)
                : string.Empty;
    }

    public string Title { get; }
    public string Detail { get; }
    public string StateText { get; }
    public string TimeText { get; }
    public string CountsText { get; }
    public string? Error { get; }
    public bool HasError { get; }
    public bool IsFailed { get; }
}

public sealed partial class OperationHistoryDialogViewModel : ObservableObject
{
    private readonly FileOperationService _operations;

    public OperationHistoryDialogViewModel(FileOperationService operations)
    {
        _operations = operations;
        Refresh();
    }

    public ObservableCollection<OperationHistoryRowViewModel> Rows { get; } = [];
    public bool HasHistory => Rows.Count > 0;
    public string SummaryText => Rows.Count == 0
        ? L10n.T("本次会话还没有完成的操作")
        : L10n.Format("本次会话共 {0} 条操作记录", Rows.Count);

    public void Refresh()
    {
        Rows.Clear();
        foreach (var entry in _operations.History)
            Rows.Add(new OperationHistoryRowViewModel(entry));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(SummaryText));
    }

    public void Clear()
    {
        _operations.ClearHistory();
        Refresh();
    }
}
