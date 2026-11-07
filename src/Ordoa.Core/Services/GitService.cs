using System.Diagnostics;
using System.Text;
using Ordoa.Core.Localization;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public enum GitChangeKind
{
    None,
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
}

/// <summary>One changed path reported by git status.</summary>
public sealed record GitFileChange(
    string FullPath,
    string RelativePath,
    char IndexStatus,
    char WorkTreeStatus,
    bool IsDirectoryHint = false,
    bool IsSubmodule = false)
{
    public bool IsUntracked => IndexStatus == '?' && WorkTreeStatus == '?';
    public bool IsConflicted => IndexStatus == 'U' || WorkTreeStatus == 'U' ||
                                IndexStatus == 'A' && WorkTreeStatus == 'A';
    public bool IsStaged => !IsUntracked && IndexStatus is not ('.' or ' ');
    public bool IsUnstaged => !IsUntracked && WorkTreeStatus is not ('.' or ' ');
    public bool IsDeleted => IndexStatus == 'D' || WorkTreeStatus == 'D';
    public bool IsAdded => IndexStatus == 'A' || WorkTreeStatus == 'A';
    public bool IsRenamed => IndexStatus == 'R' || WorkTreeStatus == 'R';
    public bool IsModified => IndexStatus == 'M' || WorkTreeStatus == 'M';

    public string Code
    {
        get
        {
            var value = IsConflicted
                ? "!"
                : IsUntracked
                    ? "?"
                    : IsStaged && IsUnstaged && IndexStatus != WorkTreeStatus
                        ? $"{IndexStatus}{WorkTreeStatus}"
                        : IsStaged
                            ? IndexStatus.ToString()
                            : WorkTreeStatus.ToString();
            return value.Trim();
        }
    }

    public GitChangeKind Kind
    {
        get
        {
            if (IsUntracked)
                return GitChangeKind.Untracked;
            if (IsConflicted)
                return GitChangeKind.Conflicted;
            if (IsDeleted)
                return GitChangeKind.Deleted;
            if (IsRenamed)
                return GitChangeKind.Renamed;
            if (IsAdded)
                return GitChangeKind.Added;
            if (IndexStatus == 'T' || WorkTreeStatus == 'T')
                return GitChangeKind.TypeChanged;
            if (IndexStatus == 'C' || WorkTreeStatus == 'C')
                return GitChangeKind.Copied;
            return GitChangeKind.Modified;
        }
    }

    public string Description
    {
        get
        {
            var text = Kind switch
            {
                GitChangeKind.Untracked => L10n.T("未跟踪"),
                GitChangeKind.Conflicted => L10n.T("冲突"),
                GitChangeKind.Deleted => L10n.T("已删除"),
                GitChangeKind.Renamed => L10n.T("已重命名"),
                GitChangeKind.Added => L10n.T("已新增"),
                GitChangeKind.TypeChanged => L10n.T("类型变更"),
                GitChangeKind.Copied => L10n.T("已复制"),
                _ => L10n.T("已修改"),
            };
            if (IsStaged && IsUnstaged && !IsUntracked && !IsConflicted)
                text += " · " + L10n.T("已暂存");
            else if (IsStaged && !IsUntracked && !IsConflicted)
                text += " · " + L10n.T("已暂存");
            return IsSubmodule ? L10n.T("子模块") + " · " + text : text;
        }
    }
}

