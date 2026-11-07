using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class FileEntryComparerTests
{
    private static FileEntry Entry(string name, string? owner, UnixFileMode? mode) => new()
    {
        Name = name,
        Path = "/tmp/" + name,
        Owner = owner,
        UnixMode = mode,
    };

    [Fact]
    public void SortsByOwner()
    {
        var entries = new[]
        {
            Entry("b", "zoe", null),
            Entry("a", "alice", null),
            Entry("c", "bob", null),
        };
        Array.Sort(entries, new FileEntryComparer(SortColumn.Owner, foldersFirst: false));

        Assert.Equal(["a", "c", "b"], entries.Select(entry => entry.Name));
    }

    [Fact]
    public void SortsByPermissions()
    {
        var entries = new[]
        {
            Entry("write", "a", UnixFileMode.UserRead | UnixFileMode.UserWrite),
            Entry("read", "a", UnixFileMode.UserRead),
            Entry("exec", "a", UnixFileMode.UserRead | UnixFileMode.UserExecute),
        };
        Array.Sort(entries, new FileEntryComparer(SortColumn.Permissions, foldersFirst: false));

        Assert.Equal(["read", "exec", "write"], entries.Select(entry => entry.Name));
    }
}
