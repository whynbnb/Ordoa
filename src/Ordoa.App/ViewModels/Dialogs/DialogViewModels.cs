using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed class ConfirmDialogViewModel
{
    public required string TitleText { get; init; }
    public required string Message { get; init; }
    public string ConfirmText { get; init; } = L10n.T("确定");
    public string CancelText { get; init; } = L10n.T("取消");
    public bool Destructive { get; init; }
    public bool ShowCancel { get; init; } = true;
}

public sealed partial class PromptDialogViewModel : ObservableObject
{
    private readonly Func<string, string?>? _validate;

    public PromptDialogViewModel(string title, string label, string initial, Func<string, string?>? validate)
    {
        TitleText = title;
        Label = label;
        _value = initial;
        _validate = validate;
        Validate();
    }

    public string TitleText { get; }
    public string Label { get; }
    public string ConfirmText { get; init; } = L10n.T("确定");
    public string CancelText { get; init; } = L10n.T("取消");

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private string? _validationError;

    public bool CanConfirm => string.IsNullOrEmpty(ValidationError);

    partial void OnValueChanged(string value) => Validate();

    private void Validate()
    {
        ValidationError = _validate?.Invoke(Value);
        OnPropertyChanged(nameof(CanConfirm));
    }
}

public sealed partial class ConnectionDialogViewModel : ObservableObject
{
    private readonly ConnectionProfile _source;
    private readonly FileSystemManager _fileSystems;

    public ConnectionDialogViewModel(ConnectionProfile? preset, string? password, FileSystemManager fileSystems)
    {
        _source = preset?.Clone() ?? new ConnectionProfile
        {
            Protocol = RemoteProtocol.Sftp,
            InitialPath = "/",
            Secure = true,
            SavePassword = true,
        };
        _fileSystems = fileSystems;
        IsEditing = preset is not null;

        _name = _source.Name;
        _protocol = _source.Protocol;
        _host = _source.Host;
        _portText = _source.Port > 0 ? _source.Port.ToString() : string.Empty;
        _username = _source.Username;
        _password = password ?? string.Empty;
        _domain = _source.Domain;
        _initialPath = _source.NormalizeInitialPath(_source.InitialPath);
        _anonymous = _source.Anonymous;
        _savePassword = _source.SavePassword;
        _secure = _source.Secure;
        _allowInvalidCertificate = _source.AllowInvalidCertificate;
        _saveProfile = true;
    }

    public bool IsEditing { get; }
    public string HeaderTitle => IsEditing ? L10n.T("编辑远程连接") : L10n.T("新建远程连接");
    public string HeaderSubtitle => Protocol switch
    {
        RemoteProtocol.Ftp => L10n.T("FTP · 文件传输协议"),
        RemoteProtocol.Ftps => L10n.T("FTPS · 加密 FTP"),
        RemoteProtocol.Sftp => L10n.T("SFTP · SSH 文件传输"),
        RemoteProtocol.Smb => L10n.T("SMB · Windows 共享"),
        RemoteProtocol.WebDav => L10n.T("WebDAV · 基于 HTTP 的文件访问"),
        _ => string.Empty,
    };

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    [NotifyPropertyChangedFor(nameof(IsFtp))]
    [NotifyPropertyChangedFor(nameof(IsSmb))]
    [NotifyPropertyChangedFor(nameof(IsWebDav))]
    [NotifyPropertyChangedFor(nameof(DefaultPortText))]
    [NotifyPropertyChangedFor(nameof(ShowAnonymous))]
    [NotifyPropertyChangedFor(nameof(ShowTlsOptions))]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private RemoteProtocol _protocol;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private string _host;

    [ObservableProperty]
    private string _portText;

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    private string _password;

    [ObservableProperty]
    private string _domain;

