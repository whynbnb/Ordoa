using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
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
        _sshAuth = _source.SshAuth;
        _privateKeyPath = _source.PrivateKeyPath;
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
    [NotifyPropertyChangedFor(nameof(IsSftp))]
    [NotifyPropertyChangedFor(nameof(ShowSshAuth))]
    [NotifyPropertyChangedFor(nameof(ShowSecretField))]
    [NotifyPropertyChangedFor(nameof(ShowPrivateKeyField))]
    [NotifyPropertyChangedFor(nameof(ShowAgentHint))]
    [NotifyPropertyChangedFor(nameof(ShowSaveSecret))]
    [NotifyPropertyChangedFor(nameof(ShowKnownHostsHint))]
    [NotifyPropertyChangedFor(nameof(SecretLabel))]
    [NotifyPropertyChangedFor(nameof(SecretPlaceholder))]
    [NotifyPropertyChangedFor(nameof(SaveSecretLabel))]
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
    [NotifyPropertyChangedFor(nameof(ShowSshAuth))]
    [NotifyPropertyChangedFor(nameof(ShowSecretField))]
    [NotifyPropertyChangedFor(nameof(ShowPrivateKeyField))]
    [NotifyPropertyChangedFor(nameof(ShowAgentHint))]
    [NotifyPropertyChangedFor(nameof(ShowSaveSecret))]
    private bool _anonymous;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSecretField))]
    [NotifyPropertyChangedFor(nameof(ShowPrivateKeyField))]
    [NotifyPropertyChangedFor(nameof(ShowAgentHint))]
    [NotifyPropertyChangedFor(nameof(ShowSaveSecret))]
    [NotifyPropertyChangedFor(nameof(SecretLabel))]
    [NotifyPropertyChangedFor(nameof(SecretPlaceholder))]
    [NotifyPropertyChangedFor(nameof(SaveSecretLabel))]
    private SshAuthMethod _sshAuth;

    [ObservableProperty]
    private string _privateKeyPath;

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
    public bool IsSftp => Protocol == RemoteProtocol.Sftp;
    public bool ShowAnonymous => IsFtp;
    public bool ShowAuthFields => !Anonymous;
    public bool ShowTlsOptions => Protocol is RemoteProtocol.Ftps or RemoteProtocol.WebDav;
    public bool ShowSshAuth => IsSftp && !Anonymous;
    public bool ShowSecretField => ShowAuthFields && (!IsSftp || SshAuth != SshAuthMethod.Agent);
    public bool ShowPrivateKeyField => IsSftp && SshAuth == SshAuthMethod.PrivateKey;
    public bool ShowAgentHint => IsSftp && SshAuth == SshAuthMethod.Agent;
    public bool ShowSaveSecret => ShowSecretField;
    public bool ShowKnownHostsHint => IsSftp;
    public string SecretLabel => IsSftp && SshAuth == SshAuthMethod.PrivateKey
        ? L10n.T("私钥密码（可选）")
        : L10n.T("密码");
    public string SecretPlaceholder => IsSftp && SshAuth == SshAuthMethod.PrivateKey
        ? L10n.T("私钥未加密时留空")
        : L10n.T("登录密码");
    public string SaveSecretLabel => IsSftp && SshAuth == SshAuthMethod.PrivateKey
        ? L10n.T("保存私钥密码到系统密钥环")
        : L10n.T("保存密码到系统密钥环");
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

    public void SetSshAuth(string method)
    {
        if (Enum.TryParse<SshAuthMethod>(method, true, out var value))
            SshAuth = value;
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

        if (Protocol == RemoteProtocol.Sftp && SshAuth == SshAuthMethod.PrivateKey &&
            string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            ValidationMessage = L10n.T("请选择私钥文件");
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
        profile.SavePassword = SavePassword && !Anonymous &&
                               !(Protocol == RemoteProtocol.Sftp && SshAuth == SshAuthMethod.Agent);
        profile.SshAuth = SshAuth;
        profile.PrivateKeyPath = (PrivateKeyPath ?? string.Empty).Trim();
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

public sealed partial class PlaceVisibilityViewModel : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    [ObservableProperty]
    private bool _isVisible;
}

public sealed class CustomPathViewModel
{
    public required string Path { get; init; }
    public string Label => KnownFolders.DisplayNameForPath(Path);
    public string Detail => KnownFolders.ShortenForDisplay(Path);
}

public sealed class SettingsCategoryViewModel
{
    public required string Title { get; init; }
    public Geometry? Icon { get; init; }
}

public sealed partial class ShortcutSettingViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string DefaultGesture { get; init; }

    private string _gesture = string.Empty;
    public string Gesture
    {
        get => _gesture;
        set
        {
            if (!SetProperty(ref _gesture, value))
                return;
            Error = string.IsNullOrWhiteSpace(value) || ShortcutService.TryParse(value, out _)
                ? null
                : L10n.T("无法识别这个快捷键，请使用 Ctrl+Shift+T 这样的格式。");
            OnPropertyChanged(nameof(Error));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsDefault));
        }
    }

    public string? Error { get; private set; }
    public bool HasError => Error is not null;
    public bool IsDefault => string.Equals(
        ShortcutService.Normalize(Gesture),
        ShortcutService.Normalize(DefaultGesture),
        StringComparison.Ordinal);
}

