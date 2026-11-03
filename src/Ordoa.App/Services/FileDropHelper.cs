using Avalonia.Input;
using Avalonia.Platform.Storage;
using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>
/// Shared helpers for file drag and drop: reading the in-process payload,
/// resolving external files/text into sources, and applying modifier keys.
/// </summary>
public static class FileDropHelper
{
    public static FileDragPayload? GetPayload(IDataTransfer data)
        => FileDragContext.Current ?? data.TryGetValue(FileDragContext.Format);

    public static bool HasData(IDataTransfer data, FileDragPayload? payload)
        => payload is { Sources.Count: > 0 } ||
           data.Contains(DataFormat.File) ||
           data.Contains(DataFormat.Text);

    /// <summary>
    /// True when every source can be moved to the XDG trash: local and not
    /// already living in a trash file system.
    /// </summary>
    public static bool IsTrashable(IReadOnlyList<OperationSource>? sources)
        => sources is { Count: > 0 } &&
           sources.All(s => s.FileSystem.IsLocal && s.FileSystem is not TrashFileSystem);

    public static TransferMode ResolveMode(
        KeyModifiers modifiers,
        IReadOnlyList<OperationSource>? sources,
        string targetFileSystemId,
        bool external)
    {
        var same = !external &&
                   sources is { Count: > 0 } &&
                   sources.All(s => string.Equals(s.FileSystem.Id, targetFileSystemId, StringComparison.Ordinal));
        return TransferModifiers.Resolve(
            modifiers.HasFlag(KeyModifiers.Control),
            modifiers.HasFlag(KeyModifiers.Shift),
            same);
    }

    public static bool IsMove(KeyModifiers modifiers, IReadOnlyList<OperationSource>? sources,
        string targetFileSystemId, bool external)
        => ResolveMode(modifiers, sources, targetFileSystemId, external) == TransferMode.Move;

    /// <summary>
    /// True when dropping would only try to move files back into their current
    /// directory (or move a directory onto itself). That is always a no-op:
    /// treating it as an operation makes the conflict resolver rename files.
    /// </summary>
    public static bool IsNoOpMove(IReadOnlyList<OperationSource> sources,
        IFileSystem destinationFileSystem, string destinationDirectory)
    {
        if (sources.Count == 0)
            return false;

        var comparison = destinationFileSystem.IsCaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var destination = destinationFileSystem.NormalizePath(destinationDirectory);

        foreach (var source in sources)
        {
            if (!string.Equals(source.FileSystem.Id, destinationFileSystem.Id, StringComparison.Ordinal))
                return false;

            var sourcePath = source.FileSystem.NormalizePath(source.Path);
            var parent = source.FileSystem.NormalizePath(source.FileSystem.GetParent(source.Path));
            if (string.Equals(parent, destination, comparison))
                continue;
            if (source.IsDirectory && string.Equals(sourcePath, destination, comparison))
                continue;
            return false;
        }

        return true;
    }

    public static async Task<IReadOnlyList<OperationSource>> ResolveSourcesAsync(IDataTransfer data)
    {
        var payload = GetPayload(data);
        if (payload is { Sources.Count: > 0 })
            return payload.Sources;

        var sources = new List<OperationSource>();
        var files = data.TryGetFiles();
        if (files is not null)
        {
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                var entry = await LocalFileSystem.Instance.GetEntryAsync(path).ConfigureAwait(false);
                if (entry is not null)
                    sources.Add(new OperationSource(LocalFileSystem.Instance, entry));
            }
        }

        if (sources.Count == 0)
        {
            var text = data.TryGetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = line.Trim();
                    if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                        Uri.TryCreate(path, UriKind.Absolute, out var uri))
                        path = uri.LocalPath;
                    if (!Path.IsPathRooted(path))
                        continue;
                    var entry = await LocalFileSystem.Instance.GetEntryAsync(path).ConfigureAwait(false);
                    if (entry is not null)
                        sources.Add(new OperationSource(LocalFileSystem.Instance, entry));
                }
            }
        }

        return sources;
    }
}
