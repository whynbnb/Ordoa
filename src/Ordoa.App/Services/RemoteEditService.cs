using Avalonia.Threading;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

/// <summary>
/// Watches files downloaded for remote editing and uploads them back after the
/// external application saves. Changes are debounced to avoid partial writes.
/// </summary>
public sealed class RemoteEditService : IDisposable
{
    private const int MaxTargets = 20;
    private readonly object _sync = new();
    private readonly Dictionary<string, EditTarget> _targets = new(StringComparer.Ordinal);
    private bool _disposed;

    public void Track(IFileSystem fileSystem, string remotePath, string localPath,
        Action<string, bool> notify)
    {
        if (_disposed || fileSystem.IsLocal)
            return;

        Untrack(localPath);
        var directory = Path.GetDirectoryName(localPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        var target = new EditTarget(fileSystem, remotePath, localPath, notify);
        try
        {
            target.Watcher = new FileSystemWatcher(directory, Path.GetFileName(localPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size |
                               NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            target.Watcher.Changed += (_, _) => Schedule(target);
            target.Watcher.Created += (_, _) => Schedule(target);
            target.Watcher.Renamed += (_, _) => Schedule(target);
        }
        catch (Exception ex)
        {
            Log.Warn($"无法监听远程编辑文件: {localPath}", ex);
            target.Watcher = null;
            return;
        }

        lock (_sync)
        {
            if (_targets.Count >= MaxTargets && _targets.Count > 0)
            {
                var oldest = _targets.Values.First();
                _targets.Remove(oldest.LocalPath);
                oldest.Dispose();
            }
            _targets[localPath] = target;
        }
    }

    private void Schedule(EditTarget target)
    {
        if (_disposed)
            return;
        lock (_sync)
        {
            target.Debounce?.Cancel();
            target.Debounce?.Dispose();
            target.Debounce = new CancellationTokenSource();
            _ = UploadAfterDelayAsync(target, target.Debounce.Token);
        }
    }

    private async Task UploadAfterDelayAsync(EditTarget target, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(target.LocalPath))
                return;

            var first = new FileInfo(target.LocalPath);
            var size = first.Length;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(target.LocalPath) || new FileInfo(target.LocalPath).Length != size)
                return;

            lock (_sync)
            {
                if (target.Uploading)
                {
                    target.Pending = true;
                    return;
                }
                target.Uploading = true;
            }

            try
            {
                await using var input = new FileStream(target.LocalPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous);
                await using var output = await target.FileSystem.OpenWriteAsync(target.RemotePath, true,
                    size, cancellationToken).ConfigureAwait(false);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                Notify(target, "已回传远程修改", false);
            }
            catch (OperationCanceledException)
            {
                // Superseded or shutting down.
            }
            catch (Exception ex)
            {
                Log.Warn($"回传远程文件失败: {target.RemotePath}", ex);
                Notify(target, "回传失败：" + ex.Message, true);
            }
            finally
            {
                var reschedule = false;
                lock (_sync)
                {
                    target.Uploading = false;
                    if (target.Pending)
                    {
                        target.Pending = false;
                        reschedule = true;
                    }
                }
                if (reschedule)
                    Schedule(target);
            }
        }
        catch (OperationCanceledException) { /* superseded */ }
    }

    private static void Notify(EditTarget target, string message, bool error)
        => Dispatcher.UIThread.Post(() => target.Notify(message, error));

    private void Untrack(string localPath)
    {
        lock (_sync)
        {
            if (!_targets.Remove(localPath, out var existing))
                return;
            existing.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_sync)
        {
            foreach (var target in _targets.Values)
                target.Dispose();
            _targets.Clear();
        }
    }

    private sealed class EditTarget(IFileSystem fileSystem, string remotePath, string localPath,
        Action<string, bool> notify) : IDisposable
    {
        public IFileSystem FileSystem { get; } = fileSystem;
        public string RemotePath { get; } = remotePath;
        public string LocalPath { get; } = localPath;
        public Action<string, bool> Notify { get; } = notify;
        public FileSystemWatcher? Watcher { get; set; }
        public CancellationTokenSource? Debounce { get; set; }
        public bool Uploading { get; set; }
        public bool Pending { get; set; }

        public void Dispose()
        {
            Watcher?.Dispose();
            Debounce?.Cancel();
            Debounce?.Dispose();
        }
    }
}
