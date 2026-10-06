namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed class VaultCreationWorkflow(TelegramStorageChannelService service, VaultCreationJournal journal, string accountId)
{
    public async Task<TelegramStorageChannelInfo> ExecuteAsync(string? requestedTitle,
        Func<TelegramStorageChannelInfo, CancellationToken, Task> register, CancellationToken token)
    {
        using var lease = journal.AcquireLease();
        await journal.PrepareProtectedArchivesAsync(token);
        var attempt = await journal.LoadAsync(token);
        if (attempt is null)
        {
            if (string.IsNullOrWhiteSpace(requestedTitle) || requestedTitle.Length > 128)
                throw new InvalidOperationException("Enter a vault name of 1 to 128 characters before creating a vault.");
            attempt = new(1, accountId, Guid.NewGuid().ToString("N"), requestedTitle.Trim(), DateTimeOffset.UtcNow, VaultCreationPhase.Prepared, null);
            await journal.SaveAsync(attempt, token);
        }
        TelegramStorageChannelInfo channel;
        if (attempt.Phase == VaultCreationPhase.Prepared)
        {
            token.ThrowIfCancellationRequested();
            attempt = attempt with { Phase = VaultCreationPhase.Dispatched };
            // Durable uncertainty boundary: once written, this request is never automatically resent.
            await journal.SaveAsync(attempt, token);
            channel = await service.CreateAdditionalAsync(attempt.Title, accountId, token, attempt.RequestId);
        }
        else if (attempt.Phase == VaultCreationPhase.Confirmed)
        {
            channel = await service.VerifyExistingAsync(attempt.ChatId!.Value, accountId, token);
            if (channel.CreationRequestId != attempt.RequestId)
                throw new InvalidDataException("The created vault does not match the saved creation request.");
        }
        else
        {
            var candidates = (await service.DiscoverAsync(accountId, token)).Where(v => v.CreationRequestId == attempt.RequestId).ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException(candidates.Length == 0
                    ? "The pending creation has no verified matching vault yet. Retry recovery; the creation request was not resent."
                    : "More than one vault matches the pending creation. All records were kept; verify the channels before recovery.");
            channel = candidates[0];
        }
        attempt = attempt with { Phase = VaultCreationPhase.Confirmed, ChatId = channel.ChatId };
        await journal.SaveAsync(attempt, CancellationToken.None);
        await register(channel, CancellationToken.None);
        await journal.ArchiveRegisteredAsync(attempt.RequestId, CancellationToken.None);
        return channel;
    }
}
