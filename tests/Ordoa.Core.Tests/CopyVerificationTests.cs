using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class CopyVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ordoa-copy-" + Guid.NewGuid().ToString("N"));

    public CopyVerificationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "source"));
        Directory.CreateDirectory(Path.Combine(_root, "destination"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task VerifiesCopiedFilesWithSha256()
    {
        var sourcePath = Path.Combine(_root, "source", "data.bin");
        var data = new byte[256 * 1024];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(sourcePath, data);

        var settings = new SettingsService();
        settings.Current.CopyVerification = CopyVerificationMode.Sha256;
        using var service = new FileOperationService(new TrashService(), settings: settings);
        var source = new OperationSource(LocalFileSystem.Instance,
            (await LocalFileSystem.Instance.GetEntryAsync(sourcePath))!);

        var operation = service.Copy([source], LocalFileSystem.Instance,
            Path.Combine(_root, "destination"), move: false);

        await WaitForCompletionAsync(operation);

        Assert.Equal(OperationState.Completed, operation.State);
        Assert.Equal(0, operation.FailureCount);
        var destinationPath = Path.Combine(_root, "destination", "data.bin");
        Assert.Equal(data, await File.ReadAllBytesAsync(destinationPath));
        var history = Assert.Single(service.History);
        Assert.Equal(OperationState.Completed, history.State);
        Assert.Equal(1, history.SuccessCount);
        service.ClearHistory();
        Assert.Empty(service.History);
    }

    [Fact]
    public async Task SkipAllLeavesExistingDestinationUntouched()
    {
        var sourcePath = Path.Combine(_root, "source", "same.txt");
        var destinationPath = Path.Combine(_root, "destination", "same.txt");
        await File.WriteAllTextAsync(sourcePath, "new");
        await File.WriteAllTextAsync(destinationPath, "old");

        using var service = new FileOperationService(new TrashService());
        var operation = service.Copy([Source(sourcePath)], LocalFileSystem.Instance,
            Path.Combine(_root, "destination"), move: false, ConflictChoice.SkipAll);

        await WaitForCompletionAsync(operation);

        Assert.Equal(OperationState.Completed, operation.State);
        Assert.Equal("old", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task RenameAllKeepsBothFiles()
    {
        var sourcePath = Path.Combine(_root, "source", "same.txt");
        var destinationPath = Path.Combine(_root, "destination", "same.txt");
        await File.WriteAllTextAsync(sourcePath, "new");
        await File.WriteAllTextAsync(destinationPath, "old");

        using var service = new FileOperationService(new TrashService());
        var operation = service.Copy([Source(sourcePath)], LocalFileSystem.Instance,
            Path.Combine(_root, "destination"), move: false, ConflictChoice.RenameAll);

        await WaitForCompletionAsync(operation);

        Assert.Equal(OperationState.Completed, operation.State);
        Assert.Equal("old", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_root, "destination", "same (1).txt")));
    }

    [Fact]
    public async Task ReplaceAllOverwritesExistingDestination()
    {
        var sourcePath = Path.Combine(_root, "source", "same.txt");
        var destinationPath = Path.Combine(_root, "destination", "same.txt");
        await File.WriteAllTextAsync(sourcePath, "new");
        await File.WriteAllTextAsync(destinationPath, "old");

        using var service = new FileOperationService(new TrashService());
        var operation = service.Copy([Source(sourcePath)], LocalFileSystem.Instance,
            Path.Combine(_root, "destination"), move: false, ConflictChoice.ReplaceAll);

        await WaitForCompletionAsync(operation);

        Assert.Equal(OperationState.Completed, operation.State);
        Assert.Equal("new", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task MissingSourceIsReportedAsAFailedTransfer()
    {
        var sourcePath = Path.Combine(_root, "source", "gone.txt");
        await File.WriteAllTextAsync(sourcePath, "hello");
        var entry = (await LocalFileSystem.Instance.GetEntryAsync(sourcePath))!;
        File.Delete(sourcePath);

        var settings = new SettingsService();
        settings.Current.CopyVerification = CopyVerificationMode.None;
        using var service = new FileOperationService(new TrashService(), settings: settings);

        var operation = service.Copy([new OperationSource(LocalFileSystem.Instance, entry)],
            LocalFileSystem.Instance, Path.Combine(_root, "destination"), move: false);

        await WaitForCompletionAsync(operation);

        Assert.Equal(OperationState.Failed, operation.State);
        Assert.Equal(1, operation.FailureCount);
        Assert.Single(operation.Failures);
        Assert.Equal(1, operation.FailedTransferCount);
        Assert.True(operation.CanRetry);
    }

    private static OperationSource Source(string path)
        => new(LocalFileSystem.Instance, LocalFileSystem.Instance.GetEntryAsync(path).GetAwaiter().GetResult()!);

    private static async Task WaitForCompletionAsync(FileOperationItem item)
    {
        for (var i = 0; i < 500; i++)
        {
            if (item.State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled)
                return;
            await Task.Delay(10);
        }

        Assert.Fail($"Operation did not finish: {item.State}");
    }
}
