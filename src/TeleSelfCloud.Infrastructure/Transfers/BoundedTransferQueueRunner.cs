namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Runs queued work with a small explicit concurrency bound.</summary>
public sealed class BoundedTransferQueueRunner
{
    public const int MaxSupportedConcurrency = 3;

    public async Task RunAsync<T>(
        IReadOnlyCollection<T> items,
        int maxConcurrency,
        Func<T, CancellationToken, Task> transferAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(transferAsync);
        if (maxConcurrency is < 1 or > MaxSupportedConcurrency)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), $"Concurrency must be from 1 to {MaxSupportedConcurrency}.");

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var transfers = items.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try { await transferAsync(item, cancellationToken); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(transfers);
    }
}
