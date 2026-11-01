using System.Collections.Concurrent;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Util;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
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
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(password))
                methods.Add(new PasswordAuthenticationMethod(profile.Username, password));

            var keyboard = new KeyboardInteractiveAuthenticationMethod(profile.Username);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = password;
            };
            methods.Add(keyboard);

            var connectionInfo = new ConnectionInfo(profile.Host, profile.EffectivePort, profile.Username, methods.ToArray())
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
            client.HostKeyReceived += (_, e) =>
            {
                fingerprint = e.FingerPrintSHA256;
                if (string.IsNullOrWhiteSpace(profile.HostKeyFingerprint))
                {
                    e.CanTrust = true;
                }
                else
                {
                    e.CanTrust = string.Equals(profile.HostKeyFingerprint, e.FingerPrintSHA256, StringComparison.Ordinal);
                    if (!e.CanTrust)
                        hostKeyError = L10n.T("服务器主机密钥与之前保存的指纹不一致，连接已中止。");
                }
            };

            try
            {
                client.Connect();
                result = new SftpFileSystem(profile, client)
                {
                    HostKeyFingerprint = string.IsNullOrWhiteSpace(profile.HostKeyFingerprint) ? fingerprint : profile.HostKeyFingerprint,
                    HostKeyAccepted = string.IsNullOrWhiteSpace(profile.HostKeyFingerprint),
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
