using System.Diagnostics;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class RunningCancellationTests
{
    private static void Save(List<QueueEntry> pending, List<QueueEntry> running) { }
    private static RunningSubtitleJob Start(SubtitleQueueService queue, CancellationToken parent = default)
    {
        queue.Enqueue(new Video { Id = Guid.NewGuid(), Name = "Synthetic test" }, "auto");
        Assert.True(queue.TryDequeuePriority(out var work));
        return queue.BeginRunning(work!, parent);
    }

    [Fact]
    public async Task CancellingOneExecution_SignalsOnlyItsToken_AndKeepsReservationUntilExit()
    {
        var queue = new SubtitleQueueService();
        var first = Start(queue); var other = Start(queue);
        try
        {
            var waitingForCancellation = Task.Delay(Timeout.Infinite, first.Token);
            Assert.True(queue.CancelRunning(first.Id, Save));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingForCancellation);
            Assert.False(other.Token.IsCancellationRequested);
            Assert.False(queue.Enqueue(first.Work.Item, "auto"));
            Assert.Contains(queue.RunningItems(), j => j.Id == first.Id && j.Cancelling);
            Assert.True(queue.CancelRunning(first.Id, (_, _) => throw new Exception("second save")));
            Assert.False(queue.RetryOrRelease(first.Work, 3));
            queue.EndRunning(first);
            Assert.True(queue.Enqueue(first.Work.Item, "auto"));
        }
        finally { queue.EndRunning(first); queue.EndRunning(other); }
    }

    [Fact]
    public void CancelledLease_IsNotRestoredBySubsequentPendingCancellation()
    {
        var queue = new SubtitleQueueService(); var first = Start(queue); var other = Start(queue);
        try
        {
            List<QueueEntry>? persisted = null;
            queue.CancelRunning(first.Id, (_, running) => persisted = running);
            Assert.Single(persisted!);
            queue.Enqueue(new Video { Id = Guid.NewGuid(), Name = "Waiting" }, "en");
            queue.CancelPending(null, (_, running) => persisted = running);
            Assert.Single(persisted!);
            Assert.Equal(other.Work.Item.Id.ToString("N"), persisted![0].ItemId);
            queue.RequeueWithoutRetry(first.Work); // launch/worker failure must not resurrect it either
            Assert.Equal(0, queue.PriorityCount);
        }
        finally { queue.EndRunning(first); queue.EndRunning(other); }
    }

    [Fact]
    public void FailedSave_DoesNotStopExecutionOrChangeItsStatus()
    {
        var queue = new SubtitleQueueService(); var job = Start(queue);
        try
        {
            Assert.Throws<IOException>(() => queue.CancelRunning(job.Id, (_, _) => throw new IOException("disk full")));
            Assert.False(job.Token.IsCancellationRequested);
            Assert.False(job.UserCancelled);
            Assert.False(queue.RunningItems().Single().Cancelling);
            Assert.True(queue.RetryOrRelease(job.Work, 3));
        }
        finally { queue.EndRunning(job); }
    }

    [Fact]
    public async Task ParentShutdown_StillRetries_AndOldExecutionCannotCancelNewAttempt()
    {
        using var parent = new CancellationTokenSource();
        var queue = new SubtitleQueueService(); var first = Start(queue, parent.Token);
        await parent.CancelAsync();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(first.UserCancelled);
        Assert.True(queue.RetryOrRelease(first.Work, 3));
        Assert.True(queue.TryDequeuePriority(out var retry));
        var next = queue.BeginRunning(retry!, CancellationToken.None);
        try
        {
            Assert.False(queue.CancelRunning(first.Id, Save));
            Assert.False(next.Token.IsCancellationRequested);
            queue.EndRunning(first);
            Assert.False(queue.CancelRunning(first.Id, Save));
            Assert.True(queue.CancelRunning(next.Id, Save));
        }
        finally { queue.EndRunning(first); queue.EndRunning(next); }
    }

    [Fact]
    public void ScheduledExecution_CanBeCancelledWithoutRemovingAnUnrelatedManualLease()
    {
        var queue = new SubtitleQueueService(); var manual = Start(queue);
        var sweep = queue.BeginRunning(manual.Work, CancellationToken.None, isQueuedJob: false);
        try
        {
            Assert.True(queue.CancelRunning(sweep.Id, (_, running) => Assert.Single(running)));
            Assert.True(sweep.UserCancelled);
            Assert.False(manual.Token.IsCancellationRequested);
        }
        finally { queue.EndRunning(sweep); queue.EndRunning(manual); }
    }

    [Fact]
    public async Task CompletionRacingCancellation_IsSafe_AndNeverTargetsAnotherExecution()
    {
        for (var i = 0; i < 50; i++)
        {
            var queue = new SubtitleQueueService(); var job = Start(queue);
            await Task.WhenAll(Task.Run(() => queue.CancelRunning(job.Id, Save)),
                Task.Run(() => { queue.Release(SubtitleQueueService.IdentityKey(job.Work)); queue.EndRunning(job); }));
            Assert.Empty(queue.RunningItems());
            Assert.Equal(0, queue.PriorityCount);
            Assert.False(queue.CancelRunning(job.Id, Save));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalEngineProcess_StopsOnCancel_AndPartialOutputIsKept(bool writePartial)
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "whispersubs-cancel-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var pidFile = Path.Combine(directory, "pid");
        var srtFile = Path.Combine(directory, "synthetic.srt");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var queue = new SubtitleQueueService(); var job = Start(queue, deadline.Token);
        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("if [ \"$3\" = yes ]; then printf 'synthetic partial subtitle' > \"$2\"; fi; echo $$ > \"$1\"; exec sleep 30");
        info.ArgumentList.Add("test"); info.ArgumentList.Add(pidFile); info.ArgumentList.Add(srtFile);
        info.ArgumentList.Add(writePartial ? "yes" : "no");
        var task = SrtProcessRunner.RunAsync(NullLogger.Instance, info, "Synthetic", srtFile, null,
            (code, error) => new Exception("Synthetic process failed"), _ => null, job.Token);
        try
        {
            while (!File.Exists(pidFile) || string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(pidFile, deadline.Token)))
                await Task.Delay(20, deadline.Token);
            using var process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile, deadline.Token)));
            Assert.True(queue.CancelRunning(job.Id, Save));
            if (writePartial) Assert.Equal("synthetic partial subtitle", await task.WaitAsync(TimeSpan.FromSeconds(5)));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.HasExited);
            Assert.False(queue.RetryOrRelease(job.Work, 3));
        }
        finally
        {
            await deadline.CancelAsync();
            try { await task; } catch { }
            queue.EndRunning(job);
            Directory.Delete(directory, recursive: true); // synthetic fixtures only
        }
    }
}
