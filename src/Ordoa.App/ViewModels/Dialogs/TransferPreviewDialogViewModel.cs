using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed partial class TransferPreviewDialogViewModel : ObservableObject
{
    private readonly TransferPreviewRequest _request;

    public TransferPreviewDialogViewModel(TransferPreviewRequest request)
    {
        _request = request;
        TitleText = request.Move ? L10n.T("移动预览") : L10n.T("复制预览");
        SummaryText = L10n.Format("{0} {1} 个项目到：", L10n.T(request.Move ? "移动" : "复制"),
            request.ItemCount);
    }

    public string TitleText { get; }
    public string SummaryText { get; }
    public string Destination => _request.Destination;
    public bool HasConflicts => _request.ConflictCount > 0;
    public IReadOnlyList<string> ConflictNames => _request.ConflictNames;
    public string ConflictText => L10n.Format("检测到 {0} 个同名项目：", _request.ConflictCount);
    public string MoreText => _request.ConflictCount > _request.ConflictNames.Count
        ? L10n.Format("还有 {0} 个同名项目", _request.ConflictCount - _request.ConflictNames.Count)
        : string.Empty;
    public bool ShowMore => MoreText.Length > 0;
    public string ScanText => _request.ScannedCount < _request.ItemCount
        ? L10n.Format("已检查前 {0} 个项目，其余冲突将在执行时询问。", _request.ScannedCount)
        : L10n.T("冲突处理方式会应用到本批次的全部同名项目。");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReplaceChecked))]
    [NotifyPropertyChangedFor(nameof(SkipChecked))]
    [NotifyPropertyChangedFor(nameof(RenameChecked))]
    private int _choiceIndex;

    public bool ReplaceChecked
    {
        get => ChoiceIndex == 0;
        set { if (value) ChoiceIndex = 0; }
    }

    public bool SkipChecked
    {
        get => ChoiceIndex == 1;
        set { if (value) ChoiceIndex = 1; }
    }

    public bool RenameChecked
    {
        get => ChoiceIndex == 2;
        set { if (value) ChoiceIndex = 2; }
    }

    public ConflictChoice Choice => ChoiceIndex switch
    {
        1 => ConflictChoice.SkipAll,
        2 => ConflictChoice.RenameAll,
        _ => ConflictChoice.ReplaceAll,
    };
}
