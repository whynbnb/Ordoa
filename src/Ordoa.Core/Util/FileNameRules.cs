using System.Runtime.InteropServices;
using System.Text;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Util;

/// <summary>Filename restriction set used by the local host or a remote provider.</summary>
public enum FileNameRuleSet
{
    Posix,
    MacOS,
    Windows,
}

public static class FileNameRules
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static FileNameRuleSet Detect(IFileSystem fileSystem)
    {
        if (string.Equals(fileSystem.Scheme, "smb", StringComparison.OrdinalIgnoreCase))
            return FileNameRuleSet.Windows;

        if (!fileSystem.IsLocal)
            return FileNameRuleSet.Posix;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return FileNameRuleSet.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return FileNameRuleSet.MacOS;
        return FileNameRuleSet.Posix;
    }

    /// <summary>Returns a localized reason when <paramref name="name"/> is not valid, otherwise null.</summary>
    public static string? Validate(string name, FileNameRuleSet rules)
    {
        if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            return L10n.T("名称不能为空");

        if (name is "." or "..")
            return L10n.T("名称不能是 . 或 ..");

        if (name.Length > 255)
            return L10n.T("名称过长");

        if (rules is FileNameRuleSet.Posix or FileNameRuleSet.MacOS &&
            Encoding.UTF8.GetByteCount(name) > 255)
        {
            return L10n.T("名称过长");
        }

        foreach (var ch in name)
        {
            if (ch == '\0' || char.IsControl(ch))
                return L10n.T("名称不能包含控制字符");

            if (ch == '/')
                return L10n.T("名称不能包含 /");

            if (ch == '\\' && rules == FileNameRuleSet.Windows)
                return L10n.T("名称不能包含 \\");
        }

        if (rules == FileNameRuleSet.MacOS && name.Contains(':'))
            return L10n.T("名称不能包含 :");

        if (rules == FileNameRuleSet.Windows)
        {
            if (name.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
                return L10n.T("名称不能包含以下字符：< > : \" | ? *");

            if (name.EndsWith(' ') || name.EndsWith('.'))
                return L10n.T("名称不能以空格或句点结尾");

            var stem = name.Split('.')[0];
            if (WindowsReservedNames.Contains(stem))
                return L10n.T("名称不能使用 Windows 保留名称");
        }

        return null;
    }
}
