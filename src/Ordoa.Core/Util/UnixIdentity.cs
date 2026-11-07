using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Ordoa.Core.Util;

/// <summary>
/// Best-effort Unix owner lookup. Linux uses statx for numeric ids and caches
/// names from /etc/passwd and /etc/group; other platforms return null.
/// </summary>
public static class UnixIdentity
{
    private static readonly bool Supported = OperatingSystem.IsLinux();
    private static readonly ConcurrentDictionary<uint, string> UserNames = new();
    private static readonly ConcurrentDictionary<uint, string> GroupNames = new();
    private static readonly Lazy<bool> AccountTablesLoaded = new(LoadAccounts, isThreadSafe: true);
    private static bool _statxUnavailable;

    public static (string? Owner, string? Group) Read(string path)
    {
        if (!Supported || _statxUnavailable)
            return (null, null);

        try
        {
            if (!AccountTablesLoaded.Value)
                return (null, null);

            var result = Statx(AtFdcwd, path, 0, StatxUid | StatxGid, out var stat);
            if (result != 0 || (stat.Mask & (StatxUid | StatxGid)) == 0)
                return (null, null);

            var owner = (stat.Mask & StatxUid) != 0
                ? UserNames.GetValueOrDefault(stat.Uid) ?? stat.Uid.ToString()
                : null;
            var group = (stat.Mask & StatxGid) != 0
                ? GroupNames.GetValueOrDefault(stat.Gid) ?? stat.Gid.ToString()
                : null;
            return (owner, group);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _statxUnavailable = true;
            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool LoadAccounts()
    {
        ReadTable("/etc/passwd", UserNames);
        ReadTable("/etc/group", GroupNames);
        return true;
    }

    private static void ReadTable(string path, ConcurrentDictionary<uint, string> target)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var fields = line.Split(':');
                if (fields.Length < 3 || !uint.TryParse(fields[2], out var id))
                    continue;
                if (!string.IsNullOrWhiteSpace(fields[0]))
                    target.TryAdd(id, fields[0]);
            }
        }
        catch
        {
            // Missing system account files only affect display names.
        }
    }

    private const int AtFdcwd = -100;
    private const uint StatxUid = 0x0000_0008;
    private const uint StatxGid = 0x0000_0010;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirfd, string pathname, int flags, uint mask, out StatxBuffer buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxBuffer
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModifyTime;
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DevMajor;
        public uint DevMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlign;
        public uint DirectIoOffsetAlign;
        public ulong Spare30;
        public ulong Spare31;
        public ulong Spare32;
        public ulong Spare33;
        public ulong Spare34;
        public ulong Spare35;
        public ulong Spare36;
        public ulong Spare37;
        public ulong Spare38;
        public ulong Spare39;
        public ulong Spare3A;
        public ulong Spare3B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }
}
