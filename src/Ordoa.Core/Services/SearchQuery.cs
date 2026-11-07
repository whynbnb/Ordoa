using System.Globalization;
using System.Text;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public enum SearchEntryType
{
    Any,
    File,
    Folder,
    Image,
    Video,
    Audio,
    Text,
    Document,
    Archive,
    Code,
    Config,
}

/// <summary>
/// Parsed search expression. Plain words match the file name; the following
/// filters are supported:
/// <code>
///   *.cs                     glob match
///   -node_modules            exclude matches and prune matching folders
///   ext:cs,json              extension filter
///   type:file|folder|image   entry category
///   size:&gt;10MB              size comparison (B, KB, MB, GB, TB)
///   modified:&gt;7d            relative age (h, d, w, mo, y)
///   modified:2024-01-01..2025-01-01
///   content:"hello world"    search file contents (local files only)
/// </code>
/// </summary>
public sealed record SearchQuery
{
    private static readonly string[] KnownKeys =
        ["ext", "type", "size", "modified", "content", "text", "name"];

    public IReadOnlyList<string> Terms { get; init; } = [];
    public IReadOnlyList<string> Excludes { get; init; } = [];
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public SearchEntryType Type { get; init; } = SearchEntryType.Any;
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTimeOffset? ModifiedAfter { get; init; }
    public DateTimeOffset? ModifiedBefore { get; init; }
    public IReadOnlyList<string> ContentTerms { get; init; } = [];

    public bool HasContent => ContentTerms.Count > 0;
    public bool HasSizeFilter => MinSize is not null || MaxSize is not null;
    public bool HasModifiedFilter => ModifiedAfter is not null || ModifiedBefore is not null;
    public bool IsEmpty => Terms.Count == 0 && Excludes.Count == 0 && Extensions.Count == 0 &&
                           Type == SearchEntryType.Any && !HasSizeFilter && !HasModifiedFilter &&
                           !HasContent;

    /// <summary>True when a directory name should not be descended into.</summary>
    public bool ShouldPruneDirectory(string name) => Excludes.Any(exclude => GlobMatcher.IsMatch(name, exclude));

