namespace Ordoa.Core.Util;

/// <summary>POSIX path helpers used by every non-Windows filesystem provider.</summary>
public static class PosixPath
{
    public const string Separator = "/";

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        path = path.Replace('\\', '/');
        var absolute = path.StartsWith('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }

        var joined = string.Join('/', stack);
        return absolute ? "/" + joined : joined;
    }

    public static string Combine(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory) || directory == "/")
            return "/" + name.TrimStart('/');
        return directory.TrimEnd('/') + "/" + name.TrimStart('/');
    }

    public static string Parent(string path)
    {
        path = Normalize(path);
        if (path == "/")
            return "/";
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    public static string Name(string path)
    {
        path = Normalize(path);
        if (path == "/")
            return "/";
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    public static bool IsRoot(string path) => Normalize(path) == "/";

    public static IReadOnlyList<string> Segments(string path)
        => Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);

    public static string JoinSegments(params string[] segments)
        => "/" + string.Join('/', segments.Where(s => !string.IsNullOrEmpty(s)));

    public static string RelativeTo(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root).TrimEnd('/');
        if (root.Length == 0)
            return path.TrimStart('/');
        if (path.Equals(root, StringComparison.Ordinal))
            return string.Empty;
        if (path.StartsWith(root + "/", StringComparison.Ordinal))
            return path[(root.Length + 1)..];
        return path.TrimStart('/');
    }

    /// <summary>Percent-encodes a path for use inside a URL, keeping slashes.</summary>
    public static string EscapePath(string path)
        => string.Join('/', Normalize(path).Split('/').Select(Uri.EscapeDataString));

    public static string UnescapePath(string path)
        => Uri.UnescapeDataString(path);

    public static bool LooksLikePath(string value)
        => value.StartsWith('/') || value.StartsWith("~/", StringComparison.Ordinal) || value == "~" || value.StartsWith("./") || value.StartsWith("../");
}
