using Ordoa.Core.Localization;
namespace Ordoa.Core.Models;

public enum RemoteProtocol
{
    Ftp,
    Ftps,
    Sftp,
    Smb,
    WebDav,
    S3,
}

public enum SshAuthMethod
{
    Password,
    PrivateKey,
    Agent,
}

/// <summary>Per-hop credentials for an SSH ProxyJump chain.</summary>
public sealed class SshJumpProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public SshAuthMethod SshAuth { get; set; } = SshAuthMethod.Password;
    public string PrivateKeyPath { get; set; } = string.Empty;

    public SshJumpProfile Clone() => (SshJumpProfile)MemberwiseClone();
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
    /// <summary>OpenSSH ProxyJump specification, for example "bastion" or "user@jump:2222".</summary>
    public string ProxyJump { get; set; } = string.Empty;
    /// <summary>Per-hop credentials. When present, this takes precedence over parsing ProxyJump.</summary>
    public List<SshJumpProfile> SshJumps { get; set; } = [];
    /// <summary>Last connection error, kept so failed entries remain editable in the sidebar.</summary>
    public string? LastError { get; set; }
    public bool Secure { get; set; } = true;
    public string? HostKeyFingerprint { get; set; }
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>S3-compatible bucket. The Host field holds the endpoint.</summary>
    public string S3Bucket { get; set; } = string.Empty;
    public string S3Region { get; set; } = "us-east-1";
    /// <summary>Use path-style URLs (required by MinIO and many S3-compatible services).</summary>
    public bool S3UsePathStyle { get; set; } = true;
    /// <summary>Optional temporary-credential session token.</summary>
    public string S3SessionToken { get; set; } = string.Empty;

    public int EffectivePort => Port > 0 ? Port : DefaultPort;

    public int DefaultPort => Protocol switch
    {
        RemoteProtocol.Ftp or RemoteProtocol.Ftps => 21,
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.Smb => 445,
        RemoteProtocol.WebDav => Secure ? 443 : 80,
        RemoteProtocol.S3 => Secure ? 443 : 80,
        _ => 21,
    };

    public string Scheme => Protocol switch
    {
        RemoteProtocol.Ftp => "ftp",
        RemoteProtocol.Ftps => "ftps",
        RemoteProtocol.Sftp => "sftp",
        RemoteProtocol.Smb => "smb",
        RemoteProtocol.WebDav => "webdav",
        RemoteProtocol.S3 => "s3",
        _ => "remote",
    };

    public string DisplayTarget => Protocol switch
    {
        RemoteProtocol.Ftp or RemoteProtocol.Ftps or RemoteProtocol.Sftp
            => $"{Username}@{Host}:{EffectivePort}",
        RemoteProtocol.Smb => $"\\\\{Host}\\{(string.IsNullOrWhiteSpace(InitialPath) ? string.Empty : InitialPath.Trim('/').Split('/')[0])}",
        RemoteProtocol.WebDav => $"{(Secure ? "https" : "http")}://{Host}:{EffectivePort}",
        RemoteProtocol.S3 => $"{S3Bucket}@{(Secure ? "https" : "http")}://{Host}:{EffectivePort}",
        _ => Host,
    };

    public string BuildUri(string path)
    {
        if (Protocol == RemoteProtocol.S3)
            return $"s3://{S3Bucket}{NormalizeInitialPath(path)}";
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

    public ConnectionProfile Clone()
    {
        var clone = (ConnectionProfile)MemberwiseClone();
        clone.SshJumps = (SshJumps ?? []).Select(jump => jump.Clone()).ToList();
        return clone;
    }

    public string ProtocolLabel => Protocol switch
    {
        RemoteProtocol.Ftp => "FTP",
        RemoteProtocol.Ftps => "FTPS",
        RemoteProtocol.Sftp => "SFTP",
        RemoteProtocol.Smb => "SMB",
        RemoteProtocol.WebDav => Secure ? "WebDAV (HTTPS)" : "WebDAV",
        RemoteProtocol.S3 => "S3",
        _ => L10n.T("远程"),
    };
}