    public static SearchQuery Parse(string? text, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.Now;
        var terms = new List<string>();
        var excludes = new List<string>();
        var extensions = new List<string>();
        var content = new List<string>();
        var type = SearchEntryType.Any;
        long? minSize = null;
        long? maxSize = null;
        DateTimeOffset? modifiedAfter = null;
        DateTimeOffset? modifiedBefore = null;

        foreach (var token in Tokenize(text))
        {
            if (token.Length == 0)
                continue;

            if (token.Length > 1 && token[0] == '-')
            {
                excludes.Add(token[1..]);
                continue;
            }

            var separator = token.IndexOf(':');
            if (separator <= 0)
            {
                terms.Add(token);
                continue;
            }

            var key = token[..separator].ToLowerInvariant();
            if (!KnownKeys.Contains(key))
            {
                terms.Add(token);
                continue;
            }

            var value = token[(separator + 1)..].Trim();
            if (value.Length == 0)
                continue;

            switch (key)
            {
                case "ext":
                    foreach (var item in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
                        extensions.Add(item.Trim().TrimStart('.').TrimStart('*').ToLowerInvariant());
                    break;
                case "type":
                    type = ParseType(value);
                    break;
                case "size":
                    ParseSize(value, ref minSize, ref maxSize);
                    break;
                case "modified":
                    ParseModified(value, reference, ref modifiedAfter, ref modifiedBefore);
                    break;
                case "content":
                case "text":
                    content.Add(value);
                    break;
                default:
                    terms.Add(value);
                    break;
            }
        }

        return new SearchQuery
        {
            Terms = terms,
            Excludes = excludes,
            Extensions = extensions.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Type = type,
            MinSize = minSize,
            MaxSize = maxSize,
            ModifiedAfter = modifiedAfter,
            ModifiedBefore = modifiedBefore,
            ContentTerms = content,
        };
    }

    public bool MatchesMetadata(FileEntry entry)
    {
        if (Type == SearchEntryType.Folder && !entry.IsDirectory)
            return false;
        if (Type is not SearchEntryType.Any and not SearchEntryType.Folder && entry.IsDirectory)
            return false;

        if (Type is not SearchEntryType.Any and not SearchEntryType.Folder &&
            !MatchesType(entry.Extension, Type))
            return false;

        if (Extensions.Count > 0 && (entry.IsDirectory ||
                                     !Extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase)))
            return false;

        if (HasSizeFilter)
        {
            if (entry.IsDirectory)
                return false;
            if (MinSize is not null && entry.Size < MinSize)
                return false;
            if (MaxSize is not null && entry.Size > MaxSize)
                return false;
        }

        if (HasModifiedFilter)
        {
            if (entry.Modified is not { } modified)
                return false;
            if (ModifiedAfter is not null && modified < ModifiedAfter)
                return false;
            if (ModifiedBefore is not null && modified > ModifiedBefore)
                return false;
        }

        foreach (var exclude in Excludes)
        {
            if (GlobMatcher.IsMatch(entry.Name, exclude))
                return false;
        }

        foreach (var term in Terms)
        {
            if (!GlobMatcher.IsMatch(entry.Name, term))
                return false;
        }

        return true;
    }

    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var current = new StringBuilder();
        var quote = '\0';
        foreach (var character in text)
        {
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    current.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static SearchEntryType ParseType(string value) => value.ToLowerInvariant() switch
    {
        "file" or "files" => SearchEntryType.File,
        "folder" or "folders" or "dir" or "directory" => SearchEntryType.Folder,
        "image" or "images" or "picture" => SearchEntryType.Image,
        "video" or "videos" => SearchEntryType.Video,
        "audio" or "music" => SearchEntryType.Audio,
        "text" => SearchEntryType.Text,
        "document" or "doc" or "documents" => SearchEntryType.Document,
        "archive" or "zip" => SearchEntryType.Archive,
        "code" or "source" => SearchEntryType.Code,
        "config" => SearchEntryType.Config,
        _ => SearchEntryType.Any,
    };

    private static bool MatchesType(string extension, SearchEntryType type) => type switch
    {
        SearchEntryType.File => true,
        SearchEntryType.Image => MimeTypes.IsImage(extension),
        SearchEntryType.Video => extension.ToLowerInvariant() is
            "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "m4v" or "mpg" or "mpeg" or "ogv",
        SearchEntryType.Audio => extension.ToLowerInvariant() is
            "mp3" or "flac" or "ogg" or "opus" or "wav" or "m4a" or "aac" or "wma" or "aiff" or "ape",
        SearchEntryType.Text => MimeTypes.IsText(extension) || extension.ToLowerInvariant() is "log" or "rtf",
        SearchEntryType.Document => extension.ToLowerInvariant() is
            "pdf" or "doc" or "docx" or "odt" or "xls" or "xlsx" or "ods" or "ppt" or "pptx" or "odp" or "rtf",
        SearchEntryType.Archive => MimeTypes.IsArchive(extension) || extension.ToLowerInvariant() is "zst" or "jar",
        SearchEntryType.Code => extension.ToLowerInvariant() is
            "js" or "mjs" or "ts" or "tsx" or "jsx" or "py" or "rb" or "go" or "rs" or "java" or "kt" or "kts" or
            "c" or "h" or "cc" or "cpp" or "cxx" or "hpp" or "cs" or "fs" or "php" or "sh" or "bash" or "zsh" or
            "fish" or "lua" or "swift" or "scala" or "clj" or "ex" or "exs" or "hs" or "zig" or "vim",
        SearchEntryType.Config => extension.ToLowerInvariant() is
            "json" or "yaml" or "yml" or "toml" or "xml" or "ini" or "conf" or "cfg" or "env" or "properties" or
            "editorconfig" or "gitignore" or "gitattributes" or "dockerfile" or "service" or "desktop",
        _ => true,
    };

    private static void ParseSize(string value, ref long? minSize, ref long? maxSize)
    {
        var range = value.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (TryParseSize(value[..range], out var low))
                minSize = low;
            if (TryParseSize(value[(range + 2)..], out var high))
                maxSize = high;
            return;
        }

        if (value.StartsWith(">=", StringComparison.Ordinal))
        {
            if (TryParseSize(value[2..], out var size))
                minSize = size;
            return;
        }
        if (value.StartsWith("<=", StringComparison.Ordinal))
        {
            if (TryParseSize(value[2..], out var size))
                maxSize = size;
            return;
        }
        if (value.StartsWith('>'))
        {
            if (TryParseSize(value[1..], out var size))
                minSize = size + 1;
            return;
        }
        if (value.StartsWith('<'))
        {
            if (TryParseSize(value[1..], out var size))
                maxSize = Math.Max(0, size - 1);
            return;
        }

        if (TryParseSize(value, out var bare))
            minSize = bare;
    }

    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim();
        if (text.Length == 0)
            return false;