public sealed class GitRepositoryStatus
{
    public required string Root { get; init; }
    public string Branch { get; init; } = string.Empty;
    public string? Upstream { get; init; }
    public bool IsDetached { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public IReadOnlyList<GitFileChange> Files { get; init; } = [];
    public IReadOnlyDictionary<string, GitFileChange> FilesByPath { get; init; } =
        new Dictionary<string, GitFileChange>(StringComparer.Ordinal);
    public IReadOnlySet<string> ChangedDirectories { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> TrackedPaths { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> IgnoredPaths { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> IgnoredDirectories { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> SubmodulePaths { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> TrackedDirectories { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public int ChangedCount => Files.Count;
    public int StagedCount => Files.Count(file => file.IsStaged);
    public int UnstagedCount => Files.Count(file => file.IsUnstaged);
    public int UntrackedCount => Files.Count(file => file.IsUntracked);
    public int ConflictedCount => Files.Count(file => file.IsConflicted);
    public bool IsClean => Files.Count == 0;
    public string BranchLabel => IsDetached || string.IsNullOrEmpty(Branch) ? L10n.T("游离 HEAD") : Branch;
}

/// <summary>Read-only git integration backed by the git executable.</summary>
public static class GitService
{
    public static string? FindRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var directory = Directory.Exists(path)
                ? Path.GetFullPath(path)
                : Path.GetDirectoryName(Path.GetFullPath(path));
            for (var depth = 0; directory is not null && depth < 256; depth++)
            {
                var marker = Path.Combine(directory, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                    return Path.TrimEndingDirectorySeparator(directory);
                directory = Directory.GetParent(directory)?.FullName;
            }
        }
        catch
        {
            // Invalid paths simply have no repository.
        }

        return null;
    }

    public static async Task<GitRepositoryStatus?> GetStatusAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var root = FindRepository(path);
        if (root is null)
            return null;

        var fullPath = Path.GetFullPath(path);
        var scope = string.Equals(Path.TrimEndingDirectorySeparator(fullPath),
            Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal)
            ? "."
            : Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return await GetStatusForRootAsync(root, cancellationToken, scope).ConfigureAwait(false);
    }

    public static async Task<GitRepositoryStatus?> GetStatusForRootAsync(string root,
        CancellationToken cancellationToken = default, string? scope = null)
    {
        var statusTask = RunGitAsync(root,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal"],
            cancellationToken);
        var trackedTask = scope is null
            ? Task.FromResult<string?>(null)
            : RunGitAsync(root, ["ls-files", "-z", "--stage", "--", scope], cancellationToken);
        var ignoredTask = scope is null
            ? Task.FromResult<string?>(null)
            : RunGitAsync(root,
                ["status", "--porcelain=v2", "-z", "--ignored=matching", "--", scope],
                cancellationToken);

        await Task.WhenAll(statusTask, trackedTask, ignoredTask).ConfigureAwait(false);
        var output = await statusTask.ConfigureAwait(false);
        if (output is null)
            return null;

        return Parse(root, output, await trackedTask.ConfigureAwait(false),
            await ignoredTask.ConfigureAwait(false));
    }

    private static GitRepositoryStatus Parse(string root, string output, string? trackedOutput,
        string? ignoredOutput)
    {
        var branch = string.Empty;
        string? upstream = null;
        var detached = false;
        var ahead = 0;
        var behind = 0;
        var files = new List<GitFileChange>();
        var comparer = OperatingSystem.IsLinux()
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index].TrimEnd('\r', '\n');
            if (token.Length == 0)
                continue;

            if (token.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = token["# branch.head ".Length..].Trim();
                detached = branch == "(detached)";
                continue;
            }
            if (token.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = token["# branch.upstream ".Length..].Trim();
                continue;
            }
            if (token.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = token["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.StartsWith('+') && int.TryParse(part[1..], out var aheadCount))
                        ahead = aheadCount;
                    else if (part.StartsWith('-') && int.TryParse(part[1..], out var behindCount))
                        behind = behindCount;
                }
                continue;
            }
            if (token.StartsWith("1 ", StringComparison.Ordinal))
            {
                var fields = token.Split(' ', 9);
                if (fields.Length == 9)
                    files.Add(CreateChange(root, fields[8], fields[1], comparer,
                        submodule: IsSubmoduleStatus(fields[2])));
                continue;
            }
            if (token.StartsWith("2 ", StringComparison.Ordinal))
            {
                var fields = token.Split(' ', 10);
                if (fields.Length == 10)
                    files.Add(CreateChange(root, fields[9], fields[1], comparer,
                        submodule: IsSubmoduleStatus(fields[2])));
                if (index + 1 < tokens.Length)
                    index++; // Original path for renames/copies.
                continue;
            }
            if (token.StartsWith("u ", StringComparison.Ordinal))
            {
                var fields = token.Split(' ', 11);
                if (fields.Length == 11)
                    files.Add(CreateChange(root, fields[10], fields[1], comparer,
                        submodule: IsSubmoduleStatus(fields[2])));
                continue;
            }
            if (token.StartsWith("? ", StringComparison.Ordinal))
            {
                var path = token[2..];
                files.Add(CreateChange(root, path, "??", comparer,
                    path.EndsWith('/') || path.EndsWith('\\')));
            }
        }

        var byPath = files
            .GroupBy(file => file.FullPath, comparer)
            .ToDictionary(group => group.Key, group => group.First(), comparer);
        var directories = new HashSet<string>(comparer);
        foreach (var file in files)
        {
            if (file.IsDirectoryHint)
                directories.Add(Path.TrimEndingDirectorySeparator(file.FullPath));
            var directory = Path.GetDirectoryName(file.FullPath);
            while (!string.IsNullOrEmpty(directory) &&
                   directory.StartsWith(root, comparer == StringComparer.Ordinal
                       ? StringComparison.Ordinal
                       : StringComparison.OrdinalIgnoreCase))
            {
                if (!directories.Add(Path.TrimEndingDirectorySeparator(directory)))
                    break;
                var parent = Path.GetDirectoryName(directory);
                if (string.Equals(parent, directory, StringComparison.Ordinal))
                    break;
                directory = parent;
            }
        }

        var tracked = new HashSet<string>(comparer);
        var submodules = new HashSet<string>(comparer);
        if (!string.IsNullOrEmpty(trackedOutput))
        {
            foreach (var record in trackedOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = record.IndexOf('\t');
                if (tab <= 0)
                    continue;
                var metadata = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (metadata.Length == 0)
                    continue;
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                    Path.Combine(root, record[(tab + 1)..].Replace('/', Path.DirectorySeparatorChar))));
                tracked.Add(full);
                if (metadata[0] == "160000")
                    submodules.Add(full);
            }
        }

        var trackedDirectories = new HashSet<string>(comparer);
        foreach (var path in tracked)
        {
            var directory = submodules.Contains(path) ? path : Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(directory) &&
                   directory.StartsWith(root, comparer == StringComparer.Ordinal
                       ? StringComparison.Ordinal
                       : StringComparison.OrdinalIgnoreCase))
            {
                if (!trackedDirectories.Add(Path.TrimEndingDirectorySeparator(directory)))
                    break;
                var parent = Path.GetDirectoryName(directory);
                if (string.Equals(parent, directory, StringComparison.Ordinal))
                    break;
                directory = parent;
            }
        }

        var ignored = new HashSet<string>(comparer);
        var ignoredDirectories = new HashSet<string>(comparer);
        if (!string.IsNullOrEmpty(ignoredOutput))
        {
            foreach (var token in ignoredOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.StartsWith("! ", StringComparison.Ordinal))
                    continue;
                var gitPath = token[2..];
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                    Path.Combine(root, gitPath.Replace('/', Path.DirectorySeparatorChar))));
                ignored.Add(full);
                if (gitPath.EndsWith('/') || gitPath.EndsWith('\\'))
                    ignoredDirectories.Add(full);
            }
        }

        return new GitRepositoryStatus
        {
            Root = root,
            Branch = branch,
            Upstream = upstream,
            IsDetached = detached,
            Ahead = ahead,
            Behind = behind,
            Files = files
                .OrderByDescending(file => file.IsConflicted)
                .ThenByDescending(file => file.IsUntracked)
                .ThenBy(file => file.RelativePath, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            FilesByPath = byPath,
            ChangedDirectories = directories,
            TrackedPaths = tracked,
            IgnoredPaths = ignored,
            IgnoredDirectories = ignoredDirectories,
            SubmodulePaths = submodules,
            TrackedDirectories = trackedDirectories,
        };
    }

    private static bool IsSubmoduleStatus(string status)
        => status.Length > 0 && status[0] == 'S';

    private static GitFileChange CreateChange(string root, string gitPath, string status,
        StringComparer comparer, bool isDirectoryHint = false, bool submodule = false)
    {
        var relative = gitPath.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var full = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(root, relative)));
        var x = status.Length > 0 ? status[0] : '.';
        var y = status.Length > 1 ? status[1] : '.';
        return new GitFileChange(full, gitPath.Replace('\\', '/'), x, y, isDirectoryHint, submodule);
    }

    private static async Task<string?> RunGitAsync(string root, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(root);
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["LC_ALL"] = "C";

            using var process = Process.Start(start);
            if (process is null)
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }

            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // git missing or unavailable.
            return null;
        }
    }
}
