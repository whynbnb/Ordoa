using System.Collections.Concurrent;
using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.FileSystems.Remote;
using Ordoa.Core.Models;

namespace Ordoa.Core.Services;

/// <summary>Caches remote sessions and coordinates connection profiles with credentials.</summary>
public sealed class FileSystemManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IFileSystem>>> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _manageGate = new(1, 1);

    public FileSystemManager(ConnectionStore? connections = null, ICredentialStore? credentials = null)
    {
        Connections = connections ?? new ConnectionStore();
        Credentials = credentials ?? new CredentialStore();
    }

    public ConnectionStore Connections { get; }
    public ICredentialStore Credentials { get; }
    public IFileSystem Local => LocalFileSystem.Instance;

    public FileSystemCapabilities LocalCapabilities => LocalFileSystem.Instance.Capabilities;

    public async Task<IFileSystem> ConnectAsync(ConnectionProfile profile, string? passwordOverride = null,
        bool savePassword = true, CancellationToken cancellationToken = default)
    {
        if (profile.Protocol == RemoteProtocol.Ftp || profile.Protocol == RemoteProtocol.Ftps ||
            profile.Protocol == RemoteProtocol.Sftp || profile.Protocol == RemoteProtocol.Smb ||
            profile.Protocol == RemoteProtocol.WebDav)
        {
            if (_sessions.TryGetValue(profile.Id, out var existing))
            {
                try
                {
                    return await existing.Value.ConfigureAwait(false);
                }
                catch
                {
                    _sessions.TryRemove(profile.Id, out _);
                }
            }

            var lazy = _sessions.GetOrAdd(profile.Id,
                _ => new Lazy<Task<IFileSystem>>(() => ConnectCoreAsync(profile, passwordOverride, savePassword, cancellationToken)));

            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            catch
            {
                _sessions.TryRemove(profile.Id, out _);
                throw;
            }
        }

        return Local;
    }

    public async Task<IFileSystem> ConnectByIdAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var profile = Connections.Get(connectionId)
            ?? throw new InvalidOperationException("找不到保存的远程连接");

        if (_sessions.TryGetValue(connectionId, out var existing))
        {
            try
            {
                return await existing.Value.ConfigureAwait(false);
            }
            catch
            {
                _sessions.TryRemove(connectionId, out _);
            }
        }

        return await ConnectAsync(profile, null, profile.SavePassword, cancellationToken).ConfigureAwait(false);
    }

    public IFileSystem? GetCached(string connectionId)
    {
        if (_sessions.TryGetValue(connectionId, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            return lazy.Value.Result;
        return null;
    }

    public bool IsConnected(string connectionId)
        => _sessions.TryGetValue(connectionId, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully;

    public async Task DisconnectAsync(string connectionId)
    {
        if (_sessions.TryRemove(connectionId, out var lazy) && lazy.IsValueCreated)
        {
            try
            {
                var fs = await lazy.Value.ConfigureAwait(false);
                await fs.DisposeAsync().ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
    }

    public async Task TestAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken = default)
    {
        var fs = await OpenAsync(profile, password, cancellationToken).ConfigureAwait(false);
        try
        {
            var path = profile.NormalizeInitialPath(profile.InitialPath);
            if (!fs.IsRoot(path) || profile.Protocol == RemoteProtocol.Smb || profile.Protocol == RemoteProtocol.WebDav)
            {
                try
                {
                    await fs.ListAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && path != fs.RootPath)
                {
                    // Fall back to the root so a wrong start folder does not hide a working connection.
                    await fs.ListAsync(fs.RootPath, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await fs.ListAsync(fs.RootPath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await fs.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task SaveProfileAsync(ConnectionProfile profile, string? password, bool deletePassword = false,
        CancellationToken cancellationToken = default)
    {
        await _manageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (deletePassword || !profile.SavePassword || profile.Anonymous)
                await Credentials.DeletePasswordAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            else if (password is not null)
                await Credentials.SetPasswordAsync(profile.Id, password, cancellationToken).ConfigureAwait(false);

            Connections.Save(profile);
        }
        finally
        {
            _manageGate.Release();
        }
    }

    public async Task RemoveProfileAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(connectionId).ConfigureAwait(false);
        await Credentials.DeletePasswordAsync(connectionId, cancellationToken).ConfigureAwait(false);
        Connections.Remove(connectionId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(key, out var lazy) && lazy.IsValueCreated)
            {
                try
                {
                    var fs = await lazy.Value.ConfigureAwait(false);
                    await fs.DisposeAsync().ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }
        }
        _manageGate.Dispose();
    }

    private async Task<IFileSystem> ConnectCoreAsync(ConnectionProfile profile, string? passwordOverride,
        bool savePassword, CancellationToken cancellationToken)
    {
        var password = passwordOverride;
        if (password is null && !profile.Anonymous)
        {
            password = await Credentials.GetPasswordAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        }
        password ??= string.Empty;

        var fs = await OpenAsync(profile, password, cancellationToken).ConfigureAwait(false);
        var effectivePassword = password;

        try
        {
            if (fs is SftpFileSystem sftp && sftp.HostKeyAccepted &&
                !string.Equals(profile.HostKeyFingerprint, sftp.HostKeyFingerprint, StringComparison.Ordinal))
            {
                profile.HostKeyFingerprint = sftp.HostKeyFingerprint;
                if (Connections.Get(profile.Id) is not null)
                    Connections.Save(profile);
            }

            try
            {
                if (profile.SavePassword && savePassword && !profile.Anonymous && effectivePassword.Length > 0)
                    await Credentials.SetPasswordAsync(profile.Id, effectivePassword, cancellationToken).ConfigureAwait(false);
                else if (!profile.SavePassword)
                    await Credentials.DeletePasswordAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("保存远程连接密码失败，连接仍然可用", ex);
            }

            Log.Info($"已连接 {profile.ProtocolLabel} {profile.DisplayTarget} ({profile.Name})");
            return fs;
        }
        catch
        {
            await fs.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IFileSystem> OpenAsync(ConnectionProfile profile, string password,
        CancellationToken cancellationToken)
    {
        var safeProfile = profile.Clone();
        if (string.IsNullOrWhiteSpace(safeProfile.Host))
            throw new ArgumentException("服务器地址不能为空");

        IFileSystem fs = safeProfile.Protocol switch
        {
            RemoteProtocol.Ftp or RemoteProtocol.Ftps => await FtpFileSystem.ConnectAsync(safeProfile, password, cancellationToken)
                .ConfigureAwait(false),
            RemoteProtocol.Sftp => await SftpFileSystem.ConnectAsync(safeProfile, password, cancellationToken)
                .ConfigureAwait(false),
            RemoteProtocol.Smb => await SmbFileSystem.ConnectAsync(safeProfile, password, cancellationToken)
                .ConfigureAwait(false),
            RemoteProtocol.WebDav => await WebDavFileSystem.ConnectAsync(safeProfile, password, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new NotSupportedException($"不支持的协议: {safeProfile.Protocol}"),
        };

        return fs;
    }
}
