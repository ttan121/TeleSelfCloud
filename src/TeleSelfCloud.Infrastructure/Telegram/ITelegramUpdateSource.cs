using System.Text.Json.Nodes;

namespace TeleSelfCloud.Infrastructure.Telegram;

public interface ITelegramUpdateSource : ITelegramRequestClient
{
    event EventHandler<JsonObject>? UpdateReceived;
}
