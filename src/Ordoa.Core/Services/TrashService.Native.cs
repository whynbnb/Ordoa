using System.Globalization;
using System.Runtime.InteropServices;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed partial class TrashService
{
    private static readonly TrashLocation WindowsLocation =
        new(L10n.T("回收站"), string.Empty, string.Empty, string.Empty);

    private Task<TrashEntry> MoveToWindowsTrashAsync(string path, CancellationToken cancellationToken)
    {
        return RunStaAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException(L10n.T("要删除的项目不存在"), path);

            var from = path + '\0' + '\0';
            var buffer = Marshal.StringToHGlobalUni(from);
            try
            {
                var operation = new ShFileOperation
                {
                    Function = FoDelete,
                    From = buffer,
                    Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
                };
                var result = SHFileOperationW(ref operation);
                if (result != 0 || operation.Aborted)
                    throw new IOException(L10n.T("无法移到系统回收站"));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException(L10n.T("无法移到系统回收站"));

            var entry = FindWindowsEntry(path);
            if (entry is not null)
                return entry;

            return new TrashEntry(
                new FileEntry
                {
                    Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                    Path = path,
                    Kind = EntryKind.File,
                    OriginalPath = path,
                },
                WindowsLocation, path, string.Empty, path, DateTimeOffset.Now);
        }, cancellationToken);
    }

    private Task<IReadOnlyList<TrashEntry>> GetWindowsEntriesAsync(CancellationToken cancellationToken)
    {
        return RunStaAsync<IReadOnlyList<TrashEntry>>(() => ReadWindowsEntries(cancellationToken),
            cancellationToken);
    }

    private static List<TrashEntry> ReadWindowsEntries(CancellationToken cancellationToken)
    {
        var result = new List<TrashEntry>();
        dynamic? shell = null;
        dynamic? bin = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return result;
            shell = Activator.CreateInstance(type);
            bin = shell?.NameSpace(10);
            if (bin is null)
                return result;

            foreach (dynamic item in bin.Items())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var path = (string?)item.Path;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path) && !Directory.Exists(path))
                        continue;

                    var name = (string?)item.Name ?? Path.GetFileName(path);
                    var deletedFrom = item.ExtendedProperty("System.Recycle.DeletedFrom") as string;
                    var original = string.IsNullOrWhiteSpace(deletedFrom)
                        ? name
                        : Path.Combine(deletedFrom, name);
                    var deletedAt = ReadWindowsDate(item, "System.Recycle.DateDeleted");
                    var isDirectory = (bool?)item.IsFolder ?? Directory.Exists(path);
                    long size = 0;
                    if (!isDirectory)
                    {
                        try { size = (long?)item.Size ?? new FileInfo(path).Length; }
                        catch { /* ignore */ }
                    }

                    var file = new FileEntry
                    {
                        Name = name,
                        Path = path,
                        Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
                        Size = size,
                        Modified = deletedAt,
                        DeletedAt = deletedAt,
                        OriginalPath = original,
                        IsHidden = name.StartsWith('.'),
                    };
                    result.Add(new TrashEntry(file, WindowsLocation, path, string.Empty,
                        original, deletedAt));
                }
                catch
                {
                    // Skip entries that the shell cannot describe.
                }
            }
        }
        catch
        {
            // The shell is unavailable in some restricted sessions.
        }
        finally
        {
            if (bin is not null)
                Marshal.FinalReleaseComObject(bin);
            if (shell is not null)
                Marshal.FinalReleaseComObject(shell);
        }

        return result.OrderByDescending(entry => entry.DeletedAt).ToList();
    }

    private static TrashEntry? FindWindowsEntry(string originalPath)
    {
        return RunStaAsync<TrashEntry?>(() =>
        {
            var entries = ReadWindowsEntries(CancellationToken.None);
            return entries.FirstOrDefault(entry =>
                string.Equals(entry.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase));
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private Task<string> RestoreWindowsEntryAsync(string trashPath, CancellationToken cancellationToken)
    {
        return RunStaAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic? shell = null;
            dynamic? bin = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null)
                    throw new NotSupportedException(L10n.T("当前平台不支持回收站"));
                shell = Activator.CreateInstance(type);
                bin = shell?.NameSpace(10);
                if (bin is null)
                    throw new InvalidDataException(L10n.T("无法读取回收站信息文件"));

                foreach (dynamic item in bin.Items())
                {
                    if (!string.Equals((string?)item.Path, trashPath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    var deletedFrom = item.ExtendedProperty("System.Recycle.DeletedFrom") as string;
                    var name = (string?)item.Name ?? Path.GetFileName(trashPath);
                    var original = string.IsNullOrWhiteSpace(deletedFrom)
                        ? name
                        : Path.Combine(deletedFrom, name);

                    foreach (var verb in new[] { "undelete", "restore" })
                    {
                        try
                        {
                            item.InvokeVerb(verb);
                            Thread.Sleep(200);
                            if (!File.Exists(trashPath) && !Directory.Exists(trashPath))
                                return original;
                        }
                        catch
                        {
                            // Try the next verb.
                        }
                    }

                    throw new IOException(L10n.T("恢复失败"));
                }

                throw new FileNotFoundException(L10n.T("要恢复的项目不存在"), trashPath);
            }
            finally
            {
                if (bin is not null)
                    Marshal.FinalReleaseComObject(bin);
                if (shell is not null)
                    Marshal.FinalReleaseComObject(shell);
            }
        }, cancellationToken);
    }

    private static void EmptyWindowsTrash()
    {
        var result = SHEmptyRecycleBinW(IntPtr.Zero, null,
            SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        if (result < 0)
            throw new IOException(L10n.T("清空回收站失败"));
    }

    private static DateTimeOffset ReadWindowsDate(dynamic item, string property)
    {
        try
        {
            var value = item.ExtendedProperty(property);
            if (value is DateTime date)
                return new DateTimeOffset(date);
            if (value is string text &&
                DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal, out var parsed))
                return parsed;
        }
        catch
        {
            // Fall through to the current time.
        }

        return DateTimeOffset.Now;
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Ordoa Shell Trash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public uint Function;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Aborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref ShFileOperation operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBinW(IntPtr window, string? rootPath, uint flags);
}
