using System.Diagnostics;
using System.Runtime.InteropServices;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Util;

public static class LinkService
{
    /// <summary>Create a symbolic link. When relative is true the target is relative to the link folder.</summary>
    public static void CreateSymbolicLink(string targetPath, string linkPath, bool relative)
    {
        var target = relative
            ? Path.GetRelativePath(Path.GetDirectoryName(linkPath)!, targetPath)
            : targetPath;
        if (Directory.Exists(targetPath))
            Directory.CreateSymbolicLink(linkPath, target);
        else
            File.CreateSymbolicLink(linkPath, target);
    }

    public static void CreateHardLink(string targetPath, string linkPath)
    {
        if (Directory.Exists(targetPath))
            throw new NotSupportedException(L10n.T("不能为文件夹创建硬链接"));
        if (!File.Exists(targetPath))
            throw new FileNotFoundException(L10n.T("文件不存在"), targetPath);

        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkW(linkPath, targetPath, IntPtr.Zero))
                throw new IOException(L10n.T("创建硬链接失败"));
            return;
        }

        if (link(targetPath, linkPath) != 0)
        {
            var error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            throw new IOException(error.Message);
        }
    }

    public static string SuggestLinkName(string targetName)
        => Path.GetFileNameWithoutExtension(targetName) + " - 链接" +
           Path.GetExtension(targetName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string linkPath, string targetPath, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldPath, string newPath);
}
