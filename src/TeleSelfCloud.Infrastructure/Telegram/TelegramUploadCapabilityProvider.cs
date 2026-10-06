using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed class TelegramUploadCapabilityProvider(TelegramAuthSession session) : IUploadCapabilityProvider
{
    // The supported TDLib schema exposes Premium status, not an account-specific byte maximum.
    public const long ConservativePerFileLimitBytes = 2_000_000_000;
    private UploadCapability? _current;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var user = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMe" }, cancellationToken);
        _current = FromCurrentUser(user, DateTimeOffset.UtcNow);
    }

    public Task<UploadCapability?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_current);
    }

    public static UploadCapability FromCurrentUser(JsonObject user, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user["@type"]?.GetValue<string>() != "user" || user["id"] is null)
            throw new InvalidDataException("TDLib did not return the authenticated user; upload capability remains unknown.");

        var accountId = user["id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new UploadCapability(
            accountId,
            ConservativePerFileLimitBytes,
            observedAt,
            "Conservative 2,000,000,000-byte ceiling for every account; current Telegram Basic FAQ, time-sensitive, TDLib 1.8.67 exposes Premium status but no per-account byte maximum");
    }
}
