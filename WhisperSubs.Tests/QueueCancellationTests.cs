using MediaBrowser.Controller.Entities;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

public class QueueCancellationTests
{
    private static Video Item() => new() { Id = Guid.NewGuid(), Name = "Test film" };
    private static void Save(List<QueueEntry> pending, List<QueueEntry> running) { }

    [Fact]
    public void CancelOne_DoesNotCancelAnotherLanguageOrTranslation()
    {
        var queue = new SubtitleQueueService();
        var film = Item();
        queue.Enqueue(film, "auto");
        queue.Enqueue(film, "en");
        queue.Enqueue(film, "auto", target: "en");
        var key = SubtitleQueueService.IdentityKey(film.Id, "auto");
        Assert.Equal(1, queue.CancelPending(key, Save));
        Assert.Equal(2, queue.PriorityCount);
        Assert.DoesNotContain(queue.PendingItems(), x => x.Key == key);
        Assert.Equal(0, queue.CancelPending(key, Save));
        Assert.True(queue.Enqueue(film, "auto")); // deliberate re-request is allowed
    }

    [Fact]
    public void CancelAll_PersistsRunningLeaseAndDoesNotCancelIt()
    {
        var queue = new SubtitleQueueService();
        var running = Item();
        queue.Enqueue(running, "auto", force: true);
        Assert.True(queue.TryDequeuePriority(out _));
        for (var i = 0; i < 205; i++) queue.Enqueue(Item(), "auto");
        string? saved = null;
        Assert.Equal(205, queue.CancelPending(null, (pending, inflight) =>
            saved = SubtitleQueueService.SerializeQueueFile(pending, inflight)));
        var (pending, inflight) = SubtitleQueueService.ParseQueueFile(saved!);
        Assert.Empty(pending);
        Assert.Single(inflight);
        Assert.Equal(running.Id.ToString("N"), inflight[0].ItemId);
        Assert.True(inflight[0].Force);
        Assert.Equal(0, queue.PriorityCount);
        Assert.False(queue.Enqueue(running, "auto")); // running identity still reserved
        Assert.Equal(0, queue.CancelPending(SubtitleQueueService.IdentityKey(running.Id, "auto"), Save));
    }

    [Fact]
    public void SaveFailure_LeavesOrderAndAllJobsIntact()
    {
        var queue = new SubtitleQueueService();
        queue.Enqueue(Item(), "auto", PriorityTier.Medium);
        queue.Enqueue(Item(), "ja", PriorityTier.High, force: true);
        var before = queue.PendingItems().ToArray();
        Assert.Throws<IOException>(() => queue.CancelPending(null, (_, _) => throw new IOException("disk full")));
        Assert.Equal(before, queue.PendingItems());
    }

    [Fact]
    public async Task CancelAndDispatch_AJobCannotBeBothCancelledAndStarted()
    {
        for (var i = 0; i < 100; i++)
        {
            var queue = new SubtitleQueueService();
            var item = Item();
            queue.Enqueue(item, "auto");
            var cancel = Task.Run(() => queue.CancelPending(SubtitleQueueService.IdentityKey(item.Id, "auto"), Save));
            var dispatch = Task.Run(() => queue.TryDequeuePriority(out _));
            await Task.WhenAll(cancel, dispatch);
            Assert.Equal(1, await cancel + (await dispatch ? 1 : 0));
            Assert.Equal(0, queue.PriorityCount);
        }
    }

    [Fact]
    public void CancelEmptyQueue_DoesNotAttemptSave()
    {
        Assert.Equal(0, new SubtitleQueueService().CancelPending(null, (_, _) => throw new Exception("unexpected save")));
    }
}
