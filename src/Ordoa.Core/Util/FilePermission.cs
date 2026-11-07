namespace Ordoa.Core.Util;

public static class FilePermission
{
    /// <summary>Formats a Unix mode as the familiar rwxr-xr-x string.</summary>
    public static string Format(UnixFileMode? mode)
    {
        if (mode is not { } value)
            return "—";

        Span<char> text = stackalloc char[9];
        Write(value, UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute, text[..3]);
        Write(value, UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute, text.Slice(3, 3));
        Write(value, UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute, text.Slice(6, 3));
        return new string(text);
    }

    private static void Write(UnixFileMode mode, UnixFileMode read, UnixFileMode write,
        UnixFileMode execute, Span<char> target)
    {
        target[0] = mode.HasFlag(read) ? 'r' : '-';
        target[1] = mode.HasFlag(write) ? 'w' : '-';
        target[2] = mode.HasFlag(execute) ? 'x' : '-';
    }
}
