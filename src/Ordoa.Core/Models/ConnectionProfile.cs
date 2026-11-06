using Ordoa.Core.Localization;
namespace Ordoa.Core.Models;

public enum RemoteProtocol
{
    Ftp,
    Ftps,
    Sftp,
    Smb,
    WebDav,
}

public enum SshAuthMethod
{
    Password,
    PrivateKey,
    Agent,
}

public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Sftp;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string InitialPath { get; set; } = "/";
    public bool Anonymous { get; set; }
    public bool SavePassword { get; set; } = true;
    /// <summary>Authentication used by SFTP connections.</summary>
    public SshAuthMethod SshAuth { get; set; } = SshAuthMethod.Password;
    /// <summary>Path to an OpenSSH/PEM private key for SFTP private-key auth.</summary>
    public string PrivateKeyPath { get; set; } = string.Empty;
    /// <summary>Last connection error, kept so failed entries remain editable in the sidebar.</summary>
    public string? LastError { get; set; }
    public bool Secure { get; set; } = true;
    public string? HostKeyFingerprint { get; set; }
    public bool AllowInvalidCertificate { get; set; }

    public int EffectivePort => Port > 0 ? Port : DefaultPort;

    public int DefaultPort => Protocol switch
    {
        RemoteProtocol.Ftp or RemoteProtocol.Ftps => 21,
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.Smb => 445,
        RemoteProtocol.WebDav => Secure ? 443 : 80,
        _ => 21,
    };

    public string Scheme => Protocol switch
    {
        RemoteProtocol.Ftp => "ftp",
        RemoteProtocol.Ftps => "ftps",
        RemoteProtocol.Sftp => "sftp",
        RemoteProtocol.Smb => "smb",
        RemoteProtocol.WebDav => "webdav",
        _ => "remote",
    };

    public string DisplayTarget => Protocol switch
    {
        RemoteProtocol.Ftp or RemoteProtocol.Ftps or RemoteProtocol.Sftp
            => $"{Username}@{Host}:{EffectivePort}",
        RemoteProtocol.Smb => $"\\\\{Host}\\{(string.IsNullOrWhiteSpace(InitialPath) ? string.Empty : InitialPath.Trim('/').Split('/')[0])}",
        RemoteProtocol.WebDav => $"{(Secure ? "https" : "http")}://{Host}:{EffectivePort}",
        _ => Host,
    };

    public string BuildUri(string path)
    {
        var user = string.IsNullOrEmpty(Username) ? string.Empty : Uri.EscapeDataString(Username) + "@";
        return $"{Scheme}://{user}{Host}:{EffectivePort}{NormalizeInitialPath(path)}";
    }

    public string NormalizeInitialPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        path = path.Trim();
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
            path = path[1..];
        if (!path.StartsWith('/'))
            path = "/" + path;
        return path;
    }

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();

    public string ProtocolLabel => Protocol switch
    {
        RemoteProtocol.Ftp => "FTP",
        RemoteProtocol.Ftps => "FTPS",
        RemoteProtocol.Sftp => "SFTP",
        RemoteProtocol.Smb => "SMB",
        RemoteProtocol.WebDav => Secure ? "WebDAV (HTTPS)" : "WebDAV",
        _ => L10n.T("远程"),
    };
}
