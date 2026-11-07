using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public enum BatchRenameCaseMode
{
    None,
    Lower,
    Upper,
    Title,
}

public enum BatchRenameExtensionMode
{
    Keep,
    Lower,
    Upper,
    Custom,
    Remove,
}

public enum BatchRenameNumberPosition
{
    Prefix,
    Suffix,
}

public sealed class BatchRenameOptions
{
    public string FindText { get; set; } = string.Empty;
    public string ReplaceText { get; set; } = string.Empty;
    public bool MatchCase { get; set; }
    /// <summary>Treat FindText as a regular expression; ReplaceText may use $1 groups.</summary>
    public bool UseRegex { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public bool Numbering { get; set; }
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    public int NumberPadding { get; set; } = 3;
    public string NumberSeparator { get; set; } = "_";
    public BatchRenameNumberPosition NumberPosition { get; set; } = BatchRenameNumberPosition.Suffix;
    public BatchRenameCaseMode CaseMode { get; set; }
    public BatchRenameExtensionMode ExtensionMode { get; set; } = BatchRenameExtensionMode.Keep;
    public string CustomExtension { get; set; } = string.Empty;
}

public enum BatchRenameIssue
{
    None,
    InvalidName,
    DuplicateName,
    NameExists,
}

public sealed record BatchRenameRow(
    FileEntry Entry,
    string NewName,
    BatchRenameIssue Issue,
    string? IssueText,
    bool IsChanged)
{
    public bool HasIssue => Issue != BatchRenameIssue.None;
}

public sealed record BatchRenamePlan(IReadOnlyList<BatchRenameRow> Rows)
{
    public int ChangeCount => Rows.Count(r => r.IsChanged);
    public int IssueCount => Rows.Count(r => r.HasIssue);
    public bool CanApply => ChangeCount > 0 && IssueCount == 0;
}

public sealed record BatchRenameRequest(
    IReadOnlyList<FileEntry> Entries,
    IReadOnlyList<string> ExistingNames,
    bool CaseSensitive,
    FileNameRuleSet NameRules);

public sealed record BatchRenameOperation(FileEntry Entry, string NewName);

public sealed record BatchRenameExecutionResult(int Renamed, int Failed, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<BatchRenameOperation> Completed { get; init; } = [];
}

public static class BatchRenamePlanner
{
    public static BatchRenamePlan CreatePlan(
        IReadOnlyList<FileEntry> entries,
        IReadOnlyList<string> existingNames,
        BatchRenameOptions options,
        bool caseSensitive,
        FileNameRuleSet rules,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var patternError = ValidateRegex(options);

        var rows = new List<BatchRenameRow>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var newName = BuildName(entry, options, timestamp, i);
            var changed = !string.Equals(entry.Name, newName, StringComparison.Ordinal);
            rows.Add(new BatchRenameRow(entry, newName, BatchRenameIssue.None, null, changed));
        }

        if (patternError is not null)
        {
            for (var i = 0; i < rows.Count; i++)
                rows[i] = rows[i] with { Issue = BatchRenameIssue.InvalidName, IssueText = patternError };
            return new BatchRenamePlan(rows);
        }

        var changedRows = rows.Where(r => r.IsChanged).ToList();
        if (changedRows.Count == 0)
            return new BatchRenamePlan(rows);

        var duplicateNames = changedRows
            .GroupBy(r => r.NewName, comparer)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(comparer);

        // Names that will be freed by this batch can safely be reused. Everything
        // else that still exists in the directory is an occupied target.
        var vacatedNames = changedRows.Select(r => r.Entry.Name).ToHashSet(comparer);
        var occupiedNames = existingNames
            .Where(name => !vacatedNames.Contains(name))
            .ToHashSet(comparer);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.IsChanged)
                continue;

            var error = FileNameRules.Validate(row.NewName, rules);
            if (error is not null)
            {
                rows[i] = row with { Issue = BatchRenameIssue.InvalidName, IssueText = error };
                continue;
            }

            if (duplicateNames.Contains(row.NewName))
            {
                rows[i] = row with
                {
                    Issue = BatchRenameIssue.DuplicateName,
                    IssueText = L10n.T("新名称与同批次中的其他项目重复"),
                };
                continue;
            }

