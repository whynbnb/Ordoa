using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed record OpenWithChoice(string? DesktopId, bool Remember);

public sealed class OpenWithAppViewModel
{
    public OpenWithAppViewModel(DesktopApplication application, bool isDefault)
    {
        Id = application.Id;
        Name = application.Name;
        Comment = application.Comment;
        IsDefault = isDefault;
    }

    public string Id { get; }
    public string Name { get; }
    public string Comment { get; }
    public bool IsDefault { get; }
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
}

public sealed partial class OpenWithDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<OpenWithAppViewModel> _all;
    private readonly string? _currentDefaultId;

    public OpenWithDialogViewModel(
        FileEntry entry,
        IReadOnlyList<DesktopApplication> applications,
        string? currentDefaultId)
    {
        FileName = entry.Name;
        _currentDefaultId = currentDefaultId;
        ExtensionText = string.IsNullOrEmpty(entry.Extension)
            ? L10n.T("无扩展名")
            : "." + entry.Extension;

        _all = applications
            .OrderByDescending(app => string.Equals(app.Id, currentDefaultId, StringComparison.Ordinal))
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(app => new OpenWithAppViewModel(
                app,
                string.Equals(app.Id, currentDefaultId, StringComparison.Ordinal)))
            .ToList();

        DefaultText = _all.FirstOrDefault(app => app.IsDefault) is { } defaultApp
            ? L10n.Format("当前默认：{0}", defaultApp.Name)
            : string.Empty;

        RememberText = string.IsNullOrEmpty(entry.Extension)
            ? L10n.T("记住此选择（无扩展名）")
            : L10n.Format("记住此选择（.{0}）", entry.Extension);

        RefreshFilter();
    }

    public string FileName { get; }
    public string ExtensionText { get; }
    public string DefaultText { get; }
    public bool HasDefaultText => !string.IsNullOrEmpty(DefaultText);
    public string RememberText { get; }
    public ObservableCollection<OpenWithAppViewModel> Apps { get; } = [];

    [ObservableProperty]
    private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => RefreshFilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    private OpenWithAppViewModel? _selectedApp;

    [ObservableProperty]
    private bool _rememberChoice = true;

    public bool CanOpen => SelectedApp is not null;
    public bool HasApps => Apps.Count > 0;

    public OpenWithChoice? BuildResult()
        => SelectedApp is null ? null : new OpenWithChoice(SelectedApp.Id, RememberChoice);

    public OpenWithChoice BuildSystemDefaultResult() => new(null, RememberChoice);

    private void RefreshFilter()
    {
        var selectedId = SelectedApp?.Id;
        var query = FilterText?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(app =>
                    app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    app.Comment.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        Apps.Clear();
        foreach (var app in filtered)
            Apps.Add(app);

        SelectedApp = Apps.FirstOrDefault(app => string.Equals(app.Id, selectedId, StringComparison.Ordinal))
                      ?? Apps.FirstOrDefault();
        OnPropertyChanged(nameof(HasApps));
    }
}
