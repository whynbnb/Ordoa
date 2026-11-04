using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed partial class BatchRenameRowViewModel : ObservableObject
{
    private string _newName = string.Empty;
    private string _statusText = string.Empty;
    private string? _issueText;
    private string _toolTipText = string.Empty;
    private bool _hasIssue;
    private bool _isUnchanged;

    public BatchRenameRowViewModel(FileEntry entry)
    {
        OriginalName = entry.Name;
        NewName = entry.Name;
        ToolTipText = entry.Name;
    }

    public string OriginalName { get; }

    public string NewName
    {
        get => _newName;
        private set => SetProperty(ref _newName, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? IssueText
    {
        get => _issueText;
        private set => SetProperty(ref _issueText, value);
    }

    public string ToolTipText
    {
        get => _toolTipText;
        private set => SetProperty(ref _toolTipText, value);
    }

    public bool HasIssue
    {
        get => _hasIssue;
        private set => SetProperty(ref _hasIssue, value);
    }

    public bool IsUnchanged
    {
        get => _isUnchanged;
        private set => SetProperty(ref _isUnchanged, value);
    }

    public void Update(BatchRenameRow row)
    {
        NewName = row.NewName;
        HasIssue = row.HasIssue;
        IsUnchanged = !row.IsChanged;
        IssueText = row.IssueText;
        StatusText = row.Issue switch
        {
            BatchRenameIssue.InvalidName => L10n.T("非法"),
            BatchRenameIssue.DuplicateName => L10n.T("重复"),
            BatchRenameIssue.NameExists => L10n.T("名称已存在"),
            _ => row.IsChanged ? string.Empty : L10n.T("不变"),
        };
        ToolTipText = row.IssueText ?? (row.IsChanged
            ? L10n.Format("{0} → {1}", row.Entry.Name, row.NewName)
            : L10n.T("名称保持不变"));
    }
}

public sealed partial class BatchRenameDialogViewModel : ObservableObject, IDisposable
{
    private readonly BatchRenameRequest _request;
    private readonly DispatcherTimer _refreshTimer;
    private BatchRenamePlan? _plan;
    private bool _disposed;

    public BatchRenameDialogViewModel(BatchRenameRequest request)
    {
        _request = request;
        CaseModes =
        [
            L10n.T("保持不变"),
            L10n.T("全部小写"),
            L10n.T("全部大写"),
            L10n.T("首字母大写"),
        ];
        ExtensionModes =
        [
            L10n.T("保留"),
            L10n.T("小写"),
            L10n.T("大写"),
            L10n.T("替换为"),
            L10n.T("移除"),
        ];
        NumberPositions =
        [
            L10n.T("名称前"),
            L10n.T("名称后"),
        ];

        foreach (var entry in request.Entries)
            Rows.Add(new BatchRenameRowViewModel(entry));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshPreview();
        };

        PropertyChanged += OnOptionChanged;
        RefreshPreview();
    }

    public IReadOnlyList<string> CaseModes { get; }
    public IReadOnlyList<string> ExtensionModes { get; }
    public IReadOnlyList<string> NumberPositions { get; }
    public ObservableCollection<BatchRenameRowViewModel> Rows { get; } = [];

    public string DateHint => L10n.T("前缀和后缀支持日期变量：{date}、{mtime}、{ctime}，例如 {date:yyyyMMdd}");
    public string PreviewTitle => L10n.Format("预览 · {0} 项", _request.Entries.Count);

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private string _replaceText = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private string _prefix = string.Empty;

    [ObservableProperty]
    private string _suffix = string.Empty;

    [ObservableProperty]
    private bool _numbering;

    [ObservableProperty]
    private decimal? _numberStart = 1;

    [ObservableProperty]
    private decimal? _numberStep = 1;

    [ObservableProperty]
    private decimal? _numberPadding = 3;

    [ObservableProperty]
    private string _numberSeparator = "_";

    [ObservableProperty]
    private int _numberPositionIndex = 1;

    [ObservableProperty]
    private int _caseIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCustomExtension))]
    private int _extensionIndex;

    [ObservableProperty]
    private string _customExtension = string.Empty;

    public bool ShowCustomExtension => ExtensionIndex == 3;

    public string Summary
    {
        get
        {
            if (_plan is null || _plan.ChangeCount == 0)
                return L10n.T("没有需要重命名的项目");

            if (_plan.IssueCount > 0)
                return L10n.Format("{0} 项将重命名 · {1} 个问题", _plan.ChangeCount, _plan.IssueCount);

            var unchanged = _plan.Rows.Count - _plan.ChangeCount;
            return unchanged > 0
                ? L10n.Format("{0} 项将重命名 · {1} 项保持不变", _plan.ChangeCount, unchanged)
                : L10n.Format("{0} 项将重命名", _plan.ChangeCount);
        }
    }

    public bool HasIssues => _plan?.IssueCount > 0;
    public bool CanApply => _plan?.CanApply == true;

    public BatchRenamePlan? BuildResult()
    {
        _refreshTimer.Stop();
        RefreshPreview();
        return _plan is { CanApply: true } ? _plan : null;
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;

        if (e.PropertyName is nameof(Summary) or nameof(HasIssues) or nameof(CanApply) or
            nameof(ShowCustomExtension))
        {
            return;
        }

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void RefreshPreview()
    {
        var options = new BatchRenameOptions
        {
            FindText = FindText ?? string.Empty,
            ReplaceText = ReplaceText ?? string.Empty,
            MatchCase = MatchCase,
            Prefix = Prefix ?? string.Empty,
            Suffix = Suffix ?? string.Empty,
            Numbering = Numbering,
            NumberStart = (int)Math.Clamp(NumberStart ?? 1m, 0m, 999999m),
            NumberStep = (int)Math.Clamp(NumberStep ?? 1m, 1m, 999999m),
            NumberPadding = (int)Math.Clamp(NumberPadding ?? 3m, 1m, 9m),
            NumberSeparator = NumberSeparator ?? string.Empty,
            NumberPosition = NumberPositionIndex == 0
                ? BatchRenameNumberPosition.Prefix
                : BatchRenameNumberPosition.Suffix,
            CaseMode = (BatchRenameCaseMode)Math.Clamp(CaseIndex, 0, 3),
            ExtensionMode = (BatchRenameExtensionMode)Math.Clamp(ExtensionIndex, 0, 4),
            CustomExtension = CustomExtension ?? string.Empty,
        };

        _plan = BatchRenamePlanner.CreatePlan(
            _request.Entries,
            _request.ExistingNames,
            options,
            _request.CaseSensitive,
            _request.NameRules);

        for (var i = 0; i < _plan.Rows.Count && i < Rows.Count; i++)
            Rows[i].Update(_plan.Rows[i]);

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(CanApply));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _refreshTimer.Stop();
        PropertyChanged -= OnOptionChanged;
    }
}
