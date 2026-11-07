using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ordoa.Core.Localization;
using Ordoa.Core.Util;

namespace Ordoa.Core.Models;

public sealed record VolumeInfo(
    string Name,
    string MountPoint,
    string FileSystemType,
    bool IsRemovable,
    bool IsNetwork,
    bool IsSystem,
    long? FreeBytes,
    long? TotalBytes)
{
    /// <summary>Block device such as /dev/sdb1, or the GIO mount path.</summary>
    public string Device { get; init; } = string.Empty;
    public bool IsMounted { get; init; } = true;
    public bool IsGvfs { get; init; }
    public bool CanMount => IsLinux && !IsMounted && Device.StartsWith("/dev/", StringComparison.Ordinal);
    public bool CanUnmount => IsMounted && IsLinux && !IsSystem &&
        (IsGvfs || IsRemovable && !string.IsNullOrEmpty(Device));
    public bool CanEject => IsRemovable && IsMounted && IsLinux && !IsSystem &&
        Device.StartsWith("/dev/", StringComparison.Ordinal);
    public string MountStateText => IsMounted ? string.Empty : L10n.T("未挂载");

    public double? UsedFraction => TotalBytes is > 0 && FreeBytes.HasValue
        ? 1d - (double)FreeBytes.Value / TotalBytes.Value
        : null;

    public string SizeText => IsMounted
        ? TotalBytes.HasValue
            ? L10n.Format("{0} 可用 / {1}", Humanize.Size(FreeBytes ?? 0), Humanize.Size(TotalBytes.Value))
            : FileSystemType
        : L10n.T("未挂载");

    private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
}

