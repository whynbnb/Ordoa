using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class BatchRenamePlannerTests
{
    private static FileEntry Item(string name, bool directory = false, DateTimeOffset? modified = null)
        => new()
        {
            Name = name,
            Path = "/tmp/" + name,
            Kind = directory ? EntryKind.Directory : EntryKind.File,
            Modified = modified,
        };

    private static BatchRenamePlan Plan(
        IReadOnlyList<FileEntry> entries,
        BatchRenameOptions options,
        IReadOnlyList<string>? existing = null,
        bool caseSensitive = true,
        FileNameRuleSet rules = FileNameRuleSet.Posix,
        DateTimeOffset? now = null)
        => BatchRenamePlanner.CreatePlan(
            entries,
            existing ?? entries.Select(e => e.Name).ToList(),
            options,
            caseSensitive,
            rules,
            now);

    [Fact]
    public void FindReplace_AndCase_AreAppliedToStem()
    {
        var entries = new[] { Item("Photo 01.JPG") };
        var plan = Plan(entries, new BatchRenameOptions
        {
            FindText = "photo",
            ReplaceText = "Image",
            CaseMode = BatchRenameCaseMode.Upper,
        });

        Assert.Equal("IMAGE 01.JPG", plan.Rows[0].NewName);
        Assert.True(plan.CanApply);
    }

    [Fact]
    public void MatchCase_CanKeepOriginalText()
    {
        var entries = new[] { Item("Photo.JPG") };
        var plan = Plan(entries, new BatchRenameOptions
        {
            FindText = "photo",
            ReplaceText = "Image",
            MatchCase = true,
        });

        Assert.Equal("Photo.JPG", plan.Rows[0].NewName);
        Assert.False(plan.Rows[0].IsChanged);
    }

    [Fact]
    public void Numbering_SupportsPrefixSuffixStepAndPadding()
    {
        var entries = new[] { Item("a.txt"), Item("b.txt") };

        var prefix = Plan(entries, new BatchRenameOptions
        {
            Numbering = true,
            NumberStart = 5,
            NumberStep = 10,
            NumberPadding = 3,
            NumberSeparator = "-",
            NumberPosition = BatchRenameNumberPosition.Prefix,
        });
        Assert.Equal(["005-a.txt", "015-b.txt"], prefix.Rows.Select(r => r.NewName));

        var suffix = Plan(entries, new BatchRenameOptions
        {
            Numbering = true,
            NumberStart = 1,
            NumberPadding = 2,
            NumberSeparator = "_",
            NumberPosition = BatchRenameNumberPosition.Suffix,
        });
        Assert.Equal(["a_01.txt", "b_02.txt"], suffix.Rows.Select(r => r.NewName));
    }

    [Fact]
    public void ExtensionModes_Work()
    {
        var entries = new[] { Item("Photo.JPG") };

        Assert.Equal("Photo.jpg", Plan(entries, new BatchRenameOptions
        {
            ExtensionMode = BatchRenameExtensionMode.Lower,
        }).Rows[0].NewName);

        Assert.Equal("Photo.JPG", Plan(entries, new BatchRenameOptions
        {
            ExtensionMode = BatchRenameExtensionMode.Upper,
        }).Rows[0].NewName);

        Assert.Equal("Photo.png", Plan(entries, new BatchRenameOptions
        {
            ExtensionMode = BatchRenameExtensionMode.Custom,
            CustomExtension = "png",
        }).Rows[0].NewName);

        Assert.Equal("Photo.tar.gz", Plan(entries, new BatchRenameOptions
        {
            ExtensionMode = BatchRenameExtensionMode.Custom,
            CustomExtension = ".tar.gz",
        }).Rows[0].NewName);

        Assert.Equal("Photo", Plan(entries, new BatchRenameOptions
        {
            ExtensionMode = BatchRenameExtensionMode.Remove,
        }).Rows[0].NewName);
    }

    [Fact]
    public void DateTokens_UseCurrentAndEntryTimes()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var modified = new DateTimeOffset(2025, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var entries = new[] { Item("File.txt", modified: modified) };

        var plan = Plan(entries, new BatchRenameOptions
        {
            Prefix = "{date}_",
            Suffix = "_{mtime:yyyyMMdd}",
        }, now: now);

        var expectedDate = now.ToLocalTime().ToString("yyyy-MM-dd");
        var expectedModified = modified.ToLocalTime().ToString("yyyyMMdd");
        Assert.Equal($"{expectedDate}_File_{expectedModified}.txt", plan.Rows[0].NewName);
    }

    [Fact]
    public void UnknownDateToken_IsLeftLiteral()
    {
        var entry = Item("File.txt");
        var result = BatchRenamePlanner.ExpandTokens("{unknown}_", entry, DateTimeOffset.Now);
        Assert.Equal("{unknown}_", result);
    }

    [Fact]
    public void InvalidNames_AreReported()
    {
        var entries = new[] { Item("a.txt"), Item("b.txt"), Item("c.txt") };

        var posix = Plan(entries, new BatchRenameOptions
        {
            FindText = "a",
            ReplaceText = "bad/name",
        });
        Assert.Equal(BatchRenameIssue.InvalidName, posix.Rows[0].Issue);

        var windows = Plan(entries, new BatchRenameOptions
        {
            FindText = "b",
            ReplaceText = "CON",
        }, rules: FileNameRuleSet.Windows);
        Assert.Equal(BatchRenameIssue.InvalidName, windows.Rows[1].Issue);

        var mac = Plan(entries, new BatchRenameOptions
        {
            FindText = "c",
            ReplaceText = "a:b",
        }, rules: FileNameRuleSet.MacOS);
        Assert.Equal(BatchRenameIssue.InvalidName, mac.Rows[2].Issue);
    }

    [Fact]
    public void Posix_AllowsCharactersThatAreInvalidOnWindows()
    {
        var entries = new[] { Item("a.txt") };
        var plan = Plan(entries, new BatchRenameOptions
        {
            FindText = "a",
            ReplaceText = "a:b?",
        });

        Assert.Equal(BatchRenameIssue.None, plan.Rows[0].Issue);
        Assert.Equal("a:b?.txt", plan.Rows[0].NewName);
    }

    [Fact]
    public void DuplicateTargets_AreReportedOnlyForChangedRows()
    {
        var entries = new[] { Item("a.txt"), Item("A.txt") };
        var lower = Plan(entries, new BatchRenameOptions
        {
            FindText = "a",
            ReplaceText = "x",
            MatchCase = false,
        }, caseSensitive: true);

        Assert.All(lower.Rows, row => Assert.Equal(BatchRenameIssue.DuplicateName, row.Issue));
        Assert.False(lower.CanApply);

        var single = Plan(new[] { Item("A.txt") }, new BatchRenameOptions
        {
            CaseMode = BatchRenameCaseMode.Lower,
        }, caseSensitive: true);
        Assert.Equal(BatchRenameIssue.None, single.Rows[0].Issue);
    }

    [Fact]
    public void ExistingName_IsAConflict()
    {
        var entries = new[] { Item("a.txt") };
        var plan = Plan(entries, new BatchRenameOptions
        {
            FindText = "a",
            ReplaceText = "b",
        }, existing: ["a.txt", "b.txt"]);

        Assert.Equal(BatchRenameIssue.NameExists, plan.Rows[0].Issue);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void Directories_IgnoreExtensionRules()
    {
        var entries = new[] { Item("folder.old", directory: true) };
        var plan = Plan(entries, new BatchRenameOptions
        {
            Prefix = "x_",
            ExtensionMode = BatchRenameExtensionMode.Remove,
        });

        Assert.Equal("x_folder.old", plan.Rows[0].NewName);
    }

    [Fact]
    public void Dotfiles_KeepTheirLeadingDot()
    {
        var entries = new[] { Item(".env.local") };
        var plan = Plan(entries, new BatchRenameOptions
        {
            ExtensionMode = BatchRenameExtensionMode.Upper,
        });

        Assert.Equal(".env.LOCAL", plan.Rows[0].NewName);
    }

    [Fact]
    public void InvalidNames_DoNotBlockUnchangedRows()
    {
        var entries = new[] { Item("a.txt") };
        var plan = Plan(entries, new BatchRenameOptions());
        Assert.False(plan.CanApply);
        Assert.Equal(BatchRenameIssue.None, plan.Rows[0].Issue);
    }
}

public sealed class BatchRenameExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoa-rename-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileSystem _fs = LocalFileSystem.Instance;

    public BatchRenameExecutorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private async Task<FileEntry> CreateAsync(string name, string contents)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, contents);
        return Assert.Single(await _fs.ListAsync(_root), e => e.Name == name);
    }

    private static BatchRenamePlan Plan(params (FileEntry Entry, string NewName)[] rows)
        => new(rows.Select(r => new BatchRenameRow(r.Entry, r.NewName, BatchRenameIssue.None, null, true)).ToList());

    [Fact]
    public async Task RenamesFile()
    {
        var entry = await CreateAsync("a.txt", "A");
        var result = await BatchRenameExecutor.ExecuteAsync(_fs, Plan((entry, "c.txt")));

        Assert.Equal(1, result.Renamed);
        Assert.Empty(result.Errors);
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(_root, "c.txt")));
    }

    [Fact]
    public async Task SwapsNamesWithoutOverwritingContents()
    {
        var a = await CreateAsync("a.txt", "A");
        var b = await CreateAsync("b.txt", "B");

        var result = await BatchRenameExecutor.ExecuteAsync(_fs, Plan(
            (a, "b.txt"),
            (b, "a.txt")));

        Assert.Equal(2, result.Renamed);
        Assert.Empty(result.Errors);
        Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(_root, "b.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".ordoa-tmp-*"));
    }

    [Fact]
    public async Task RefusesToOverwriteExistingTarget()
    {
        var a = await CreateAsync("a.txt", "A");
        await CreateAsync("b.txt", "B");

        var result = await BatchRenameExecutor.ExecuteAsync(_fs, Plan((a, "b.txt")));

        Assert.Equal(0, result.Renamed);
        Assert.Equal(1, result.Failed);
        Assert.NotEmpty(result.Errors);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task RefusesDuplicateTargetsInAManualPlan()
    {
        var a = await CreateAsync("a.txt", "A");
        var b = await CreateAsync("b.txt", "B");

        var result = await BatchRenameExecutor.ExecuteAsync(_fs, Plan(
            (a, "c.txt"),
            (b, "c.txt")));

        Assert.Equal(0, result.Renamed);
        Assert.Equal(2, result.Failed);
        Assert.True(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task CaseOnlyRename_WorksOnCaseSensitiveFileSystem()
    {
        if (!_fs.IsCaseSensitive)
            return;

        var entry = await CreateAsync("Photo.TXT", "A");
        var result = await BatchRenameExecutor.ExecuteAsync(_fs, Plan((entry, "photo.txt")));

        Assert.Equal(1, result.Renamed);
        Assert.False(File.Exists(Path.Combine(_root, "Photo.TXT")));
        Assert.True(File.Exists(Path.Combine(_root, "photo.txt")));
    }
}
