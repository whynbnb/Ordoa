using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ordoa.App.Services;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed class UndoHistoryRowViewModel
{
    public UndoHistoryRowViewModel(UndoHistoryItem item, bool undo)
    {
        Title = item.Title;
        Kind = item.Kind switch
        {
            UndoEntryKind.Rename => L10n.T("重命名"),
            UndoEntryKind.Move => L10n.T("移动"),
            UndoEntryKind.Trash => L10n.T("回收站"),
            UndoEntryKind.New => L10n.T("新建"),
            UndoEntryKind.Copy => L10n.T("复制"),
            _ => string.Empty,
        };
        Time = item.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        ActionText = undo ? L10n.T("撤销") : L10n.T("重做");
    }

    public string Title { get; }
    public string Kind { get; }
    public string Time { get; }
    public string ActionText { get; }
}

public sealed partial class UndoHistoryDialogViewModel : ObservableObject, IDisposable
{
    private readonly UndoService _undo;
    private bool _disposed;

    public UndoHistoryDialogViewModel(UndoService undo)
    {
        _undo = undo;
        _undo.Changed += OnChanged;
        Refresh();
    }

    public ObservableCollection<UndoHistoryRowViewModel> UndoRows { get; } = [];
    public ObservableCollection<UndoHistoryRowViewModel> RedoRows { get; } = [];
    public bool HasUndo => UndoRows.Count > 0;
    public bool HasRedo => RedoRows.Count > 0;
    public bool IsEmpty => !HasUndo && !HasRedo;
    public string SummaryText => IsEmpty
        ? L10n.T("本次会话还没有可撤销的操作")
        : L10n.Format("可撤销 {0} 步，可重做 {1} 步", UndoRows.Count, RedoRows.Count);

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    public void Refresh()
    {
        UndoRows.Clear();
        foreach (var item in _undo.UndoHistory)
            UndoRows.Add(new UndoHistoryRowViewModel(item, undo: true));
        RedoRows.Clear();
        foreach (var item in _undo.RedoHistory)
            RedoRows.Add(new UndoHistoryRowViewModel(item, undo: false));
        OnPropertyChanged(nameof(HasUndo));
        OnPropertyChanged(nameof(HasRedo));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        await _undo.UndoAsync().ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task RedoAsync()
    {
        await _undo.RedoAsync().ConfigureAwait(true);
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _undo.Changed -= OnChanged;
    }
}