public sealed partial class ConnectionSettingViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public string? Error { get; init; }
    public bool IsFailed => !string.IsNullOrEmpty(Error);

    [ObservableProperty]
    private bool _isVisible;
}

public sealed partial class TerminalSettingViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public bool IsHerdr { get; init; }
    public bool CanBeDefault => !IsHerdr;

    [ObservableProperty]
    private bool _isDefault;

    [ObservableProperty]
    private bool _showInContextMenu;
}

public sealed partial class CustomCommandViewModel : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = "%d";

    [ObservableProperty]
    private bool _showInContextMenu = true;

    [ObservableProperty]
    private bool _runInTerminal;

    [ObservableProperty]
    private bool _requiresSelection;
}

public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private readonly AppSettings _original;
    private readonly Dictionary<string, string> _openWithDefaults;
    private readonly FileSystemManager? _fileSystems;
    private readonly TerminalService? _terminals;

    public SettingsDialogViewModel(AppSettings settings, FileSystemManager? fileSystems = null,
        TerminalService? terminals = null)
    {
        _original = settings;
        _fileSystems = fileSystems;
        _terminals = terminals;
        _openWithDefaults = new Dictionary<string, string>(
            settings.OpenWithDefaults ?? [], StringComparer.Ordinal);
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
        _showVolumes = settings.ShowVolumes;
        _showHiddenVolumes = settings.ShowHiddenVolumes;
        _showThumbnails = settings.ShowThumbnails;
        _showTransferPanel = settings.ShowTransferPanel;
        _showPreviewPanel = settings.ShowPreviewPanel;
        _previewPanelPlacementIndex = settings.PreviewPanelPlacement == PreviewPanelPlacement.Bottom ? 1 : 0;
        _searchDepth = settings.SearchDepth;
        _maxSearchResults = settings.MaxSearchResults;
        _ffmpegPath = settings.FfmpegPath;
        _useSystemWindowFrame = settings.UseSystemWindowFrame;
        _windowButtonMode = settings.WindowButtonMode;
        _singleInstance = settings.SingleInstance;
        _allowMultipleWindows = settings.AllowMultipleWindows;
        _folderOpenIndex = settings.FolderOpenMode == FolderOpenMode.NewWindow ? 1 : 0;
        _undoBehaviorIndex = (int)settings.UndoBehavior;
        _terminalMode = settings.TerminalMode;
        _manualTerminalName = settings.ManualTerminalName;
        _manualTerminalPath = settings.ManualTerminalPath;
        _manualTerminalArguments = settings.ManualTerminalArguments;
        _manualTerminalCommandArguments = settings.ManualTerminalCommandArguments;
        FolderOpenModes =
        [
            L10n.T("当前窗口"),
            L10n.T("新窗口"),
        ];
        UndoBehaviors =
        [
            L10n.T("跟随系统"),
            L10n.T("完整（包括复制）"),
            L10n.T("仅结构操作"),
            L10n.T("关闭"),
        ];
        PreviewPanelPlacements =
        [
            L10n.T("右侧"),
            L10n.T("底部"),
        ];

        Categories =
        [
            new SettingsCategoryViewModel { Title = L10n.T("外观"), Icon = FileIconCatalog.Resource("IconApps") },
            new SettingsCategoryViewModel { Title = L10n.T("浏览"), Icon = FileIconCatalog.Resource("IconFolder") },
            new SettingsCategoryViewModel { Title = L10n.T("侧栏"), Icon = FileIconCatalog.Resource("IconPanel") },
            new SettingsCategoryViewModel { Title = L10n.T("预览"), Icon = FileIconCatalog.Resource("IconEye") },
            new SettingsCategoryViewModel { Title = L10n.T("终端"), Icon = FileIconCatalog.Resource("IconTerminal") },
            new SettingsCategoryViewModel { Title = L10n.T("命令"), Icon = FileIconCatalog.Resource("IconCommand") },
            new SettingsCategoryViewModel { Title = L10n.T("网络"), Icon = FileIconCatalog.Resource("IconCloud") },
            new SettingsCategoryViewModel { Title = L10n.T("行为与撤销"), Icon = FileIconCatalog.Resource("IconUndo") },
            new SettingsCategoryViewModel { Title = L10n.T("系统与集成"), Icon = FileIconCatalog.Resource("IconSettings") },
            new SettingsCategoryViewModel { Title = L10n.T("快捷键"), Icon = FileIconCatalog.Resource("IconKeyboard") },
        ];

        foreach (var definition in ShortcutService.Definitions)
        {
            var gesture = settings.ShortcutOverrides.TryGetValue(definition.Id, out var custom)
                ? custom
                : definition.DefaultGesture;
            Shortcuts.Add(new ShortcutSettingViewModel
            {
                Id = definition.Id,
                Label = definition.Label,
                DefaultGesture = definition.DefaultGesture,
                Gesture = gesture,
            });
        }

        ReloadConnections();
        ReloadTerminals();

        foreach (var path in settings.CustomSidebarPaths ?? [])
            CustomPaths.Add(new CustomPathViewModel { Path = path });

        foreach (var command in settings.CustomCommands ?? [])
        {
            CustomCommands.Add(new CustomCommandViewModel
            {
                Id = command.Id,
                Name = command.Name,
                Command = command.Command,
                WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? "%d" : command.WorkingDirectory,
                ShowInContextMenu = command.ShowInContextMenu,
                RunInTerminal = command.RunInTerminal,
                RequiresSelection = command.RequiresSelection,
            });
        }

        var hidden = new HashSet<string>(settings.HiddenPlaces ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var place in KnownFolders.GetPlaces())
        {
            Places.Add(new PlaceVisibilityViewModel
            {
                Key = place.Key,
                Label = place.Label,
                IsVisible = !hidden.Contains(place.Key),
            });
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<PlaceVisibilityViewModel> Places { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<CustomPathViewModel> CustomPaths { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<ConnectionSettingViewModel> Connections { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<TerminalSettingViewModel> Terminals { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<CustomCommandViewModel> CustomCommands { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<ShortcutSettingViewModel> Shortcuts { get; } = [];

    public bool HasConnections => Connections.Count > 0;

    public bool HasTerminals => Terminals.Count > 0;

    public bool HasCustomCommands => CustomCommands.Count > 0;

    public string TerminalScanSummary
    {
        get
        {
            var detected = Terminals.Count(item => !item.IsHerdr);
            return detected == 0
                ? L10n.T("未检测到终端")
                : L10n.Format("已检测到 {0} 个终端", detected);
        }
    }

    public IReadOnlyList<SettingsCategoryViewModel> Categories { get; }

    [ObservableProperty]
    private int _selectedCategoryIndex;

    public bool ShowAppearance => SelectedCategoryIndex == 0;
    public bool ShowBrowsing => SelectedCategoryIndex == 1;
    public bool ShowSidebarCategory => SelectedCategoryIndex == 2;
    public bool ShowPreview => SelectedCategoryIndex == 3;
    public bool ShowTerminal => SelectedCategoryIndex == 4;
    public bool ShowCommands => SelectedCategoryIndex == 5;
    public bool ShowNetwork => SelectedCategoryIndex == 6;
    public bool ShowBehavior => SelectedCategoryIndex == 7;
    public bool ShowSystem => SelectedCategoryIndex == 8;
    public bool ShowShortcuts => SelectedCategoryIndex == 9;

    partial void OnSelectedCategoryIndexChanged(int value)
    {
        if (Categories.Count > 0 && (value < 0 || value >= Categories.Count))
        {
            SelectedCategoryIndex = Math.Clamp(value, 0, Categories.Count - 1);
            return;
        }

        OnPropertyChanged(nameof(ShowAppearance));
        OnPropertyChanged(nameof(ShowBrowsing));
        OnPropertyChanged(nameof(ShowSidebarCategory));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(ShowTerminal));
        OnPropertyChanged(nameof(ShowCommands));
        OnPropertyChanged(nameof(ShowNetwork));
        OnPropertyChanged(nameof(ShowBehavior));
        OnPropertyChanged(nameof(ShowSystem));
        OnPropertyChanged(nameof(ShowShortcuts));
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
    private bool _showVolumes;

    [ObservableProperty]
    private bool _showHiddenVolumes;

    [ObservableProperty]
    private string _newCustomPath = string.Empty;

    [ObservableProperty]
    private string? _customPathError;

    [ObservableProperty]
    private bool _showThumbnails;

    [ObservableProperty]
    private bool _showTransferPanel;

    [ObservableProperty]
    private bool _showPreviewPanel;

    [ObservableProperty]
    private int _previewPanelPlacementIndex;

    [ObservableProperty]
    private decimal _searchDepth;

    [ObservableProperty]
    private decimal _maxSearchResults;

    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    [ObservableProperty]
    private bool _useSystemWindowFrame;

    [ObservableProperty]
    private WindowButtonMode _windowButtonMode;

    [ObservableProperty]
    private bool _singleInstance;

    [ObservableProperty]
    private bool _allowMultipleWindows;

    [ObservableProperty]
    private int _folderOpenIndex;

    [ObservableProperty]
    private int _undoBehaviorIndex;

    [ObservableProperty]
    private TerminalDetectionMode _terminalMode;

    partial void OnTerminalModeChanged(TerminalDetectionMode value)
    {
        OnPropertyChanged(nameof(ShowTerminalAuto));
        OnPropertyChanged(nameof(ShowTerminalManual));
        if (value == TerminalDetectionMode.Auto)
            TerminalError = null;
    }

    public bool ShowTerminalAuto => TerminalMode == TerminalDetectionMode.Auto;
    public bool ShowTerminalManual => !ShowTerminalAuto;

    [ObservableProperty]
    private string _manualTerminalName = string.Empty;

    [ObservableProperty]
    private string _manualTerminalPath = string.Empty;

    partial void OnManualTerminalPathChanged(string value) => TerminalError = null;

    [ObservableProperty]
    private string _manualTerminalArguments = string.Empty;

    [ObservableProperty]
    private string _manualTerminalCommandArguments = string.Empty;

    [ObservableProperty]
    private string? _terminalError;

    public bool ValidateTerminalSettings()
    {
        if (TerminalMode == TerminalDetectionMode.Manual)
        {
            var path = (ManualTerminalPath ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                TerminalError = L10n.T("手动模式需要指定终端可执行文件。");
                return false;
            }

            if (path == "~")
                path = AppPaths.Home;
            else if (path.StartsWith("~/", StringComparison.Ordinal))
                path = Path.Combine(AppPaths.Home, path[2..]);

            var resolved = TerminalCatalog.FindExecutable(path);
            if (resolved is null && !File.Exists(path))
            {
                TerminalError = L10n.T("找不到指定的终端可执行文件。");
                return false;
            }
        }

        TerminalError = null;
        return true;
    }

    [ObservableProperty]
    private string _undoStatus = string.Empty;

    [ObservableProperty]
    private bool _isRegisteringFileManager;

    [ObservableProperty]
    private string _integrationStatus = string.Empty;

    public IReadOnlyList<string> FolderOpenModes { get; }
    public IReadOnlyList<string> UndoBehaviors { get; }
    public IReadOnlyList<string> PreviewPanelPlacements { get; }

    public string SettingsPath => AppPaths.SettingsFile;

    public bool IsMacOS => OperatingSystem.IsMacOS();

    public bool IsLinux => OperatingSystem.IsLinux();

    public bool HasOpenWithDefaults => _openWithDefaults.Count > 0;

    public string OpenWithSummary => _openWithDefaults.Count == 0
        ? L10n.T("没有已记住的打开方式")
        : L10n.Format("已记住 {0} 个扩展名", _openWithDefaults.Count);

    public void SetIntegrationStatus(string message) => IntegrationStatus = message;

    public void SetUndoStatus(string message) => UndoStatus = message;

    public void ReloadConnections()
    {
        var visibility = Connections.ToDictionary(item => item.Id, item => item.IsVisible, StringComparer.Ordinal);
        var hidden = new HashSet<string>(_original.HiddenConnections ?? [], StringComparer.Ordinal);
        Connections.Clear();

        if (_fileSystems is not null)
        {
            foreach (var profile in _fileSystems.Connections.GetAll())
            {
                Connections.Add(new ConnectionSettingViewModel
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Detail = $"{profile.ProtocolLabel} · {profile.DisplayTarget}",
                    Error = profile.LastError,
                    IsVisible = visibility.TryGetValue(profile.Id, out var visible)
                        ? visible
                        : !hidden.Contains(profile.Id),
                });
            }
        }

        OnPropertyChanged(nameof(HasConnections));
    }

    public void ReloadTerminals(bool resetToDefaults = false)
    {
        var previous = new Dictionary<string, TerminalSettingViewModel>(StringComparer.Ordinal);
        foreach (var terminal in Terminals)
            previous[terminal.Id] = terminal;

        var preferences = new Dictionary<string, TerminalPreference>(StringComparer.Ordinal);
        foreach (var preference in resetToDefaults ? new AppSettings().TerminalPreferences
                     : _original.TerminalPreferences ?? [])
        {
            if (!string.IsNullOrWhiteSpace(preference.Id))
                preferences[preference.Id] = preference;
        }

        Terminals.Clear();
        if (_terminals is not null)
        {
            foreach (var profile in _terminals.Discover())
            {
                preferences.TryGetValue(profile.Id, out var preference);
                previous.TryGetValue(profile.Id, out var existing);
                Terminals.Add(new TerminalSettingViewModel
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Detail = profile.Executable,
                    IsHerdr = false,
                    IsDefault = existing?.IsDefault ?? preference?.IsDefault ?? false,
                    ShowInContextMenu = existing?.ShowInContextMenu ??
                                        preference?.ShowInContextMenu ?? true,
                });
            }

            if (_terminals.FindHerdr() is { } herdr)
            {
                preferences.TryGetValue("herdr", out var preference);
                previous.TryGetValue("herdr", out var existing);
                Terminals.Add(new TerminalSettingViewModel
                {
                    Id = "herdr",
                    Name = herdr.Name,
                    Detail = herdr.Executable,
                    IsHerdr = true,
                    IsDefault = false,
                    ShowInContextMenu = existing?.ShowInContextMenu ??
                                        preference?.ShowInContextMenu ?? false,
                });
            }
        }

        if (Terminals.All(item => !item.IsDefault || !item.CanBeDefault))
        {
            var first = Terminals.FirstOrDefault(item => item.CanBeDefault);
            if (first is not null)
                first.IsDefault = true;
        }

        OnPropertyChanged(nameof(HasTerminals));
        OnPropertyChanged(nameof(TerminalScanSummary));
    }

    public void SetDefaultTerminal(string id)
    {
        foreach (var terminal in Terminals)
            terminal.IsDefault = terminal.CanBeDefault &&
                                 string.Equals(terminal.Id, id, StringComparison.Ordinal);
    }

    public void SetTerminalMode(string value)
    {
        if (Enum.TryParse<TerminalDetectionMode>(value, true, out var mode))
            TerminalMode = mode;
    }

    public void AddCustomCommand()
    {
        CustomCommands.Add(new CustomCommandViewModel
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkingDirectory = "%d",
            ShowInContextMenu = true,
        });
        OnPropertyChanged(nameof(HasCustomCommands));
    }

    public void RemoveCustomCommand(string id)
    {
        var command = CustomCommands.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal));
        if (command is null)
            return;

        CustomCommands.Remove(command);
        OnPropertyChanged(nameof(HasCustomCommands));
    }

    public void ClearOpenWithDefaults()
    {
        if (_openWithDefaults.Count == 0)
            return;

        _openWithDefaults.Clear();
        OnPropertyChanged(nameof(HasOpenWithDefaults));
        OnPropertyChanged(nameof(OpenWithSummary));
    }

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
        settings.ShowVolumes = ShowVolumes;
        settings.ShowHiddenVolumes = ShowHiddenVolumes;
        settings.ShowThumbnails = ShowThumbnails;
        settings.ShowTransferPanel = ShowTransferPanel;
        settings.ShowPreviewPanel = ShowPreviewPanel;
        settings.PreviewPanelPlacement = PreviewPanelPlacementIndex == 1
            ? PreviewPanelPlacement.Bottom
            : PreviewPanelPlacement.Right;
        settings.SearchDepth = Math.Clamp((int)SearchDepth, 1, 64);
        settings.MaxSearchResults = Math.Clamp((int)MaxSearchResults, 100, 20000);
        settings.FfmpegPath = (FfmpegPath ?? string.Empty).Trim();
        settings.UseSystemWindowFrame = UseSystemWindowFrame;
        settings.WindowButtonMode = WindowButtonMode;
        settings.SingleInstance = SingleInstance;
        settings.AllowMultipleWindows = AllowMultipleWindows;
        settings.FolderOpenMode = AllowMultipleWindows && FolderOpenIndex == 1
            ? FolderOpenMode.NewWindow
            : FolderOpenMode.CurrentWindow;
        settings.UndoBehavior = (UndoBehavior)Math.Clamp(UndoBehaviorIndex, 0, 3);
        settings.TerminalMode = TerminalMode;
        settings.ManualTerminalName = (ManualTerminalName ?? string.Empty).Trim();
        settings.ManualTerminalPath = (ManualTerminalPath ?? string.Empty).Trim();
        settings.ManualTerminalArguments = (ManualTerminalArguments ?? string.Empty).Trim();
        settings.ManualTerminalCommandArguments = (ManualTerminalCommandArguments ?? string.Empty).Trim();
        settings.TerminalPreferences = Terminals
            .Select(terminal => new TerminalPreference
            {
                Id = terminal.Id,
                IsDefault = terminal.IsDefault,
                ShowInContextMenu = terminal.ShowInContextMenu,
            })
            .ToList();
        settings.CustomCommands = CustomCommands
            .Where(command => !string.IsNullOrWhiteSpace(command.Command))
            .Select(command => new CustomCommand
            {
                Id = command.Id,
                Name = string.IsNullOrWhiteSpace(command.Name)
                    ? FirstWord(command.Command)
                    : command.Name.Trim(),
                Command = command.Command.Trim(),
                WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory)
                    ? "%d"
                    : command.WorkingDirectory.Trim(),
                ShowInContextMenu = command.ShowInContextMenu,
                RunInTerminal = command.RunInTerminal,
                RequiresSelection = command.RequiresSelection,
            })
            .ToList();
        settings.OpenWithDefaults = new Dictionary<string, string>(_openWithDefaults, StringComparer.Ordinal);
        settings.ShortcutOverrides = Shortcuts
            .Where(shortcut => string.IsNullOrWhiteSpace(shortcut.Gesture) ||
                               ShortcutService.TryParse(shortcut.Gesture, out _))
            .Select(shortcut => new
            {
                shortcut.Id,
                Gesture = ShortcutService.Normalize(shortcut.Gesture),
                Default = ShortcutService.Normalize(shortcut.DefaultGesture),
            })
            .Where(item => !string.Equals(item.Gesture, item.Default, StringComparison.Ordinal))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Gesture, StringComparer.Ordinal);
        settings.CustomSidebarPaths = CustomPaths.Select(item => item.Path).ToList();
        settings.HiddenPlaces = Places.Where(place => !place.IsVisible).Select(place => place.Key).ToList();
        if (_fileSystems is not null)
            settings.HiddenConnections = Connections.Where(item => !item.IsVisible).Select(item => item.Id).ToList();
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
        ShowVolumes = defaults.ShowVolumes;
        ShowHiddenVolumes = defaults.ShowHiddenVolumes;
        ShowThumbnails = defaults.ShowThumbnails;
        ShowTransferPanel = defaults.ShowTransferPanel;
        ShowPreviewPanel = defaults.ShowPreviewPanel;
        PreviewPanelPlacementIndex = defaults.PreviewPanelPlacement == PreviewPanelPlacement.Bottom ? 1 : 0;
        SearchDepth = defaults.SearchDepth;
        MaxSearchResults = defaults.MaxSearchResults;
        FfmpegPath = defaults.FfmpegPath;
        UseSystemWindowFrame = defaults.UseSystemWindowFrame;
        WindowButtonMode = defaults.WindowButtonMode;
        SingleInstance = defaults.SingleInstance;
        AllowMultipleWindows = defaults.AllowMultipleWindows;
        FolderOpenIndex = defaults.FolderOpenMode == FolderOpenMode.NewWindow ? 1 : 0;
        UndoBehaviorIndex = (int)defaults.UndoBehavior;
        TerminalMode = defaults.TerminalMode;
        ManualTerminalName = defaults.ManualTerminalName;
        ManualTerminalPath = defaults.ManualTerminalPath;
        ManualTerminalArguments = defaults.ManualTerminalArguments;
        ManualTerminalCommandArguments = defaults.ManualTerminalCommandArguments;
        ReloadTerminals(resetToDefaults: true);
        CustomCommands.Clear();
        OnPropertyChanged(nameof(HasCustomCommands));
        _openWithDefaults.Clear();
        OnPropertyChanged(nameof(HasOpenWithDefaults));
        OnPropertyChanged(nameof(OpenWithSummary));
        CustomPaths.Clear();
        NewCustomPath = string.Empty;
        CustomPathError = null;
        foreach (var connection in Connections)
            connection.IsVisible = true;
        ResetPlaces();
        ResetShortcuts();
    }

    public void ResetShortcuts()
    {
        foreach (var shortcut in Shortcuts)
            shortcut.Gesture = shortcut.DefaultGesture;
    }

    public void ResetPlaces()
    {
        foreach (var place in Places)
            place.IsVisible = true;
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

    public void SetWindowButtonMode(string value)
    {
        if (Enum.TryParse<WindowButtonMode>(value, true, out var mode))
            WindowButtonMode = mode;
    }

    public bool AddCustomPath()
    {
        var input = (NewCustomPath ?? string.Empty).Trim().Trim('"');
        if (input.Length == 0)
        {
            CustomPathError = L10n.T("请输入路径。");
            return false;
        }

        if (input == "~")
            input = AppPaths.Home;
        else if (input.StartsWith("~/", StringComparison.Ordinal))
            input = Path.Combine(AppPaths.Home, input[2..]);

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        }
        catch
        {
            CustomPathError = L10n.T("路径格式无效。");
            return false;
        }

        if (!Directory.Exists(full))
        {
            CustomPathError = L10n.T("路径不存在或不是文件夹。");
            return false;
        }

        if (CustomPaths.Any(item => string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase)))
        {
            CustomPathError = L10n.T("该路径已添加。");
            return false;
        }

        CustomPaths.Add(new CustomPathViewModel { Path = full });
        NewCustomPath = string.Empty;
        CustomPathError = null;
        return true;
    }

    public void RemoveCustomPath(string path)
    {
        var item = CustomPaths.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            CustomPaths.Remove(item);
        CustomPathError = null;
    }

    private static string FirstWord(string command)
    {
        var trimmed = command.Trim();
        var index = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return index > 0 ? trimmed[..index] : trimmed;
    }
}
