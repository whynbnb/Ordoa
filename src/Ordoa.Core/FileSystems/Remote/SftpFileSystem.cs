using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SshNet.Agent;
using Ordoa.Core.Localization;

namespace Ordoa.Core.FileSystems.Remote;

/// <summary>SFTP provider backed by SSH.NET.</summary>
public sealed class SftpFileSystem : FileSystemBase
{
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConnectionProfile _profile;
    private readonly ConcurrentDictionary<string, EntryKind> _resolvedLinks = new(StringComparer.Ordinal);

    private SftpFileSystem(ConnectionProfile profile, SftpClient client)
        : base(profile.Id, profile.Scheme, profile.Name)
    {
        _profile = profile;
        _client = client;
    }

    public string? HostKeyFingerprint { get; private set; }
    public bool HostKeyAccepted { get; private set; }

    public override FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.List | FileSystemCapabilities.Read | FileSystemCapabilities.Write |
        FileSystemCapabilities.CreateDirectory | FileSystemCapabilities.Delete | FileSystemCapabilities.Rename |
        FileSystemCapabilities.Move | FileSystemCapabilities.Copy | FileSystemCapabilities.Symlinks |
        FileSystemCapabilities.UnixPermissions | FileSystemCapabilities.SetModifiedTime;

    public static Task<SftpFileSystem> ConnectAsync(ConnectionProfile profile, string password,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var methods = BuildAuthenticationMethods(profile, password);
            var connectionInfo = new ConnectionInfo(profile.Host, profile.EffectivePort, profile.Username, methods)
            {
                Timeout = TimeSpan.FromSeconds(25),
                ChannelCloseTimeout = TimeSpan.FromSeconds(15),
                RetryAttempts = 1,
            };

            var client = new SftpClient(connectionInfo)
            {
                OperationTimeout = TimeSpan.FromSeconds(60),
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            };

            SftpFileSystem? result = null;
            var fingerprint = string.Empty;
            string? hostKeyError = null;
            var knownHostsStatus = KnownHostsStatus.Unknown;
            client.HostKeyReceived += (_, e) =>
            {
                fingerprint = e.FingerPrintSHA256;
                var hostKey = e.HostKey ?? [];
                var keyType = GetHostKeyType(hostKey) ?? e.HostKeyName ?? string.Empty;
                var knownHosts = KnownHostsStore.Default.Check(
                    profile.Host, profile.EffectivePort, keyType, hostKey);
                knownHostsStatus = knownHosts.Status;

                if (knownHosts.Status == KnownHostsStatus.Revoked)
                {
                    e.CanTrust = false;
                    hostKeyError = L10n.T("服务器主机密钥已被 known_hosts 标记为吊销，连接已中止。");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(profile.HostKeyFingerprint))
                {
                    e.CanTrust = string.Equals(profile.HostKeyFingerprint, e.FingerPrintSHA256, StringComparison.Ordinal);
                    if (!e.CanTrust)
                        hostKeyError = L10n.T("服务器主机密钥与之前保存的指纹不一致，连接已中止。");
                    return;
                }

                switch (knownHosts.Status)
                {
                    case KnownHostsStatus.Match:
                        e.CanTrust = true;
                        break;
                    case KnownHostsStatus.Mismatch:
                        e.CanTrust = false;
                        hostKeyError = L10n.T("服务器主机密钥与 known_hosts 中的记录不一致，连接已中止。");
                        break;
                    default:
                        // Trust on first use; the fingerprint is pinned in the
                        // connection profile after a successful connection.
                        e.CanTrust = true;
                        break;
                }
            };

            try
            {
                client.Connect();
                result = new SftpFileSystem(profile, client)
                {
                    HostKeyFingerprint = string.IsNullOrWhiteSpace(profile.HostKeyFingerprint) ? fingerprint : profile.HostKeyFingerprint,
                    HostKeyAccepted = string.IsNullOrWhiteSpace(profile.HostKeyFingerprint) &&
                                      knownHostsStatus == KnownHostsStatus.Unknown,
                };
                return result;
            }
            catch (Exception ex) when (hostKeyError is not null)
            {
                client.Dispose();
                throw new IOException(hostKeyError, ex);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    private static AuthenticationMethod[] BuildAuthenticationMethods(ConnectionProfile profile, string secret)
    {
        var methods = new List<AuthenticationMethod>();
        switch (profile.SshAuth)
        {
            case SshAuthMethod.Agent:
                var identities = RequestAgentIdentities();
                if (identities.Length == 0)
                {
                    throw new InvalidOperationException(
                        L10n.T("SSH Agent 中没有可用身份，请先使用 ssh-add 添加密钥。"));
                }

                methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, identities));
                break;

            case SshAuthMethod.PrivateKey:
                var keyPath = ExpandPath(profile.PrivateKeyPath);
                if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
                    throw new FileNotFoundException(L10n.T("找不到私钥文件，请检查路径。"), profile.PrivateKeyPath);

                try
                {
                    var keyFile = new PrivateKeyFile(keyPath, string.IsNullOrEmpty(secret) ? null : secret);
                    methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, keyFile));
                }
                catch (SshPassPhraseNullOrEmptyException ex)
                {
                    throw new IOException(L10n.T("私钥已加密，请输入私钥密码。"), ex);
                }
                catch (SshException ex)
                {
                    throw new IOException(L10n.T("无法读取私钥文件，请检查文件格式和私钥密码。"), ex);
                }
                break;

