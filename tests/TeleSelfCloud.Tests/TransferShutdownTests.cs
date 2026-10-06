using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class TransferShutdownTests
{
    [Fact]
    public async Task BoundedRunnerWaitsForAcceptedWorkToSaveCheckpointAfterCancellationAndDoesNotStartWaitingJobs()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<int>();
        var run = new BoundedTransferQueueRunner().RunAsync(new[] { 1, 2, 3 }, 1, async (id, token) =>
        {
            started.TrySetResult();
            await canFinish.Task;
            // Accepted work must be saved even if pause/close arrives meanwhile.
            saved.Add(id);
            token.ThrowIfCancellationRequested();
        }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.False(run.IsCompleted);
        canFinish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(new[] { 1 }, saved);
    }
}
