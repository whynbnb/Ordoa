using FluentFTP;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.Core.FileSystems.Remote;

/// <summary>FTP and explicit/implicit FTPS provider backed by FluentFTP.</summary>
public sealed class FtpFileSystem : FileSystemBase
{
    private readonly AsyncFtpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConnectionProfile _profile;

    private FtpFileSystem(ConnectionProfile profile, AsyncFtpClient client)
        : base(profile.Id, profile.Scheme, profile.Name)
    {
        _profile = profile;
        _client = client;
    }

    public override FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.List | FileSystemCapabilities.Read | FileSystemCapabilities.Write |
        FileSystemCapabilities.CreateDirectory | FileSystemCapabilities.Delete | FileSystemCapabilities.Rename |
        FileSystemCapabilities.Move | FileSystemCapabilities.Copy | FileSystemCapabilities.SetModifiedTime;

    public static async Task<FtpFileSystem> ConnectAsync(ConnectionProfile profile, string password,
        CancellationToken cancellationToken = default)
    {
        var config = new FtpConfig
        {
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            ConnectTimeout = 20000,
            ReadTimeout = 120000,
            WriteTimeout = 120000,
            DataConnectionConnectTimeout = 20000,
            DataConnectionReadTimeout = 120000,
            DataConnectionWriteTimeout = 120000,
            EncryptionMode = profile.Protocol switch
            {
                RemoteProtocol.Ftps => profile.EffectivePort == 990 ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit,
                _ => FtpEncryptionMode.None,
            },
            ValidateAnyCertificate = profile.AllowInvalidCertificate,
            RetryAttempts = 2,
            ListingDataType = FtpDataType.ASCII,
            TransferChunkSize = 256 * 1024,
            LocalFileBufferSize = 256 * 1024,
        };

        var user = profile.Anonymous ? "anonymous" : profile.Username;
        var pass = profile.Anonymous && string.IsNullOrEmpty(password) ? "anonymous@ordoa.local" : password;
        var client = new AsyncFtpClient(profile.Host, user, pass, profile.EffectivePort, config);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return new FtpFileSystem(profile, client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override string GetDisplayPath(string path)
        => $"{_profile.DisplayTarget}{NormalizePath(path)}";

    public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            path = NormalizePath(path);
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var items = await _client.GetListing(path, FtpListOption.SizeModify | FtpListOption.AllFiles,
                cancellationToken).ConfigureAwait(false);

            var result = new List<FileEntry>(items.Length);
            foreach (var item in items)
            {
                if (item.Name is "." or "..")
                    continue;
                result.Add(BuildEntry(item, path));
            }
            return (IReadOnlyList<FileEntry>)result;
        }, cancellationToken);

    public override Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            path = NormalizePath(path);
            if (IsRoot(path))
            {
                return new FileEntry
                {
                    Name = DisplayName,
                    Path = path,
                    Kind = EntryKind.Directory,
                    Modified = null,
                };
            }

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var item = await _client.GetObjectInfo(path, true, cancellationToken).ConfigureAwait(false);
                return item is null ? null : BuildEntry(item, GetParent(path));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }, cancellationToken);

    public override Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            path = NormalizePath(path);
            if (IsRoot(path))
                return true;
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await _client.FileExists(path, cancellationToken).ConfigureAwait(false) ||
                   await _client.DirectoryExists(path, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public override Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return (Stream)await _client.OpenRead(NormalizePath(path), FtpDataType.Binary, 0, true, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);

    public override Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null,
        CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            path = NormalizePath(path);
            var exists = await _client.FileExists(path, cancellationToken).ConfigureAwait(false);
            if (!overwrite && exists)
                throw new IOException(L10n.T("目标文件已存在"));
            if (overwrite && exists)
                await _client.DeleteFile(path, cancellationToken).ConfigureAwait(false);

            return (Stream)await _client.OpenWrite(path, FtpDataType.Binary, true, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await _client.CreateDirectory(NormalizePath(path), true, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public override Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            path = NormalizePath(path);
            if (await _client.DirectoryExists(path, cancellationToken).ConfigureAwait(false))
                await _client.DeleteDirectory(path, cancellationToken).ConfigureAwait(false);
            else
                await _client.DeleteFile(path, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public override Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            sourcePath = NormalizePath(sourcePath);
            destinationPath = NormalizePath(destinationPath);
            if (await _client.DirectoryExists(sourcePath, cancellationToken).ConfigureAwait(false))
                await _client.MoveDirectory(sourcePath, destinationPath, FtpRemoteExists.Overwrite, cancellationToken).ConfigureAwait(false);
            else
                await _client.MoveFile(sourcePath, destinationPath, FtpRemoteExists.Overwrite, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public override Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await _client.SetModifiedTime(NormalizePath(path), modified.LocalDateTime, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
                await _client.Disconnect(CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* ignore */ }
        await _client.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private FileEntry BuildEntry(FtpListItem item, string parent)
    {
        var name = item.Name;
        var kind = item.Type switch
        {
            FtpObjectType.Directory => EntryKind.Directory,
            FtpObjectType.Link => item.LinkObject?.Type == FtpObjectType.Directory ? EntryKind.Directory : EntryKind.File,
            _ => EntryKind.File,
        };

        UnixFileMode? mode = item.Chmod > 0 ? (UnixFileMode)(item.Chmod & 0x1FF) : null;
        var modified = new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Local));

        return new FileEntry
        {
            Name = name,
            Path = Combine(parent, name),
            Kind = kind,
            Size = kind == EntryKind.Directory ? 0 : item.Size,
            Modified = modified,
            IsHidden = name.StartsWith('.'),
            IsSymlink = item.Type == FtpObjectType.Link,
            LinkTarget = item.LinkTarget,
            UnixMode = mode,
            MimeType = kind == EntryKind.File ? MimeTypes.FromExtension(GetExtension(name)) : null,
        };
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
            await _client.Connect(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
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