        var index = 0;
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] is '.' or ','))
            index++;
        var numberText = text[..index].Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return false;

        var unit = text[index..].Trim().ToLowerInvariant();
        var multiplier = unit switch
        {
            "" or "b" => 1L,
            "k" or "kb" or "kib" => 1024L,
            "m" or "mb" or "mib" => 1024L * 1024,
            "g" or "gb" or "gib" => 1024L * 1024 * 1024,
            "t" or "tb" or "tib" => 1024L * 1024 * 1024 * 1024,
            _ => -1L,
        };

        if (multiplier < 0)
            return false;

        bytes = (long)Math.Round(number * multiplier);
        return true;
    }

    private static void ParseModified(string value, DateTimeOffset now,
        ref DateTimeOffset? after, ref DateTimeOffset? before)
    {
        var range = value.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (TryParseDate(value[..range], now, out var low))
                after = low;
            if (TryParseDate(value[(range + 2)..], now, out var high))
                before = high;
            return;
        }

        if (value.StartsWith(">=", StringComparison.Ordinal))
        {
            if (TryParseDate(value[2..], now, out var date))
                after = date;
            return;
        }
        if (value.StartsWith("<=", StringComparison.Ordinal))
        {
            if (TryParseDate(value[2..], now, out var date))
                before = date;
            return;
        }
        if (value.StartsWith('>'))
        {
            if (TryParseDate(value[1..], now, out var date))
                after = date;
            return;
        }
        if (value.StartsWith('<'))
        {
            if (TryParseDate(value[1..], now, out var date))
                before = date;
            return;
        }

        if (value.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            after = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            return;
        }
        if (value.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(-1);
            after = start;
            before = start.AddDays(1);
            return;
        }
        if (value.Equals("week", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("thisweek", StringComparison.OrdinalIgnoreCase))
        {
            var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            after = start.AddDays(-(int)now.DayOfWeek);
            return;
        }
        if (value.Equals("month", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("thismonth", StringComparison.OrdinalIgnoreCase))
        {
            after = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
            return;
        }
        if (value.Equals("year", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("thisyear", StringComparison.OrdinalIgnoreCase))
        {
            after = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
            return;
        }

        if (TryParseAge(value, now, out var age))
        {
            after = age;
            return;
        }

        if (TryParseDate(value, now, out var day))
        {
            after = day;
            before = day.AddDays(1);
        }
    }

    internal static bool TryParseAge(string text, DateTimeOffset now, out DateTimeOffset result)
    {
        result = default;
        text = text.Trim().ToLowerInvariant();
        if (text.Length < 2)
            return false;

        var index = 0;
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.'))
            index++;
        if (!double.TryParse(text[..index], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            return false;

        var unit = text[index..];
        result = unit switch
        {
            "min" or "minute" or "minutes" or "m" => now.AddMinutes(-amount),
            "h" or "hour" or "hours" => now.AddHours(-amount),
            "d" or "day" or "days" => now.AddDays(-amount),
            "w" or "week" or "weeks" => now.AddDays(-amount * 7),
            "mo" or "month" or "months" => now.AddMonths(-(int)Math.Round(amount)),
            "y" or "year" or "years" => now.AddYears(-(int)Math.Round(amount)),
            _ => default,
        };

        return unit is "min" or "minute" or "minutes" or "m" or "h" or "hour" or "hours" or "d" or "day" or
            "days" or "w" or "week" or "weeks" or "mo" or "month" or "months" or "y" or "year" or "years";
    }

    private static bool TryParseDate(string text, DateTimeOffset now, out DateTimeOffset result)
    {
        if (TryParseAge(text, now, out result))
            return true;

        text = text.Trim();
        if (text.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            result = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            return true;
        }

        string[] formats = ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm", "yyyyMMdd"];
        if (DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out result))
            return true;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            result = new DateTimeOffset(parsed);
            return true;
        }

        result = default;
        return false;
    }
}
