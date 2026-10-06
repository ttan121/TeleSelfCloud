using System.Text.Json.Nodes;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Small request surface used by Telegram services and fake-driven integration tests.</summary>
public interface ITelegramRequestClient
{
    Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default);
}
