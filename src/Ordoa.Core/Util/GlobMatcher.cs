using System.Text.RegularExpressions;

namespace Ordoa.Core.Util;

/// <summary>Simple glob matching used by the search box (supports * and ?).</summary>
public static class GlobMatcher
{
    public static bool IsMatch(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return true;

        if (pattern.IndexOfAny(['*', '?']) < 0)
            return name.Contains(pattern, StringComparison.CurrentCultureIgnoreCase);

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string SuggestFileName(string baseName, Func<string, bool> exists)
    {
        if (!exists(baseName))
            return baseName;

        var dot = baseName.LastIndexOf('.');
        var stem = dot > 0 ? baseName[..dot] : baseName;
        var extension = dot > 0 ? baseName[dot..] : string.Empty;

        for (var i = 1; i < 10000; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!exists(candidate))
                return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }
}
