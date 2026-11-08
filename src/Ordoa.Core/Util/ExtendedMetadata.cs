using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Util;

/// <summary>Best-effort extended attribute, ACL and ownership helpers for Unix.</summary>
public static class ExtendedMetadata
{
    public sealed record AttributeValue(string Name, string Value);

    public static bool CanEditAttributes => OperatingSystem.IsLinux();
    public static bool CanEditAcl => OperatingSystem.IsLinux();

    public static IReadOnlyList<AttributeValue> ReadAttributes(string path)
    {
        if (OperatingSystem.IsMacOS())
            return ReadMacAttributes(path);
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

    /// <summary>Replaces all user-visible extended attributes with the supplied set.</summary>
    public static void WriteAttributes(string path, IEnumerable<AttributeValue> attributes)
    {
        if (!CanEditAttributes)
            throw new NotSupportedException(L10n.T("此平台不支持编辑扩展属性"));

        var desired = attributes
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Name))
            .GroupBy(attribute => attribute.Name.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var existing = ReadAttributes(path).Select(attribute => attribute.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in existing)
        {
            if (!desired.ContainsKey(name))
                RemoveAttribute(path, name);
        }

        foreach (var pair in desired)
            SetAttribute(path, pair.Key, pair.Value);
    }

    public static void SetAttribute(string path, string name, string value)
    {
        if (!CanEditAttributes)
            throw new NotSupportedException(L10n.T("此平台不支持编辑扩展属性"));

        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (setxattr(path, name, bytes, (UIntPtr)bytes.Length, 0) != 0)
            throw new IOException(L10n.T("写入扩展属性失败"));
    }

    public static void RemoveAttribute(string path, string name)
    {
        if (!CanEditAttributes)
            throw new NotSupportedException(L10n.T("此平台不支持编辑扩展属性"));
        if (removexattr(path, name) != 0)
            throw new IOException(L10n.T("删除扩展属性失败"));
    }

    /// <summary>Applies getfacl-style text through setfacl on Linux.</summary>
    public static async Task SetAclAsync(string path, string aclText,
        CancellationToken cancellationToken = default)
    {
        if (!CanEditAcl)
            throw new NotSupportedException(L10n.T("此平台不支持编辑 ACL"));

        var start = new ProcessStartInfo("setfacl")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--set-file=-");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(L10n.T("无法启动 setfacl"));
        await process.StandardInput.WriteAsync(aclText.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException(string.IsNullOrWhiteSpace(error)
                ? L10n.T("修改 ACL 失败")
                : error.Trim());
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

    private static IReadOnlyList<AttributeValue> ReadMacAttributes(string path)
    {
        try
        {
            var names = RunForOutput("/usr/bin/xattr", [path])?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? [];
            var result = new List<AttributeValue>();
            foreach (var name in names)
            {
                var hex = RunForOutput("/usr/bin/xattr", ["-px", name, path]);
                var value = DecodeMacValue(hex);
                result.Add(new AttributeValue(name, value));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private static string DecodeMacValue(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return string.Empty;

        try
        {
            var bytes = Convert.FromHexString(hex.Replace(" ", string.Empty).Trim());
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return hex.Trim();
        }
    }

    private static string? RunForOutput(string executable, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
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

    [DllImport("libc", SetLastError = true)]
    private static extern int removexattr(string path, string name);
}
