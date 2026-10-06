using System.Globalization;
using System.Text.Json.Nodes;

namespace TeleSelfCloud.Infrastructure.Telegram;

internal sealed record TelegramDedupDocument(int FileId, string UniqueId)
{
    internal static TelegramDedupDocument Read(JsonObject message, long chat, long id, string owner, int index, long size)
    {
        var content = message["content"]; var file = content?["document"]?["document"];
        var nativeId = Number(file?["id"]); var unique = file?["remote"]?["unique_id"]?.GetValue<string>();
        if (message["@type"]?.GetValue<string>() != "message" || Number(message["chat_id"]) != chat || Number(message["id"]) != id ||
            message["sending_state"] is not null || message["scheduling_state"] is not null ||
            content?["@type"]?.GetValue<string>() != "messageDocument" ||
            content?["caption"]?["text"]?.GetValue<string>() != $"TSC-PART|1|{owner}|{index.ToString(CultureInfo.InvariantCulture)}" ||
            nativeId is null or <= 0 or > int.MaxValue || Number(file?["size"]) != size || string.IsNullOrWhiteSpace(unique) || unique.Length > 1024 ||
            file?["remote"]?["is_uploading_completed"]?.GetValue<bool>() != true)
            throw new InvalidDataException("The duplicate document is not an accepted, complete remote part with stable identity.");
        return new((int)nativeId.Value, unique);
    }
    private static long? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        return value.TryGetValue<int>(out var integer) ? integer : null;
    }
}
