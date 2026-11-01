using Ordoa.Core.Localization;
using System.Runtime.InteropServices;
using System.Text;
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
    public double? UsedFraction => TotalBytes is > 0 && FreeBytes.HasValue
        ? 1d - (double)FreeBytes.Value / TotalBytes.Value
        : null;

    public string SizeText => TotalBytes.HasValue
        ? L10n.Format("{0} 可用 / {1}", Humanize.Size(FreeBytes ?? 0), Humanize.Size(TotalBytes.Value))
        : FileSystemType;
}

public static class VolumeService
{
    private static readonly HashSet<string> RealFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "jfs", "reiserfs", "vfat", "exfat", "ntfs", "ntfs3",
        "fuseblk", "zfs", "iso9660", "udf", "hfs", "hfsplus", "apfs", "overlay", "cifs", "smb3", "smbfs",
        "nfs", "nfs4", "sshfs", "davfs", "davfs2", "afpfs", "9p", "fuse.sshfs", "fuse.rclone",
    };

    public static Task<IReadOnlyList<VolumeInfo>> GetVolumesAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<VolumeInfo>>(() => GetVolumes(), cancellationToken);

    public static IReadOnlyList<VolumeInfo> GetVolumes()
    {
        try
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? GetLinuxVolumes()
                : GetDriveInfoVolumes();
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
            return GetDriveInfoVolumes();

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

            volumes.Add(new VolumeInfo(name, mountPoint, fsType, isRemovable, isNetwork, isRoot, free, total));
        }

        return volumes
            .OrderBy(v => v.IsSystem ? 0 : v.IsNetwork ? 2 : v.IsRemovable ? 1 : 0)
            .ThenBy(v => v.MountPoint, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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

    private static IReadOnlyList<VolumeInfo> GetDriveInfoVolumes()
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;
                var mountPoint = drive.RootDirectory.FullName;
                var isNetwork = drive.DriveType == DriveType.Network;
                var isRemovable = drive.DriveType is DriveType.Removable or DriveType.CDRom;
                var name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? (drive.Name.TrimEnd('\\', '/') is { Length: > 0 } n ? n : drive.Name)
                    : drive.VolumeLabel;

                result.Add(new VolumeInfo(name, mountPoint, drive.DriveFormat, isRemovable, isNetwork,
                    drive.DriveType == DriveType.Fixed && mountPoint.Length <= 4,
                    drive.AvailableFreeSpace, drive.TotalSize));
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
}
