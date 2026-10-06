using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Retries transient transfer failures a small, bounded number of times.</summary>
public sealed class TransferRetryPolicy
{
    public const int DefaultMaxAttempts = 3;
    public static readonly TimeSpan MaxAutomaticWait = TimeSpan.FromMinutes(5);
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly int _maxAttempts;

    public TransferRetryPolicy(
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _delayAsync = delayAsync ?? Task.Delay;
        _maxAttempts = maxAttempts;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        Func<int, TimeSpan, Task>? retrying = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception ex) when (attempt < _maxAttempts && GetDelay(ex, attempt) is not null)
            {
                var delay = GetDelay(ex, attempt)!.Value;
                if (retrying is not null) await retrying(attempt, delay);
                await _delayAsync(delay, cancellationToken);
            }
        }
    }

    public static TimeSpan? GetDelay(Exception exception, int failedAttempt)
    {
        if (failedAttempt < 1) throw new ArgumentOutOfRangeException(nameof(failedAttempt));
        var telegram = exception as TelegramRequestException;
        var transient = telegram is not null
            ? telegram.IsTransient
            : exception is IOException and not FileNotFoundException;
        if (!transient) return null;

        if (telegram?.RetryAfter is { } retryAfter)
            return retryAfter <= MaxAutomaticWait ? retryAfter : null;
        return TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(failedAttempt - 1, 4)));
    }
}