public static class VolumeService
{
    private static readonly HashSet<string> RealFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "jfs", "reiserfs", "vfat", "exfat", "ntfs", "ntfs3",
        "fuseblk", "zfs", "iso9660", "udf", "hfs", "hfsplus", "apfs", "overlay", "cifs", "smb3", "smbfs",
        "nfs", "nfs4", "sshfs", "davfs", "davfs2", "afpfs", "9p", "fuse.sshfs", "fuse.rclone",
    };

    public static Task<IReadOnlyList<VolumeInfo>> GetVolumesAsync(bool includeHidden = false,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<VolumeInfo>>(() => GetVolumes(includeHidden), cancellationToken);

    public static IReadOnlyList<VolumeInfo> GetVolumes(bool includeHidden = false)
    {
        try
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? GetLinuxVolumes()
                : GetDriveInfoVolumes(includeHidden);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<VolumeInfo> GetLinuxVolumes()
    {
        const string mountsFile = "/proc/self/mounts";
        if (!File.Exists(mountsFile))
            return GetDriveInfoVolumes(includeHidden: false);

        var volumes = new List<VolumeInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenDevices = new HashSet<string>(StringComparer.Ordinal);
        var home = AppPaths.Home;

        foreach (var line in File.ReadLines(mountsFile))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            var device = UnescapeMount(parts[0]);
            var mountPoint = UnescapeMount(parts[1]);
            var fsType = parts[2];

            if (!IsInteresting(fsType) || !seen.Add(mountPoint) || IsIgnoredMount(mountPoint))
                continue;

            var isRoot = mountPoint == "/";
            var isRemovable = mountPoint.StartsWith("/media/", StringComparison.Ordinal) ||
                              mountPoint.StartsWith("/run/media/", StringComparison.Ordinal) ||
                              device.StartsWith("/dev/sr", StringComparison.Ordinal) ||
                              fsType.Equals("iso9660", StringComparison.OrdinalIgnoreCase) ||
                              fsType.Equals("udf", StringComparison.OrdinalIgnoreCase);
            var isNetwork = fsType is "cifs" or "smb3" or "smbfs" or "nfs" or "nfs4" or "sshfs" or "davfs" or "davfs2" or "afpfs" or "9p" ||
                            fsType.StartsWith("fuse.sshfs", StringComparison.OrdinalIgnoreCase);

            if (mountPoint.StartsWith(home + "/.snap", StringComparison.Ordinal))
                continue;

            // A single btrfs (or bind-mounted) filesystem often appears many times.
            // Keep only the most useful mount for fixed volumes.
            if (!isRemovable && !isNetwork && fsType is not "overlay" && !seenDevices.Add(device))
                continue;

            var label = GetVolumeLabel(mountPoint);
            var name = isRoot
                ? L10n.T("系统")
                : !string.IsNullOrWhiteSpace(label) ? label : Path.GetFileName(mountPoint.TrimEnd('/'));
            if (string.IsNullOrWhiteSpace(name) || name == "/")
                name = device;

            long? free = null, total = null;
            try
            {
                var drive = new DriveInfo(mountPoint);
                if (drive.IsReady)
                {
                    free = drive.AvailableFreeSpace;
                    total = drive.TotalSize;
                }
            }
            catch { /* no permission or pseudo drive */ }

            volumes.Add(new VolumeInfo(name, mountPoint, fsType, isRemovable, isNetwork, isRoot, free, total)
            {
                Device = device,
            });
        }

        AddUnmountedVolumes(volumes, seenDevices);
        AddGvfsVolumes(volumes, seen);

        return volumes
            .OrderBy(v => v.IsSystem ? 0 : v.IsNetwork ? 2 : v.IsRemovable ? 1 : 0)
            .ThenBy(v => v.IsMounted ? 0 : 1)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public sealed record VolumeOperationResult(bool Success, string Message, string? MountPoint = null);

    public static Task<VolumeOperationResult> MountAsync(VolumeInfo volume,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult(new VolumeOperationResult(false, L10n.T("当前平台不支持挂载操作")));
        if (string.IsNullOrWhiteSpace(volume.Device))
            return Task.FromResult(new VolumeOperationResult(false, L10n.T("找不到设备")));

        var tool = FindExecutable("udisksctl");
        if (tool is null)
            return Task.FromResult(new VolumeOperationResult(false, L10n.T("未找到 udisksctl")));

        return RunOperationAsync(tool, ["mount", "-b", volume.Device], cancellationToken);
    }

    public static Task<VolumeOperationResult> UnmountAsync(VolumeInfo volume,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult(new VolumeOperationResult(false, L10n.T("当前平台不支持卸载操作")));

        if (volume.IsGvfs)
        {
            var gio = FindExecutable("gio");
            return gio is null
                ? Task.FromResult(new VolumeOperationResult(false, L10n.T("未找到 gio")))
                : RunOperationAsync(gio, ["mount", "-u", volume.MountPoint], cancellationToken);
        }

        if (volume.Device.StartsWith("/dev/", StringComparison.Ordinal) &&
            FindExecutable("udisksctl") is { } udisks)
            return RunOperationAsync(udisks, ["unmount", "-b", volume.Device], cancellationToken);

        var umount = FindExecutable("umount");
        return umount is null
            ? Task.FromResult(new VolumeOperationResult(false, L10n.T("未找到 umount")))
            : RunOperationAsync(umount, [volume.MountPoint], cancellationToken);
    }

    public static Task<VolumeOperationResult> EjectAsync(VolumeInfo volume,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult(new VolumeOperationResult(false, L10n.T("当前平台不支持弹出操作")));
        if (string.IsNullOrWhiteSpace(volume.Device))
            return Task.FromResult(new VolumeOperationResult(false, L10n.T("找不到设备")));

        var tool = FindExecutable("udisksctl");
        if (tool is not null)
            return RunOperationAsync(tool, ["power-off", "-b", volume.Device], cancellationToken);
        return UnmountAsync(volume, cancellationToken);
    }

    private static async Task<VolumeOperationResult> RunOperationAsync(string executable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
                return new VolumeOperationResult(false, L10n.T("无法启动系统工具"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }

            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            var success = process.ExitCode == 0;
            var message = !string.IsNullOrWhiteSpace(error)
                ? error
                : !string.IsNullOrWhiteSpace(output) ? output : success ? L10n.T("操作完成") : L10n.T("操作失败");
            return new VolumeOperationResult(success, message);
        }
        catch (OperationCanceledException)
        {
            return new VolumeOperationResult(false, L10n.T("操作已取消"));
        }
        catch (Exception ex)
        {
            return new VolumeOperationResult(false, ex.Message);
        }
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }

    private static void AddUnmountedVolumes(List<VolumeInfo> volumes, HashSet<string> seenDevices)
    {
        var lsblk = FindExecutable("lsblk");
        if (lsblk is null)
            return;

        try
        {
            var start = new ProcessStartInfo(lsblk)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-J", "-b", "-o",
                         "NAME,PATH,TYPE,FSTYPE,LABEL,MOUNTPOINT,RM,HOTPLUG,SIZE,TRAN",
                     })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null)
                return;
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return;

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("blockdevices", out var devices))
                return;
            foreach (var device in devices.EnumerateArray())
                AddUnmountedDevice(device, volumes, seenDevices);
        }
        catch
        {
            // lsblk is optional.
        }
    }

    private static void AddUnmountedDevice(JsonElement device, List<VolumeInfo> volumes,
        HashSet<string> seenDevices)
    {
        if (device.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
                AddUnmountedDevice(child, volumes, seenDevices);
        }

        var type = GetString(device, "type");
        var mountPoint = GetString(device, "mountpoint");
        var fsType = GetString(device, "fstype");
        var path = GetString(device, "path");
        if (type is not ("part" or "disk" or "crypt") ||
            !string.IsNullOrWhiteSpace(mountPoint) ||
            string.IsNullOrWhiteSpace(fsType) ||
            string.IsNullOrWhiteSpace(path))
            return;

        var removable = GetBool(device, "rm") || GetBool(device, "hotplug") ||
                        string.Equals(GetString(device, "tran"), "usb", StringComparison.OrdinalIgnoreCase);
        if (!removable || volumes.Any(volume => string.Equals(volume.Device, path, StringComparison.Ordinal)))
            return;

        seenDevices.Add(path);
        var label = GetString(device, "label");
        var name = string.IsNullOrWhiteSpace(label) ? GetString(device, "name") : label;
        long? total = device.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes)
            ? bytes
            : null;
        volumes.Add(new VolumeInfo(name ?? path, string.Empty, fsType, true, false, false, null, total)
        {
            Device = path,
            IsMounted = false,
        });
    }

    private static void AddGvfsVolumes(List<VolumeInfo> volumes, HashSet<string> seenMounts)
    {
        try
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrWhiteSpace(runtime))
                runtime = "/run/user/" + GetUid().ToString();
            var root = Path.Combine(runtime, "gvfs");
            if (!Directory.Exists(root))
                return;

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!seenMounts.Add(directory))
                    continue;

                var raw = Path.GetFileName(directory);
                var isNetwork = raw.StartsWith("smb-share:", StringComparison.OrdinalIgnoreCase) ||
                                raw.StartsWith("sftp:", StringComparison.OrdinalIgnoreCase) ||
                                raw.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase) ||
                                raw.StartsWith("dav:", StringComparison.OrdinalIgnoreCase) ||
                                raw.StartsWith("davs:", StringComparison.OrdinalIgnoreCase) ||
                                raw.StartsWith("nfs:", StringComparison.OrdinalIgnoreCase);
                var isRemovable = raw.StartsWith("mtp:", StringComparison.OrdinalIgnoreCase) ||
                                  raw.StartsWith("gphoto2:", StringComparison.OrdinalIgnoreCase) ||
                                  raw.StartsWith("afc:", StringComparison.OrdinalIgnoreCase);
                volumes.Add(new VolumeInfo(DescribeGvfs(raw), directory, "gvfs", isRemovable,
                    isNetwork, false, null, null)
                {
                    Device = directory,
                    IsGvfs = true,
                });
            }
        }
        catch
        {
            // GVfs is not running or the runtime directory is restricted.
        }
    }

    private static string DescribeGvfs(string raw)
    {
        try
        {
            var separator = raw.IndexOf(':');
            var kind = separator > 0 ? raw[..separator] : raw;
            var details = separator > 0 ? raw[(separator + 1)..] : string.Empty;
            var values = details.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

            string? value = null;
            if (values.TryGetValue("host", out var host))
                value = host;
            else if (values.TryGetValue("server", out var server))
                value = values.TryGetValue("share", out var share) ? $"{server} / {share}" : server;
            else if (values.TryGetValue("volume", out var volume))
                value = volume;

            value = (value ?? kind).Replace('_', ' ').Trim();
            return string.IsNullOrWhiteSpace(value) ? raw : value;
        }
        catch
        {
            return raw;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0;
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    private static bool IsIgnoredMount(string mountPoint)
    {
        if (mountPoint == "/")
            return false;
        ReadOnlySpan<string> ignored =
        [
            "/proc", "/sys", "/dev", "/run", "/var", "/tmp", "/snap", "/efi", "/boot", "/mnt/wsl",
        ];
        foreach (var prefix in ignored)
        {
            if (mountPoint.Equals(prefix, StringComparison.Ordinal) ||
                mountPoint.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                if (prefix == "/run" && mountPoint.StartsWith("/run/media/", StringComparison.Ordinal))
                    return false;
                return true;
            }
        }
        return false;
    }

    private static bool IsInteresting(string fsType)
        => RealFileSystems.Contains(fsType) ||
           fsType.StartsWith("fuse.", StringComparison.OrdinalIgnoreCase);

    private static string GetVolumeLabel(string mountPoint)
    {
        try
        {
            var drive = new DriveInfo(mountPoint);
            return drive.IsReady ? drive.VolumeLabel : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string UnescapeMount(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length &&
                value[i + 1] is >= '0' and <= '7' &&
                value[i + 2] is >= '0' and <= '7' &&
                value[i + 3] is >= '0' and <= '7')
            {
                var code = (value[i + 1] - '0') * 64 + (value[i + 2] - '0') * 8 + (value[i + 3] - '0');
                builder.Append((char)code);
                i += 3;
            }
            else
            {
                builder.Append(value[i]);
            }
        }
        return builder.ToString();
    }

    private static IReadOnlyList<VolumeInfo> GetDriveInfoVolumes(bool includeHidden)
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;
                var mountPoint = drive.RootDirectory.FullName;

                // macOS exposes many internal APFS volumes through DriveInfo.
                // Only "/" and user-visible mounts under "/Volumes" belong in
                // the sidebar unless the user explicitly asks for all volumes.
                if (OperatingSystem.IsMacOS() && !includeHidden && !IsVisibleMacVolume(mountPoint))
                    continue;

                var isNetwork = drive.DriveType == DriveType.Network;
                var isRemovable = drive.DriveType is DriveType.Removable or DriveType.CDRom;
                var name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? (drive.Name.TrimEnd('\\', '/') is { Length: > 0 } n ? n : drive.Name)
                    : drive.VolumeLabel;
                var isSystem = OperatingSystem.IsMacOS()
                    ? mountPoint == "/"
                    : drive.DriveType == DriveType.Fixed && mountPoint.Length <= 4;

                result.Add(new VolumeInfo(name, mountPoint, drive.DriveFormat, isRemovable, isNetwork,
                    isSystem, drive.AvailableFreeSpace, drive.TotalSize));
            }
            catch
            {
                // Ignore drives that disappear while enumerating.
            }
        }

        return result
            .OrderByDescending(v => v.IsSystem)
            .ThenBy(v => v.IsRemovable)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsVisibleMacVolume(string mountPoint)
    {
        if (mountPoint == "/")
            return true;
        if (!mountPoint.StartsWith("/Volumes/", StringComparison.Ordinal))
            return false;

        // Time Machine's private snapshot volume is not a useful disk entry.
        var name = Path.GetFileName(mountPoint.TrimEnd('/'));
        return !name.Equals(".timemachine", StringComparison.OrdinalIgnoreCase);
    }
}
