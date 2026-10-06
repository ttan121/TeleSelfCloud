using System.Text.Json.Nodes;

namespace TeleSelfCloud.Infrastructure.Telegram;

public static class VaultMetadataKeyCreationGuard
{
    // Absence must be established from all history, never an incremental checkpoint.
    public static async Task EnsureNoProtectedHistoryAsync(ITelegramRequestClient session, long chatId, CancellationToken token)
    {
        if (chatId == 0) throw new ArgumentOutOfRangeException(nameof(chatId));
        long anchor = 0;
        for (var page = 0; page < 100000; page++)
        {
            token.ThrowIfCancellationRequested();
            var response = await session.ExecuteAsync(new JsonObject
            {
                ["@type"] = "getChatHistory", ["chat_id"] = chatId, ["from_message_id"] = anchor,
                ["offset"] = 0, ["limit"] = 100, ["only_local"] = false
            }, token);
            token.ThrowIfCancellationRequested();
            if (response["@type"]?.ToString() != "messages" || response["messages"] is not JsonArray messages || messages.Count > 100)
                throw new InvalidDataException("Telegram recovery history is malformed. The saved transfer was kept; no new document was sent.");
            if (messages.Count == 0) return;
            long previous = long.MaxValue;
            foreach (var node in messages)
            {
                if (node is not JsonObject message || !long.TryParse(message["id"]?.ToString(), out var id) || id <= 0 || id >= previous ||
                    (anchor != 0 && id > anchor) || !long.TryParse(message["chat_id"]?.ToString(), out var chat) || chat != chatId)
                    throw new InvalidDataException("Telegram recovery history has invalid order, IDs, or storage scope. The saved transfer was kept; no new document was sent.");
                previous = id;
                var caption = message["content"]?["caption"]?["text"]?.ToString() ?? "";
                var fields = caption.Split('|');
                if (fields[0] == "TSC-MANIFEST" && (fields.Length != 4 || fields[1] != "1") ||
                    fields[0] == "TSC-FOLDERS" && (fields.Length != 4 || fields[1] is not "1" and not "2"))
                    throw new InvalidDataException("This vault already contains protected or unsupported metadata. Recover its existing key instead of creating a new key.");
            }
            if (anchor != 0 && messages.Count == 1 && previous == anchor) return;
            if (anchor != 0 && previous >= anchor)
                throw new InvalidDataException("Telegram recovery history did not advance. The saved transfer was kept; no new document was sent.");
            anchor = previous;
        }
        throw new InvalidDataException("The vault history exceeds the key-creation scan limit. No new key was created.");
    }
}