            default:
                if (!string.IsNullOrEmpty(secret))
                    methods.Add(new PasswordAuthenticationMethod(profile.Username, secret));

                var keyboard = new KeyboardInteractiveAuthenticationMethod(profile.Username);
                keyboard.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                        prompt.Response = secret;
                };
                methods.Add(keyboard);
                break;
        }

        if (methods.Count == 0)
            throw new InvalidOperationException(L10n.T("没有可用的 SFTP 认证方式。"));

        return methods.ToArray();
    }

    private static SshAgentPrivateKey[] RequestAgentIdentities()
    {
        try
        {
            return new SshAgent().RequestIdentities();
        }
        catch (Exception ex)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    return new Pageant().RequestIdentities();
                }
                catch
                {
                    // Fall through to the clearer error below.
                }
            }

            throw new IOException(
                L10n.T("无法连接 SSH Agent，请确认 SSH_AUTH_SOCK 或 ssh-agent 正在运行。"), ex);
        }
    }

    private static string? GetHostKeyType(byte[] key)
    {
        if (key.Length < 4)
            return null;

        var length = BinaryPrimitives.ReadUInt32BigEndian(key);
        if (length == 0 || length > key.Length - 4)
            return null;

        return Encoding.ASCII.GetString(key, 4, (int)length);
    }

    private static string ExpandPath(string path)
    {
        path = (path ?? string.Empty).Trim();
        if (path == "~")
            return AppPaths.Home;
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(AppPaths.Home, path[2..]);
        return path;
    }

    public override string GetDisplayPath(string path)
        => $"{_profile.DisplayTarget}{NormalizePath(path)}";

    public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            path = NormalizePath(path);
            var result = new List<FileEntry>();
            foreach (var file in _client.ListDirectory(path).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Name is "." or "..")
                    continue;
                result.Add(BuildEntry(file));
            }
            return (IReadOnlyList<FileEntry>)result;
        }, cancellationToken);

    public override Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            path = NormalizePath(path);
            if (IsRoot(path))
            {
                return new FileEntry
                {
                    Name = DisplayName,
                    Path = path,
                    Kind = EntryKind.Directory,
                };
            }

            try
            {
                var file = _client.Get(path);
                return BuildEntry(file);
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        }, cancellationToken);

    public override Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(() => IsRoot(NormalizePath(path)) || _client.Exists(NormalizePath(path)), cancellationToken);

    public override Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(() => (Stream)_client.OpenRead(NormalizePath(path)), cancellationToken);

    public override Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null,
        CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            path = NormalizePath(path);
            if (!overwrite && _client.Exists(path))
                throw new IOException(L10n.T("目标文件已存在"));
            var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
            return (Stream)_client.Open(path, mode, FileAccess.Write);
        }, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            _client.CreateDirectory(NormalizePath(path));
        }, cancellationToken);

    public override Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            path = NormalizePath(path);
            DeleteCore(path, recursive, cancellationToken);
        }, cancellationToken);

    public override Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            _client.RenameFile(NormalizePath(sourcePath), NormalizePath(destinationPath));
        }, cancellationToken);

    public override Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            _client.SetLastWriteTimeUtc(NormalizePath(path), modified.UtcDateTime);
        }, cancellationToken);

    public override Task SetUnixModeAsync(string path, UnixFileMode mode, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            var bits = (int)mode & 0xFFF;
            var octal = ((bits >> 9) & 7) * 1000 + ((bits >> 6) & 7) * 100 + ((bits >> 3) & 7) * 10 + (bits & 7);
            _client.ChangePermissions(NormalizePath(path), (short)octal);
        }, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
                _client.Disconnect();
        }
        catch { /* ignore */ }
        _client.Dispose();
        _gate.Dispose();
    }

    private void DeleteCore(string path, bool recursive, CancellationToken cancellationToken)
    {
        ISftpFile file;
        try { file = _client.Get(path); }
        catch (SftpPathNotFoundException) { return; }

        if (file.IsDirectory && !file.IsSymbolicLink)
        {
            if (!recursive)
                throw new IOException(L10n.T("目录不为空"));
            foreach (var child in _client.ListDirectory(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Name is "." or "..")
                    continue;
                DeleteCore(Combine(path, child.Name), true, cancellationToken);
            }
            _client.DeleteDirectory(path);
        }
        else
        {
            _client.DeleteFile(path);
        }
    }

    private FileEntry BuildEntry(ISftpFile file)
    {
        var isDirectory = file.IsDirectory;
        var kind = isDirectory ? EntryKind.Directory : EntryKind.File;

        if (file.IsSymbolicLink)
        {
            kind = _resolvedLinks.GetOrAdd(file.FullName, _ => ResolveSymlinkKind(file.FullName));
        }

        var attributes = file.Attributes;
        var mode = ToUnixMode(attributes);
        DateTimeOffset? modified = file.LastWriteTimeUtc == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc));

        var owner = attributes.UserId != 0 ? attributes.UserId.ToString() : null;
        var group = attributes.GroupId != 0 ? attributes.GroupId.ToString() : null;

        return new FileEntry
        {
            Name = file.Name,
            Path = file.FullName,
            Kind = kind,
            Size = kind == EntryKind.Directory ? 0 : file.Length,
            Modified = modified,
            IsHidden = file.Name.StartsWith('.'),
            IsSymlink = file.IsSymbolicLink,
            UnixMode = mode,
            Owner = owner,
            Group = group,
            MimeType = kind == EntryKind.File ? MimeTypes.FromExtension(GetExtension(file.Name)) : null,
        };
    }

    private EntryKind ResolveSymlinkKind(string path)
    {
        var previous = _client.WorkingDirectory;
        try
        {
            _client.ChangeDirectory(path);
            return EntryKind.Directory;
        }
        catch
        {
            return EntryKind.File;
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(previous))
                    _client.ChangeDirectory(previous);
            }
            catch { /* ignore */ }
        }
    }

    private static UnixFileMode? ToUnixMode(SftpFileAttributes attributes)
    {
        var mode = UnixFileMode.None;
        if (attributes.OwnerCanRead) mode |= UnixFileMode.UserRead;
        if (attributes.OwnerCanWrite) mode |= UnixFileMode.UserWrite;
        if (attributes.OwnerCanExecute) mode |= UnixFileMode.UserExecute;
        if (attributes.GroupCanRead) mode |= UnixFileMode.GroupRead;
        if (attributes.GroupCanWrite) mode |= UnixFileMode.GroupWrite;
        if (attributes.GroupCanExecute) mode |= UnixFileMode.GroupExecute;
        if (attributes.OthersCanRead) mode |= UnixFileMode.OtherRead;
        if (attributes.OthersCanWrite) mode |= UnixFileMode.OtherWrite;
        if (attributes.OthersCanExecute) mode |= UnixFileMode.OtherExecute;
        return mode;
    }

    private async Task RunAsync(Action action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..];
    }
}