    [ObservableProperty]
    private string _initialPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAuthFields))]
    private bool _anonymous;

    [ObservableProperty]
    private bool _savePassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultPortText))]
    private bool _secure;

    [ObservableProperty]
    private bool _allowInvalidCertificate;

    [ObservableProperty]
    private bool _saveProfile;

    [ObservableProperty]
    private string _testStatus = string.Empty;

    [ObservableProperty]
    private bool _isTestError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private bool _isTesting;

    [ObservableProperty]
    private string? _validationMessage;

    public bool IsFtp => Protocol is RemoteProtocol.Ftp or RemoteProtocol.Ftps;
    public bool IsSmb => Protocol == RemoteProtocol.Smb;
    public bool IsWebDav => Protocol == RemoteProtocol.WebDav;
    public bool ShowAnonymous => IsFtp;
    public bool ShowAuthFields => !Anonymous;
    public bool ShowTlsOptions => Protocol is RemoteProtocol.Ftps or RemoteProtocol.WebDav;
    public bool CanTest => !IsTesting && !string.IsNullOrWhiteSpace(Host);
    public string DefaultPortText => new ConnectionProfile { Protocol = Protocol, Secure = Secure }.EffectivePort.ToString();

    partial void OnProtocolChanged(RemoteProtocol value)
    {
        PortText = string.Empty;
    }

    public void SetProtocol(string protocol)
    {
        if (Enum.TryParse<RemoteProtocol>(protocol, true, out var value))
        {
            Protocol = value;
            if (value == RemoteProtocol.Ftps)
                Secure = true;
        }
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        if (IsTesting)
            return;

        var profile = BuildProfile();
        if (profile is null)
        {
            IsTestError = true;
            TestStatus = ValidationMessage ?? L10n.T("请检查填写内容");
            return;
        }

        IsTesting = true;
        IsTestError = false;
        TestStatus = L10n.T("正在连接…");
        try
        {
            await _fileSystems.TestAsync(profile, Password, cancellationToken).ConfigureAwait(true);
            TestStatus = L10n.T("连接成功");
            IsTestError = false;
        }
        catch (Exception ex)
        {
            TestStatus = ex.Message;
            IsTestError = true;
        }
        finally
        {
            IsTesting = false;
        }
    }

    public ConnectionProfile? BuildProfile()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(Host))
        {
            ValidationMessage = L10n.T("服务器地址不能为空");
            return null;
        }

        var port = 0;
        if (!string.IsNullOrWhiteSpace(PortText) &&
            (!int.TryParse(PortText, out port) || port is < 1 or > 65535))
        {
            ValidationMessage = L10n.T("端口必须是 1-65535 之间的数字");
            return null;
        }

        var username = Anonymous ? "anonymous" : Username.Trim();
        if (!Anonymous && string.IsNullOrWhiteSpace(username) && Protocol is RemoteProtocol.Sftp or RemoteProtocol.Smb)
        {
            ValidationMessage = L10n.T("请输入用户名");
            return null;
        }

        var path = string.IsNullOrWhiteSpace(InitialPath) ? "/" : InitialPath.Trim();
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
            path = path[1..];
        if (!path.StartsWith('/'))
            path = "/" + path;

        var profile = _source.Clone();
        profile.Protocol = Protocol;
        profile.Host = Host.Trim();
        profile.Port = port;
        profile.Name = string.IsNullOrWhiteSpace(Name) ? ConnectionStore.DefaultName(profile) : Name.Trim();
        profile.Username = username;
        profile.Domain = Domain.Trim();
        profile.InitialPath = path;
        profile.Anonymous = Anonymous;
        profile.SavePassword = SavePassword && !Anonymous;
        profile.Secure = Secure;
        profile.AllowInvalidCertificate = AllowInvalidCertificate;
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = ConnectionStore.DefaultName(profile);
        return profile;
    }

    public bool Validate()
    {
        var profile = BuildProfile();
        return profile is not null;
    }
}

public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private readonly AppSettings _original;

    public SettingsDialogViewModel(AppSettings settings)
    {
        _original = settings;
        _language = settings.Language;
        _theme = settings.Theme;
        _accent = settings.Accent;
        _compactMode = settings.CompactMode;
        _showHidden = settings.ShowHidden;
        _foldersFirst = settings.FoldersFirst;
        _view = settings.View;
        _confirmPermanentDelete = settings.ConfirmPermanentDelete;
        _restoreSession = settings.RestoreSession;
        _showSidebar = settings.ShowSidebar;
        _showThumbnails = settings.ShowThumbnails;
        _showTransferPanel = settings.ShowTransferPanel;
        _searchDepth = settings.SearchDepth;
        _maxSearchResults = settings.MaxSearchResults;
    }

    [ObservableProperty]
    private UiLanguage _language;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private AccentKind _accent;

    [ObservableProperty]
    private bool _compactMode;

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private bool _foldersFirst;

    [ObservableProperty]
    private ViewKind _view;

    [ObservableProperty]
    private bool _confirmPermanentDelete;

    [ObservableProperty]
    private bool _restoreSession;

    [ObservableProperty]
    private bool _showSidebar;

    [ObservableProperty]
    private bool _showThumbnails;

    [ObservableProperty]
    private bool _showTransferPanel;

    [ObservableProperty]
    private decimal _searchDepth;

    [ObservableProperty]
    private decimal _maxSearchResults;

    public string SettingsPath => AppPaths.SettingsFile;

    public AppSettings ToSettings()
    {
        var settings = _original.Clone();
        settings.Language = Language;
        settings.Theme = Theme;
        settings.Accent = Accent;
        settings.CompactMode = CompactMode;
        settings.ShowHidden = ShowHidden;
        settings.FoldersFirst = FoldersFirst;
        settings.View = View;
        settings.ConfirmPermanentDelete = ConfirmPermanentDelete;
        settings.RestoreSession = RestoreSession;
        settings.ShowSidebar = ShowSidebar;
        settings.ShowThumbnails = ShowThumbnails;
        settings.ShowTransferPanel = ShowTransferPanel;
        settings.SearchDepth = Math.Clamp((int)SearchDepth, 1, 64);
        settings.MaxSearchResults = Math.Clamp((int)MaxSearchResults, 100, 20000);
        return settings;
    }

    public void Reset()
    {
        var defaults = new AppSettings();
        Language = defaults.Language;
        Theme = defaults.Theme;
        Accent = defaults.Accent;
        CompactMode = defaults.CompactMode;
        ShowHidden = defaults.ShowHidden;
        FoldersFirst = defaults.FoldersFirst;
        View = defaults.View;
        ConfirmPermanentDelete = defaults.ConfirmPermanentDelete;
        RestoreSession = defaults.RestoreSession;
        ShowSidebar = defaults.ShowSidebar;
        ShowThumbnails = defaults.ShowThumbnails;
        ShowTransferPanel = defaults.ShowTransferPanel;
        SearchDepth = defaults.SearchDepth;
        MaxSearchResults = defaults.MaxSearchResults;
    }

    public void SetLanguage(string value)
    {
        if (Enum.TryParse<UiLanguage>(value, true, out var language))
            Language = language;
    }

    public void SetTheme(string value)
    {
        if (Enum.TryParse<AppTheme>(value, true, out var theme))
            Theme = theme;
    }

    public void SetView(string value)
    {
        if (Enum.TryParse<ViewKind>(value, true, out var view))
            View = view;
    }

    public void SetAccent(string value)
    {
        if (Enum.TryParse<AccentKind>(value, true, out var accent))
            Accent = accent;
    }
}
