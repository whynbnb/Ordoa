using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Util;
using SMBLibrary;
using SMBLibrary.Client;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace Ordoa.Core.FileSystems.Remote;

/// <summary>SMB2/3 provider implemented with the managed SMBLibrary.</summary>
public sealed class SmbFileSystem : FileSystemBase
{
    private readonly SMB2Client _client;
    private readonly ConnectionProfile _profile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, INTFileStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ISMBFileStore> _interfaces = new(StringComparer.OrdinalIgnoreCase);

    private SmbFileSystem(ConnectionProfile profile, SMB2Client client)
        : base(profile.Id, profile.Scheme, profile.Name)
    {
        _profile = profile;
        _client = client;
    }

    public override bool IsCaseSensitive => false;

    public override FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.List | FileSystemCapabilities.Read | FileSystemCapabilities.Write |
        FileSystemCapabilities.CreateDirectory | FileSystemCapabilities.Delete | FileSystemCapabilities.Rename |
        FileSystemCapabilities.Move | FileSystemCapabilities.Copy | FileSystemCapabilities.SetModifiedTime;

    public static Task<SmbFileSystem> ConnectAsync(ConnectionProfile profile, string password,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(profile.Host))
                throw new ArgumentException("SMB 服务器地址不能为空");

            var client = new SMB2Client(60000, true);
            try
            {
                if (!client.Connect(profile.Host, SMBTransportType.DirectTCPTransport))
                    throw new IOException($"无法连接 SMB 服务器 {profile.Host}:{profile.EffectivePort}");

                var domain = profile.Domain ?? string.Empty;
                var status = client.Login(domain, profile.Username, password);
                if (status != NTStatus.STATUS_SUCCESS)
                    throw new UnauthorizedAccessException($"SMB 登录失败: {status}");

                return new SmbFileSystem(profile, client);
            }
            catch
            {
                try { client.Logoff(); } catch { /* ignore */ }
                try { client.Disconnect(); } catch { /* ignore */ }
                throw;
            }
        }, cancellationToken);
    }

    public override string GetDisplayPath(string path)
        => $"smb://{_profile.Host}{NormalizePath(path)}";

    public override async Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRoot(path))
                return await Task.Run(() => ListShares(path, cancellationToken), cancellationToken).ConfigureAwait(false);

            var (share, relative) = SplitPath(path);
            var store = GetStore(share);
            var handle = OpenDirectory(store, relative);
            try
            {
                var status = store.QueryDirectory(out var entries, handle, "*",
                    FileInformationClass.FileDirectoryInformation);
                if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES))
                    throw new IOException($"读取目录失败: {status}");

                var result = new List<FileEntry>();
                foreach (var info in entries.OfType<FileDirectoryInformation>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (info.FileName is "." or "..")
                        continue;
                    result.Add(BuildEntry(info, path));
                }
                return result;
            }
            finally
            {
                store.CloseFile(handle);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (IsRoot(path))
        {
            return new FileEntry { Name = DisplayName, Path = path, Kind = EntryKind.Directory };
        }

        var parent = GetParent(path);
        var name = GetName(path);
        var entries = await ListAsync(parent, cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public override async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (IsRoot(path))
            return true;
        try
        {
            return await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch
        {
            return false;
        }
    }

    public override async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (share, relative) = SplitPath(path);
            var store = GetStore(share);
            var status = store.CreateFile(out var handle, out _, relative,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SmbFileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);

            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"打开文件失败: {status} ({path})");

            return new SmbReadStream(store, handle, (int)Math.Min(_interfaces[share].MaxReadSize, 1024 * 1024));
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null,
        CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (share, relative) = SplitPath(path);
            var store = GetStore(share);
            var status = store.CreateFile(out var handle, out _, relative,
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SmbFileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                overwrite ? CreateDisposition.FILE_OVERWRITE_IF : CreateDisposition.FILE_CREATE,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);

            if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
                throw new IOException("目标文件已存在");
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"创建文件失败: {status} ({path})");

            return new SmbWriteStream(store, handle, (int)Math.Min(_interfaces[share].MaxWriteSize, 1024 * 1024));
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (share, relative) = SplitPath(path);
            var store = GetStore(share);
            var status = store.CreateFile(out var handle, out _, relative,
                AccessMask.GENERIC_WRITE, SmbFileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_CREATE,
                CreateOptions.FILE_DIRECTORY_FILE, null);

            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"创建目录失败: {status} ({path})");
            store.CloseFile(handle);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (share, relative) = SplitPath(path);
            var store = GetStore(share);
            DeleteCore(store, relative, recursive, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        sourcePath = NormalizePath(sourcePath);
        destinationPath = NormalizePath(destinationPath);
        var sourceShare = SplitPath(sourcePath).Share;
        var destinationShare = SplitPath(destinationPath).Share;

        if (!string.Equals(sourceShare, destinationShare, StringComparison.OrdinalIgnoreCase))
        {
            // Let the operation engine perform a recursive copy-and-delete across shares.
            throw new IOException("跨共享移动需要复制后删除");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = GetStore(sourceShare);
            var sourceRelative = SplitPath(sourcePath).Relative;
            var destinationRelative = SplitPath(destinationPath).Relative;
            var status = store.CreateFile(out var handle, out _, sourceRelative,
                AccessMask.DELETE, SmbFileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);

            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"移动失败: {status}");

            try
            {
                var info = new FileRenameInformationType2
                {
                    ReplaceIfExists = true,
                    RootDirectory = 0,
                    FileName = destinationRelative.Replace('/', '\\'),
                };
                status = store.SetFileInformation(handle, info);
                if (status != NTStatus.STATUS_SUCCESS)
                    throw new IOException($"重命名失败: {status}");
            }
            finally
            {
                store.CloseFile(handle);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (share, relative) = SplitPath(path);
            var store = GetStore(share);
            var status = store.CreateFile(out var handle, out _, relative,
                AccessMask.GENERIC_WRITE, SmbFileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (status != NTStatus.STATUS_SUCCESS)
                return;

            try
            {
                var basic = new FileBasicInformation
                {
                    CreationTime = new SetFileTime(true),
                    LastAccessTime = new SetFileTime(true),
                    LastWriteTime = new SetFileTime(modified.UtcDateTime),
                    ChangeTime = new SetFileTime(true),
                };
                store.SetFileInformation(handle, basic);
            }
            finally
            {
                store.CloseFile(handle);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task<SpaceInfo> GetSpaceAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (IsRoot(path))
            return SpaceInfo.Unknown;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (share, _) = SplitPath(path);
            var store = GetStore(share);
            var status = store.GetFileSystemInformation(out var information,
                FileSystemInformationClass.FileFsFullSizeInformation);
            if (status != NTStatus.STATUS_SUCCESS || information is not FileFsFullSizeInformation full)
                return SpaceInfo.Unknown;

            var unit = (long)full.SectorsPerAllocationUnit * full.BytesPerSector;
            return new SpaceInfo(full.ActualAvailableAllocationUnits * unit, full.TotalAllocationUnits * unit);
        }
        catch
        {
            return SpaceInfo.Unknown;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var store in _interfaces.Values)
        {
            try { store.Disconnect(); } catch { /* ignore */ }
        }
        _stores.Clear();
        _interfaces.Clear();
        try { _client.Logoff(); } catch { /* ignore */ }
        try { _client.Disconnect(); } catch { /* ignore */ }
        _gate.Dispose();
        await Task.CompletedTask;
    }

    private IReadOnlyList<FileEntry> ListShares(string parent, CancellationToken cancellationToken)
    {
        var shares = _client.ListShares(out var status);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"无法列出共享: {status}");

        var result = new List<FileEntry>();
        foreach (var share in shares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(share) || share == "IPC$")
                continue;
            if (share.StartsWith('.'))
                continue;

            result.Add(new FileEntry
            {
                Name = share,
                Path = Combine(parent, share),
                Kind = EntryKind.Directory,
                IsHidden = false,
            });
        }
        return result;
    }

    private void DeleteCore(INTFileStore store, string relative, bool recursive, CancellationToken cancellationToken)
    {
        var isDirectory = IsDirectory(store, relative);
        if (isDirectory)
        {
            if (recursive)
            {
                var dirHandle = OpenDirectory(store, relative);
                List<QueryDirectoryFileInformation> entries;
                try
                {
                    var status = store.QueryDirectory(out entries, dirHandle, "*", FileInformationClass.FileDirectoryInformation);
                    if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES))
                        throw new IOException($"读取目录失败: {status}");
                }
                finally
                {
                    store.CloseFile(dirHandle);
                }

                foreach (var info in entries.OfType<FileDirectoryInformation>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (info.FileName is "." or "..")
                        continue;
                    var child = string.IsNullOrEmpty(relative) ? info.FileName : relative + "\\" + info.FileName;
                    DeleteCore(store, child, true, cancellationToken);
                }
            }
        }

        var openStatus = store.CreateFile(out var handle, out _, relative,
            AccessMask.DELETE, isDirectory ? SmbFileAttributes.Directory : SmbFileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
            isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE, null);

        if (openStatus != NTStatus.STATUS_SUCCESS)
            throw new IOException($"删除失败: {openStatus}");

        try
        {
            var status = store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"删除失败: {status}");
        }
        finally
        {
            store.CloseFile(handle);
        }
    }

    private bool IsDirectory(INTFileStore store, string relative)
    {
        var status = store.CreateFile(out var handle, out var fileStatus, relative,
            AccessMask.GENERIC_READ, SmbFileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
        if (status == NTStatus.STATUS_SUCCESS)
        {
            store.CloseFile(handle);
            return true;
        }

        status = store.CreateFile(out handle, out fileStatus, relative,
            AccessMask.GENERIC_READ, SmbFileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        if (status == NTStatus.STATUS_SUCCESS)
            store.CloseFile(handle);
        return false;
    }

    private object OpenDirectory(INTFileStore store, string relative)
    {
        var status = store.CreateFile(out var handle, out _, relative,
            AccessMask.GENERIC_READ, SmbFileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"打开目录失败: {status}");
        return handle;
    }

    private INTFileStore GetStore(string share)
    {
        if (_stores.TryGetValue(share, out var existing))
            return existing;

        var store = _client.TreeConnect(share, out var status);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"无法访问共享 {share}: {status}");

        var typed = (INTFileStore)store;
        _stores[share] = typed;
        _interfaces[share] = store;
        return typed;
    }

    private static FileEntry BuildEntry(FileDirectoryInformation info, string parent)
    {
        var isDirectory = (info.FileAttributes & SmbFileAttributes.Directory) != 0;
        var name = info.FileName;
        DateTimeOffset? modified = info.LastWriteTime == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTime, DateTimeKind.Utc));
        DateTimeOffset? created = info.CreationTime == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(info.CreationTime, DateTimeKind.Utc));

        return new FileEntry
        {
            Name = name,
            Path = PosixPath.Combine(parent, name),
            Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
            Size = isDirectory ? 0 : info.EndOfFile,
            Modified = modified,
            Created = created,
            IsHidden = name.StartsWith('.') || (info.FileAttributes & SmbFileAttributes.Hidden) != 0,
            IsReadOnly = (info.FileAttributes & SmbFileAttributes.ReadOnly) != 0,
            IsSymlink = (info.FileAttributes & SmbFileAttributes.ReparsePoint) != 0,
            MimeType = isDirectory ? null : MimeTypes.FromExtension(GetExtension(name)),
        };
    }

    private static (string Share, string Relative) SplitPath(string path)
    {
        var segments = PosixPath.Segments(path);
        if (segments.Count == 0)
            throw new ArgumentException("SMB 路径必须包含共享名称", nameof(path));

        var share = segments[0];
        var relative = string.Join('\\', segments.Skip(1));
        return (share, relative);
    }

    private static string GetExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..];
    }

    private sealed class SmbReadStream : Stream
    {
        private readonly INTFileStore _store;
        private readonly object _handle;
        private readonly int _chunkSize;
        private long _position;
        private bool _disposed;

        public SmbReadStream(INTFileStore store, object handle, int chunkSize)
        {
            _store = store;
            _handle = handle;
            _chunkSize = Math.Clamp(chunkSize, 16 * 1024, 1024 * 1024);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
                return 0;

            var status = _store.ReadFile(out var data, _handle, _position, Math.Min(count, _chunkSize));
            if (status == NTStatus.STATUS_END_OF_FILE)
                return 0;
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"读取远程文件失败: {status}");
            if (data is null || data.Length == 0)
                return 0;

            var length = Math.Min(data.Length, count);
            Array.Copy(data, 0, buffer, offset, length);
            _position += length;
            return length;
        }

        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var read = Read(temp, 0, temp.Length);
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _store.CloseFile(_handle);
            }
            base.Dispose(disposing);
        }
    }

    private sealed class SmbWriteStream : Stream
    {
        private readonly INTFileStore _store;
        private readonly object _handle;
        private readonly int _chunkSize;
        private long _position;
        private bool _disposed;

        public SmbWriteStream(INTFileStore store, object handle, int chunkSize)
        {
            _store = store;
            _handle = handle;
            _chunkSize = Math.Clamp(chunkSize, 16 * 1024, 1024 * 1024);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                var chunk = Math.Min(count - written, _chunkSize);
                var data = new byte[chunk];
                Array.Copy(buffer, offset + written, data, 0, chunk);
                var status = _store.WriteFile(out var bytesWritten, _handle, _position, data);
                if (status != NTStatus.STATUS_SUCCESS)
                    throw new IOException($"写入远程文件失败: {status}");
                if (bytesWritten <= 0)
                    throw new IOException("远程写入未返回进度");
                written += bytesWritten;
                _position += bytesWritten;
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() => _store.FlushFileBuffers(_handle);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                try { _store.FlushFileBuffers(_handle); } catch { /* ignore */ }
                _store.CloseFile(_handle);
            }
            base.Dispose(disposing);
        }
    }
}
