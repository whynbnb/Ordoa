using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

public sealed record ClipboardPayload(IReadOnlyList<OperationSource> Sources, bool IsCut);

public sealed class ClipboardService
{
    private static readonly DataFormat<ClipboardPayload> PayloadFormat =
        DataFormat.CreateInProcessFormat<ClipboardPayload>("ordoa-clipboard");

    private readonly Func<TopLevel?> _topLevel;
    private ClipboardPayload? _payload;

    public ClipboardService(Func<TopLevel?> topLevel)
    {
        _topLevel = topLevel;
    }

    public bool HasInternal => _payload is { Sources.Count: > 0 };
    public bool IsCut => _payload?.IsCut == true;
    public IReadOnlyList<OperationSource> InternalSources => _payload?.Sources ?? [];

    public event EventHandler? Changed;

    public async Task SetInternalAsync(IReadOnlyList<OperationSource> sources, bool cut)
    {
        _payload = new ClipboardPayload(sources, cut);
        await PublishAsync(_payload).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ClipboardPayload?> TryReadInternalAsync()
    {
        var topLevel = _topLevel();
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null)
            return _payload;

        try
        {
            var data = await clipboard.TryGetInProcessDataAsync().ConfigureAwait(false);
            if (data is not null)
            {
                var payload = await data.TryGetValueAsync(PayloadFormat).ConfigureAwait(false);
                if (payload is { Sources.Count: > 0 })
                {
                    _payload = payload;
                    return payload;
                }
            }
        }
        catch
        {
            // Some platforms do not support in-process clipboard retrieval.
        }

        return _payload;
    }

    public async Task<IReadOnlyList<OperationSource>> TryReadExternalAsync(CancellationToken cancellationToken = default)
    {
        var topLevel = _topLevel();
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null)
            return [];

        try
        {
            using var data = await clipboard.TryGetDataAsync().ConfigureAwait(false);
            if (data is null)
                return [];

            var paths = new List<string>();
            var files = await data.TryGetFilesAsync().ConfigureAwait(false);
            if (files is not null)
            {
                foreach (var file in files)
                {
                    var path = file.TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }
            }

            if (paths.Count == 0)
            {
                var text = await data.TryGetTextAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                    paths.AddRange(ParseTextPaths(text));
            }

            var result = new List<OperationSource>();
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var entry = await LocalFileSystem.Instance.GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
                    if (entry is not null)
                        result.Add(new OperationSource(LocalFileSystem.Instance, entry));
                }
                catch { /* skip invalid clipboard path */ }
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("读取系统剪贴板失败", ex);
            return [];
        }
    }

    public async Task SetTextAsync(string text)
    {
        var topLevel = _topLevel();
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null)
            return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer).ConfigureAwait(false);
    }

    public async Task ConsumeCutAsync()
    {
        if (_payload is not { IsCut: true } payload)
            return;

        var copy = new ClipboardPayload(payload.Sources, false);
        _payload = copy;
        try
        {
            await PublishAsync(copy).ConfigureAwait(false);
        }
        catch { /* ignore */ }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _payload = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task PublishAsync(ClipboardPayload payload)
    {
        var topLevel = _topLevel();
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null)
            return;

        var transfer = new DataTransfer();
        var textItem = new DataTransferItem();
        textItem.SetText(string.Join(Environment.NewLine,
            payload.Sources.Select(s => s.FileSystem.GetDisplayPath(s.Path))));
        textItem.Set(PayloadFormat, payload);
        transfer.Add(textItem);

        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider is not null)
        {
            foreach (var source in payload.Sources)
            {
                if (!source.FileSystem.IsLocal)
                    continue;
                try
                {
                    IStorageItem? item = source.IsDirectory
                        ? await storageProvider.TryGetFolderFromPathAsync(source.Path).ConfigureAwait(false)
                        : await storageProvider.TryGetFileFromPathAsync(source.Path).ConfigureAwait(false);
                    if (item is not null)
                        transfer.Add(DataTransferItem.CreateFile(item));
                }
                catch { /* local path may no longer exist */ }
            }
        }

        await clipboard.SetDataAsync(transfer).ConfigureAwait(false);
    }

    private static IEnumerable<string> ParseTextPaths(string text)
    {
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(line, UriKind.Absolute, out var uri))
                    yield return uri.LocalPath;
                continue;
            }

            if (Path.IsPathRooted(line))
                yield return line;
        }
    }
}