            if (occupiedNames.Contains(row.NewName))
            {
                rows[i] = row with
                {
                    Issue = BatchRenameIssue.NameExists,
                    IssueText = L10n.T("同名项目已存在"),
                };
            }
        }

        return new BatchRenamePlan(rows);
    }

    /// <summary>Builds the proposed name for a single entry.</summary>
    public static string BuildName(FileEntry entry, BatchRenameOptions options, DateTimeOffset now, int index)
    {
        string stem;
        string extension;
        if (entry.IsDirectory)
        {
            stem = entry.Name;
            extension = string.Empty;
        }
        else
        {
            (stem, extension) = SplitName(entry.Name);
        }

        if (!string.IsNullOrEmpty(options.FindText))
        {
            if (options.UseRegex)
            {
                try
                {
                    stem = Regex.Replace(stem, options.FindText, options.ReplaceText ?? string.Empty,
                        options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException)
                {
                    // Invalid patterns are reported by ValidateRegex before rows are built.
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }
            else
            {
                stem = stem.Replace(
                    options.FindText,
                    options.ReplaceText ?? string.Empty,
                    options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }
        }

        stem = options.CaseMode switch
        {
            BatchRenameCaseMode.Lower => stem.ToLowerInvariant(),
            BatchRenameCaseMode.Upper => stem.ToUpperInvariant(),
            BatchRenameCaseMode.Title => TitleCaseFirst(stem),
            _ => stem,
        };

        var prefix = ExpandTokens(options.Prefix, entry, now);
        var suffix = ExpandTokens(options.Suffix, entry, now);
        var core = prefix + stem + suffix;

        if (options.Numbering)
        {
            var padding = Math.Clamp(options.NumberPadding, 1, 9);
            var number = ((long)options.NumberStart + (long)index * options.NumberStep)
                .ToString("D" + padding.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            var separator = options.NumberSeparator ?? string.Empty;
            core = options.NumberPosition == BatchRenameNumberPosition.Prefix
                ? number + separator + core
                : core + separator + number;
        }

        var resultExtension = entry.IsDirectory
            ? string.Empty
            : options.ExtensionMode switch
            {
                BatchRenameExtensionMode.Lower => extension.ToLowerInvariant(),
                BatchRenameExtensionMode.Upper => extension.ToUpperInvariant(),
                BatchRenameExtensionMode.Custom => NormalizeExtension(options.CustomExtension),
                BatchRenameExtensionMode.Remove => string.Empty,
                _ => extension,
            };

        return core + resultExtension;
    }

    private static string? ValidateRegex(BatchRenameOptions options)
    {
        if (!options.UseRegex || string.IsNullOrEmpty(options.FindText))
            return null;
        try
        {
            _ = new Regex(options.FindText, RegexOptions.None, TimeSpan.FromSeconds(1));
            return null;
        }
        catch (ArgumentException ex)
        {
            return L10n.Format("正则表达式无效：{0}", ex.Message);
        }
    }

    public static (string Stem, string Extension) SplitName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return (name, string.Empty);

        return (name[..dot], name[dot..]);
    }

    public static string ExpandTokens(string? template, FileEntry entry, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{'))
            return template ?? string.Empty;

        var builder = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                builder.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                builder.Append(template[i..]);
                break;
            }

            var token = template[(i + 1)..close];
            i = close;

            if (!TryExpandToken(token, entry, now, out var value))
            {
                builder.Append('{').Append(token).Append('}');
                continue;
            }

            builder.Append(value);
        }

        return builder.ToString();
    }

    private static bool TryExpandToken(string token, FileEntry entry, DateTimeOffset now, out string value)
    {
        value = string.Empty;
        var colon = token.IndexOf(':');
        var key = (colon >= 0 ? token[..colon] : token).Trim().ToLowerInvariant();
        var format = colon >= 0 ? token[(colon + 1)..] : null;

        switch (key)
        {
            case "date":
            case "today":
                return FormatDate(now, format, out value);
            case "mtime":
            case "modified":
                if (entry.Modified is null)
                    return true;
                return FormatDate(entry.Modified.Value, format, out value);
            case "ctime":
            case "created":
                if (entry.Created is null)
                    return true;
                return FormatDate(entry.Created.Value, format, out value);
            default:
                return false;
        }
    }

    private static bool FormatDate(DateTimeOffset date, string? format, out string value)
    {
        var effectiveFormat = string.IsNullOrWhiteSpace(format) ? "yyyy-MM-dd" : format;
        try
        {
            value = date.ToLocalTime().ToString(effectiveFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            value = date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return true;
    }

    private static string TitleCaseFirst(string value)
    {
        if (value.Length == 0)
            return value;
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string NormalizeExtension(string? extension)
    {
        var value = (extension ?? string.Empty).Trim();
        if (value.Length == 0 || value == ".")
            return string.Empty;

        return value.StartsWith('.') ? value : "." + value;
    }
}

public static class BatchRenameExecutor
{
    public static async Task<BatchRenameExecutionResult> ExecuteAsync(
        IFileSystem fileSystem,
        BatchRenamePlan plan,
        CancellationToken cancellationToken = default)
    {
        var rows = plan.Rows.Where(r => r.IsChanged && !r.HasIssue).ToList();
        if (rows.Count == 0)
            return new BatchRenameExecutionResult(0, 0, []);

        var comparer = fileSystem.IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var operations = new List<PreparedOperation>(rows.Count);

        foreach (var row in rows)
        {
            var parent = fileSystem.GetParent(row.Entry.Path);
            var source = fileSystem.NormalizePath(row.Entry.Path);
            var target = fileSystem.NormalizePath(fileSystem.Combine(parent, row.NewName));
            if (comparer.Equals(source, target))
            {
                // Case-only changes on a case-sensitive filesystem can still be real
                // renames, but an exact same path is a no-op.
                if (string.Equals(source, target, StringComparison.Ordinal))
                    continue;
            }

            operations.Add(new PreparedOperation(row, source, target, parent));
        }

        if (operations.Count == 0)
            return new BatchRenameExecutionResult(0, 0, []);

        var sourcePaths = operations.Select(o => o.Source).ToHashSet(comparer);
        var targetPaths = new HashSet<string>(comparer);
        var errors = new List<string>();

        foreach (var operation in operations)
        {
            if (!targetPaths.Add(operation.Target))
                errors.Add(FormatError(operation.Row, L10n.T("新名称与同批次中的其他项目重复")));
        }

        if (errors.Count > 0)
            return new BatchRenameExecutionResult(0, operations.Count, errors);

        // Refuse to overwrite a name that appeared after the preview was built.
        // Targets that are themselves sources will be freed by staging below.
        foreach (var operation in operations)
        {
            if (sourcePaths.Contains(operation.Target))
                continue;

            bool exists;
            try
            {
                exists = await fileSystem.ExistsAsync(operation.Target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new BatchRenameExecutionResult(0, operations.Count,
                    [FormatError(operation.Row, ex)]);
            }

            if (exists)
                errors.Add(FormatError(operation.Row, L10n.T("同名项目已存在")));
        }

        if (errors.Count > 0)
            return new BatchRenameExecutionResult(0, operations.Count, errors);

        foreach (var operation in operations)
        {
            operation.Stage = sourcePaths.Contains(operation.Target);
            if (operation.Stage)
            {
                operation.Temp = fileSystem.NormalizePath(fileSystem.Combine(
                    operation.Parent, ".ordoa-tmp-" + Guid.NewGuid().ToString("N")));
            }
        }

        // Move everything that would collide with a source out of the way first.
        var staged = new List<PreparedOperation>();
        foreach (var operation in operations.Where(o => o.Stage))
        {
            try
            {
                await fileSystem.MoveAsync(operation.Source, operation.Temp, cancellationToken).ConfigureAwait(false);
                staged.Add(operation);
            }
            catch (Exception ex)
            {
                errors.Add(FormatError(operation.Row, ex));

                // Nothing has been renamed to a final name yet, so restore the
                // staged items and leave the directory untouched.
                for (var i = staged.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        await fileSystem.MoveAsync(staged[i].Temp, staged[i].Source, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception rollback)
                    {
                        errors.Add(FormatError(staged[i].Row, rollback) + $" ({staged[i].Temp})");
                    }
                }

                return new BatchRenameExecutionResult(0, operations.Count, errors);
            }
        }

        var renamed = 0;
        var completed = new List<BatchRenameOperation>();

        foreach (var operation in operations.Where(o => !o.Stage))
        {
            try
            {
                await fileSystem.MoveAsync(operation.Source, operation.Target, cancellationToken).ConfigureAwait(false);
                renamed++;
                completed.Add(new BatchRenameOperation(operation.Row.Entry, operation.Row.NewName));
            }
            catch (Exception ex)
            {
                errors.Add(FormatError(operation.Row, ex));
            }
        }

        foreach (var operation in operations.Where(o => o.Stage))
        {
            var occupied = false;
            try
            {
                occupied = await fileSystem.ExistsAsync(operation.Target, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(FormatError(operation.Row, ex));
                occupied = true;
            }

            if (occupied)
            {
                errors.Add(FormatError(operation.Row, L10n.T("同名项目已存在")));

                try
                {
                    await fileSystem.MoveAsync(operation.Temp, operation.Source, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception restore)
                {
                    errors.Add(FormatError(operation.Row, restore) + $" ({operation.Temp})");
                }

                continue;
            }

            try
            {
                await fileSystem.MoveAsync(operation.Temp, operation.Target, cancellationToken).ConfigureAwait(false);
                renamed++;
                completed.Add(new BatchRenameOperation(operation.Row.Entry, operation.Row.NewName));
            }
            catch (Exception ex)
            {
                errors.Add(FormatError(operation.Row, ex));

                try
                {
                    await fileSystem.MoveAsync(operation.Temp, operation.Source, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception restore)
                {
                    errors.Add(FormatError(operation.Row, restore) + $" ({operation.Temp})");
                }
            }
        }

        return new BatchRenameExecutionResult(renamed, operations.Count - renamed, errors)
        {
            Completed = completed,
        };
    }

    private static string FormatError(BatchRenameRow row, Exception exception)
        => $"{row.Entry.Name}: {exception.Message}";

    private static string FormatError(BatchRenameRow row, string message)
        => $"{row.Entry.Name}: {message}";

    private sealed class PreparedOperation(
        BatchRenameRow row,
        string source,
        string target,
        string parent)
    {
        public BatchRenameRow Row { get; } = row;
        public string Source { get; } = source;
        public string Target { get; } = target;
        public string Parent { get; } = parent;
        public bool Stage { get; set; }
        public string Temp { get; set; } = string.Empty;
    }
}
