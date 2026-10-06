using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Ensures queue setup failures do not strand a task in Running state.</summary>
public sealed class TransferQueueStartGuard(ITransferQueueStore queueStore)
{
    public async Task StartAsync(string taskId, Func<Task> refreshAsync)
    {
        ArgumentNullException.ThrowIfNull(refreshAsync);
        if (!await TryStartAsync(taskId, refreshAsync))
            throw new InvalidOperationException("The transfer is no longer waiting to start.");
    }

    private async Task<bool> TryStartAsync(string taskId, Func<Task> refreshAsync)
    {
        var started = queueStore is IAtomicTransferQueueStartStore atomic
            ? await atomic.TryStartAsync(taskId, CancellationToken.None)
            : await SetRunningAsync(taskId);
        if (!started) return false;
        try
        {
            await refreshAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            await queueStore.SetStateAsync(taskId, TransferQueueState.Paused, null, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await queueStore.SetStateAsync(taskId, TransferQueueState.Failed, SafeFailure.Describe(ex), CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> SetRunningAsync(string taskId)
    {
        await queueStore.SetStateAsync(taskId, TransferQueueState.Running, null, CancellationToken.None);
        return true;
    }

    public async Task RunAsync(string taskId, Func<Task> refreshAsync, Func<Task> transferAsync)
    {
        ArgumentNullException.ThrowIfNull(transferAsync);
        if (!await TryStartAsync(taskId, refreshAsync)) return;
        try
        {
            await transferAsync();
            await queueStore.SetStateAsync(taskId, TransferQueueState.Completed, null, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await queueStore.SetStateAsync(taskId, TransferQueueState.Paused, null, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await queueStore.SetStateAsync(taskId, TransferQueueState.Failed, SafeFailure.Describe(ex), CancellationToken.None);
            throw;
        }
    }
}
