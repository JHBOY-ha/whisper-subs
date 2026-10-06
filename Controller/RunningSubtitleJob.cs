using System;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Controller
{
    /// <summary>One execution, not one media identity: a stale button must never cancel a later run.</summary>
    internal sealed class RunningSubtitleJob
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SubtitleWorkItem Work { get; }
        public bool IsQueuedJob { get; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token { get; }
        public volatile bool UserCancelled;
        public Task CancellationCallbacks { get; set; } = Task.CompletedTask;

        public RunningSubtitleJob(SubtitleWorkItem work, CancellationToken parent, bool isQueuedJob)
        {
            Work = work;
            IsQueuedJob = isQueuedJob;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
            Token = Cancellation.Token;
        }

        public void DisposeWhenCallbacksFinish()
        {
            // Do not block holding the dispatcher lock; cancellation callbacks can wait on the job.
            _ = CancellationCallbacks.ContinueWith(done =>
            {
                _ = done.Exception; // observe callback errors without changing the cancellation decision
                Cancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
