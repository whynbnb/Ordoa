using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Util;

/// <summary>Best-effort extended attribute, ACL and ownership helpers for Unix.</summary>
public static class ExtendedMetadata
{
    public sealed record AttributeValue(string Name, string Value);

    public static IReadOnlyList<AttributeValue> ReadAttributes(string path)
    {
        if (!OperatingSystem.IsLinux())
            return [];

        try
        {
            var size = listxattr(path, null, UIntPtr.Zero);
            if (size <= 0)
                return [];

            var buffer = new byte[(int)size];
            var read = listxattr(path, buffer, (UIntPtr)buffer.Length);
            if (read <= 0)
                return [];

            var names = Encoding.UTF8.GetString(buffer, 0, read)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<AttributeValue>();
            foreach (var name in names)
            {
                var value = ReadAttribute(path, name);
                if (value is not null)
                    result.Add(new AttributeValue(name, value));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    public static void CopyAttributes(string sourcePath, string destinationPath)
    {
        if (!OperatingSystem.IsLinux())
            return;

        foreach (var attribute in ReadAttributes(sourcePath))
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(attribute.Value);
                setxattr(destinationPath, attribute.Name, bytes, (UIntPtr)bytes.Length, 0);
            }
            catch
            {
                // Some namespaces require privileges; preserve what we can.
            }
        }
    }

    public static async Task<string?> ReadAclAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return null;

        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "ls" : "getfacl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (OperatingSystem.IsMacOS())
            {
                start.ArgumentList.Add("-le");
                start.ArgumentList.Add("--");
                start.ArgumentList.Add(path);
            }
            else
            {
                start.ArgumentList.Add("-p");
                start.ArgumentList.Add("--");
                start.ArgumentList.Add(path);
            }

            using var process = Process.Start(start);
            if (process is null)
                return null;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;
            var text = output.Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public static async Task SetOwnershipAsync(string path, string owner, string group,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new NotSupportedException(L10n.T("此平台不支持修改所有者和用户组"));
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(group))
            throw new ArgumentException(L10n.T("请输入所有者和用户组"));

        var start = new ProcessStartInfo("chown")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(owner.Trim() + ":" + group.Trim());
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(L10n.T("无法启动 chown"));
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(error)
                ? L10n.T("修改所有者失败；通常需要管理员权限")
                : error.Trim());
    }

    private static string? ReadAttribute(string path, string name)
    {
        try
        {
            var size = getxattr(path, name, null, UIntPtr.Zero);
            if (size < 0)
                return null;
            if (size == 0)
                return string.Empty;

            var buffer = new byte[(int)size];
            var read = getxattr(path, name, buffer, (UIntPtr)buffer.Length);
            if (read < 0)
                return null;
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int listxattr(string path, byte[]? list, UIntPtr size);

    [DllImport("libc", SetLastError = true)]
    private static extern int getxattr(string path, string name, byte[]? value, UIntPtr size);

    [DllImport("libc", SetLastError = true)]
    private static extern int setxattr(string path, string name, byte[] value, UIntPtr size, int flags);
}
