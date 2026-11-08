using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed record PendingTransferSource(string FileSystemId, string Path);

/// <summary>A copy or move request that was queued but has not finished yet.</summary>
public sealed record PendingTransfer(
    string Id,
    List<PendingTransferSource> Sources,
    string DestinationFileSystemId,
    string DestinationPath,
    bool Move,
    DateTimeOffset CreatedAt);

/// <summary>
/// Persists queued transfers so a crash or restart does not silently lose the
/// remaining work. The queue itself is recreated on the next launch.
/// </summary>
public sealed class TransferJournal
{
    private readonly object _sync = new();
    private List<PendingTransfer> _items;

    public TransferJournal(string? path = null)
    {
        FilePath = path ?? Path.Combine(AppPaths.DataDirectory, "pending-transfers.json");
        _items = JsonFile.Load<List<PendingTransfer>>(FilePath) ?? [];
        _items.RemoveAll(item => item is null || item.Sources is not { Count: > 0 });
    }

    public string FilePath { get; }

    public IReadOnlyList<PendingTransfer> Items
    {
        get
        {
            lock (_sync)
                return [.. _items];
        }
    }

    public void Add(PendingTransfer transfer)
    {
        lock (_sync)
        {
            _items.RemoveAll(item => item.Id == transfer.Id);
            _items.Add(transfer);
            SaveLocked();
        }
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            if (_items.RemoveAll(item => item.Id == id) == 0)
                return;
            SaveLocked();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            JsonFile.Save(FilePath, _items);
        }
        catch (Exception ex)
        {
            Log.Warn("保存传输队列失败", ex);
        }
    }
}
