using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class TransferJournalTests
{
    [Fact]
    public void Journal_RoundTripsAndRemovesEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-transfers-{Guid.NewGuid():N}.json");
        try
        {
            var journal = new TransferJournal(path);
            var transfer = new PendingTransfer(
                "op-1",
                [new PendingTransferSource("local", "/tmp/source.txt")],
                "local",
                "/tmp/destination",
                Move: false,
                DateTimeOffset.UtcNow);

            journal.Add(transfer);
            var reloaded = new TransferJournal(path);
            var item = Assert.Single(reloaded.Items);
            Assert.Equal("op-1", item.Id);
            Assert.Equal("/tmp/source.txt", item.Sources[0].Path);
            Assert.False(item.Move);

            reloaded.Remove("op-1");
            Assert.Empty(new TransferJournal(path).Items);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Journal_ReplacesDuplicateIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-transfers-{Guid.NewGuid():N}.json");
        try
        {
            var journal = new TransferJournal(path);
            for (var i = 0; i < 2; i++)
            {
                journal.Add(new PendingTransfer(
                    "same",
                    [new PendingTransferSource("local", $"/tmp/source-{i}.txt")],
                    "local",
                    "/tmp/destination",
                    Move: i == 1,
                    DateTimeOffset.UtcNow));
            }

            var item = Assert.Single(journal.Items);
            Assert.True(item.Move);
            Assert.Equal("/tmp/source-1.txt", item.Sources[0].Path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
