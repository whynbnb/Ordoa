using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ordoa.App.Services;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed partial class ImageConversionRowViewModel : ObservableObject
{
    public ImageConversionRowViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string Name { get; }

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private bool _isFailed;
}

public sealed partial class ImageConversionDialogViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public ImageConversionDialogViewModel(IDialogService dialogs, IReadOnlyList<string> paths,
        string outputDirectory)
    {
        _dialogs = dialogs;
        _outputDirectory = outputDirectory;
        foreach (var path in paths)
            Rows.Add(new ImageConversionRowViewModel(path));
        SummaryText = L10n.Format("共 {0} 张图片", Rows.Count);
    }

    public ObservableCollection<ImageConversionRowViewModel> Rows { get; } = [];

    public IReadOnlyList<string> Formats { get; } =
    [
        "JPEG",
        "PNG",
        "WebP",
    ];

    [ObservableProperty]
    private int _formatIndex;

    [ObservableProperty]
    private decimal _quality = 85;

    [ObservableProperty]
    private decimal _maxDimension;

    [ObservableProperty]
    private string _suffix = "-converted";

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isRunning;

    public bool CanStart => !IsRunning && Rows.Count > 0 && !string.IsNullOrWhiteSpace(OutputDirectory);

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnOutputDirectoryChanged(string value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var selected = await _dialogs.PickFolderAsync(L10n.T("选择输出文件夹"), OutputDirectory)
            .ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(selected))
            OutputDirectory = selected;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart)
            return;

        IsRunning = true;
        Progress = 0;
        var succeeded = 0;
        var failed = 0;
        var format = FormatIndex switch
        {
            1 => ImageTargetFormat.Png,
            2 => ImageTargetFormat.WebP,
            _ => ImageTargetFormat.Jpeg,
        };
        var options = new ImageConversionOptions(format, (int)Quality, (int)MaxDimension, Suffix ?? string.Empty);

        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            row.Status = L10n.T("正在转换…");
            row.IsFailed = false;
            try
            {
                var destination = ImageConversionService.BuildOutputPath(row.Path, OutputDirectory, format,
                    Suffix ?? string.Empty);
                await ImageConversionService.ConvertAsync(row.Path, destination, options).ConfigureAwait(true);
                row.Status = System.IO.Path.GetFileName(destination);
                row.IsDone = true;
                succeeded++;
            }
            catch (Exception ex)
            {
                row.Status = ex.Message;
                row.IsFailed = true;
                failed++;
            }
            Progress = (double)(i + 1) / Rows.Count;
        }

        SummaryText = failed == 0
            ? L10n.Format("已转换 {0} 张图片", succeeded)
            : L10n.Format("已转换 {0} 张，失败 {1} 张", succeeded, failed);
        IsRunning = false;
    }
}
