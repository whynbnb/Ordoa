using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class FileOperationStateTests
{
    [Fact]
    public void PauseAndResume_RestorePreviousState()
    {
        var item = new FileOperationItem("copy", "destination", OperationKind.Copy)
        {
            State = OperationState.Scanning,
        };

        Assert.True(item.CanPause);
        item.Pause();
        Assert.Equal(OperationState.Paused, item.State);
        Assert.True(item.CanResume);
        Assert.True(item.CanCancel);

        item.Resume();
        Assert.Equal(OperationState.Scanning, item.State);
        Assert.True(item.CanPause);
    }

    [Fact]
    public void Retry_IsOnlyAvailableForFailedOrCancelledItems()
    {
        var item = new FileOperationItem("copy", "destination", OperationKind.Copy)
        {
            State = OperationState.Running,
        };
        Assert.False(item.CanRetry);

        item.State = OperationState.Failed;
        Assert.False(item.CanRetry);
    }
}
