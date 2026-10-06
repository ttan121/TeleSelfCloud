using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed record TelegramStorageChannelInfo(long ChatId, string AccountId, string Title,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CreationRequestId = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => $"{Title} ({ChatId})";
}

public sealed class TelegramStorageChannelService(
    ITelegramRequestClient session,
    string settingsPath,
    Func<LocalRecordCipher>? settingsCipherFactory = null)
{
    private const string StorageDescription = "Private file storage for TeleSelfCloud. Do not add other members.";
    private const int MaxSettingsBytes = 16 * 1024;
    private const int MaxStoredSettingsBytes = MaxSettingsBytes + 76;

    public async Task<TelegramStorageChannelInfo> VerifyExistingAsync(long chatId, string expectedAccountId, CancellationToken token)
    {
        var user = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMe" }, token);
        if (user["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture) != expectedAccountId)
            throw new InvalidOperationException("The configured storage channel belongs to a different Telegram account.");
        JsonObject chat;
        try { chat = await session.ExecuteAsync(new JsonObject { ["@type"] = "getChat", ["chat_id"] = chatId }, token); }
        catch (TelegramRequestException ex) when (string.Equals(ex.Message, "Chat not found", StringComparison.OrdinalIgnoreCase))
        {
            // A new TDLib session may not have loaded this registered chat yet.
            // Discovery only reads/validates chats; never create a replacement vault.
            await DiscoverAsync(expectedAccountId, token);
            chat = await session.ExecuteAsync(new JsonObject { ["@type"] = "getChat", ["chat_id"] = chatId }, token);
        }
        if (chat["id"]?.GetValue<long>() != chatId) throw new InvalidDataException("Telegram returned an unexpected storage chat.");
        var requestId = await ValidatePrivateChannelAsync(chat, token);
        return new(chatId, expectedAccountId, chat["title"]?.GetValue<string>() ?? "TeleSelfCloud Storage", requestId);
    }

    // Explicitly requested additional vault: never rewrites the legacy primary-channel setting.
    public async Task<TelegramStorageChannelInfo> CreateAdditionalAsync(string title, string expectedAccountId, CancellationToken token, string? creationRequestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 128) throw new ArgumentException("The vault name must be at most 128 characters.", nameof(title));
        if (creationRequestId is not null && (!Guid.TryParseExact(creationRequestId, "N", out var requestGuid) || requestGuid.ToString("N") != creationRequestId))
            throw new ArgumentException("Invalid vault creation request ID.", nameof(creationRequestId));
        var user = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMe" }, token);
        if (user["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture) != expectedAccountId)
            throw new InvalidOperationException("The configured storage channel belongs to a different Telegram account.");
        var chat = await session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "createNewSupergroupChat", ["title"] = title.Trim(), ["is_forum"] = false,
            ["is_channel"] = true, ["description"] = StorageDescription + (creationRequestId is null ? "" : "\nCreation: " + creationRequestId), ["location"] = null,
            ["message_auto_delete_time"] = 0, ["for_import"] = false
        }, token);
        var chatId = chat["id"]?.GetValue<long>() ?? throw new InvalidDataException("Telegram did not return the newly created storage channel ID.");
        try
        {
            var confirmedRequest = await ValidatePrivateChannelAsync(chat, CancellationToken.None);
            if (confirmedRequest != creationRequestId) throw new InvalidDataException("The created vault does not match the saved creation request.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Telegram created storage chat {chatId}, but verification did not finish. Add this existing chat instead of creating another. {ex.Message}", ex);
        }
        return new(chatId, expectedAccountId, chat["title"]?.GetValue<string>() ?? title.Trim(), creationRequestId);
    }

    public async Task<TelegramStorageChannelInfo?> FindExistingAsync(CancellationToken cancellationToken)
    {
        var user = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMe" }, cancellationToken);
        var accountId = user["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("TDLib did not return the active Telegram account ID.");

        var saved = Load(accountId);
        if (saved is not null)
        {
            if (!string.Equals(saved.AccountId, accountId, StringComparison.Ordinal))
                throw new InvalidOperationException("The configured storage channel belongs to a different Telegram account.");
            await session.ExecuteAsync(new JsonObject
            {
                ["@type"] = "getChats",
                ["chat_list"] = new JsonObject { ["@type"] = "chatListMain" },
                ["offset_order"] = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["offset_chat_id"] = 0,
                ["limit"] = 100
            }, cancellationToken);
            JsonObject existingChat;
            try
            {
                existingChat = await session.ExecuteAsync(new JsonObject
                {
                    ["@type"] = "getChat",
                    ["chat_id"] = saved.ChatId
                }, cancellationToken);
            }
            catch (TelegramRequestException ex) when (string.Equals(ex.Message, "Chat not found", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The saved Telegram storage channel could not be found or accessed. Local manifests and staged parts were preserved.", ex);
            }
            if (existingChat["id"]?.GetValue<long>() != saved.ChatId)
                throw new InvalidDataException("Telegram returned an unexpected storage chat.");
            await ValidatePrivateChannelAsync(existingChat, cancellationToken);
            return saved;
        }

        var recoveredChat = await FindExistingStorageChannelAsync(cancellationToken);
        if (recoveredChat is not null)
        {
            var recoveredChatId = recoveredChat["id"]?.GetValue<long>()
                ?? throw new InvalidDataException("Telegram returned a storage channel without an ID.");
            var recovered = new TelegramStorageChannelInfo(recoveredChatId, accountId, recoveredChat["title"]?.GetValue<string>() ?? "TeleSelfCloud Storage");
            await ValidatePrivateChannelAsync(recoveredChat, cancellationToken);
            Save(recovered);
            return recovered;
        }

        return null;
    }

    public async Task<TelegramStorageChannelInfo> GetOrCreateAsync(bool createIfMissing, CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(cancellationToken);
        if (existing is not null) return existing;

        if (!createIfMissing)
            throw new InvalidOperationException("No TeleSelfCloud storage channel is configured for this account.");

        var user = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMe" }, cancellationToken);
        var accountId = user["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("TDLib did not return the active Telegram account ID.");
        var title = $"TeleSelfCloud Storage {user["first_name"]?.GetValue<string>() ?? "Account"}";
        var createdChat = await session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "createNewSupergroupChat",
            ["title"] = title,
            ["is_forum"] = false,
            ["is_channel"] = true,
            ["description"] = StorageDescription,
            ["location"] = null,
            ["message_auto_delete_time"] = 0,
            ["for_import"] = false
        }, cancellationToken);
        var chatId = createdChat["id"]?.GetValue<long>()
            ?? throw new InvalidDataException("Telegram did not return the newly created storage channel ID.");
        if (createdChat["type"]?["@type"]?.GetValue<string>() != "chatTypeSupergroup" ||
            createdChat["type"]?["is_channel"]?.GetValue<bool>() != true)
            throw new InvalidDataException("Telegram did not create a channel suitable for TeleSelfCloud storage.");

        var info = new TelegramStorageChannelInfo(chatId, accountId, title);
        await ValidatePrivateChannelAsync(createdChat, cancellationToken);
        Save(info);
        return info;
    }

    private async Task<JsonObject?> FindExistingStorageChannelAsync(CancellationToken cancellationToken)
    {
        var user = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMe" }, cancellationToken);
        var account = user["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("TDLib did not return the active Telegram account ID.");
        var candidates = await DiscoverAsync(account, cancellationToken, rejectUnsafeMarked: true);
        if (candidates.Count > 1) throw new InvalidOperationException("Multiple private storage vaults were found. Find vaults and choose one to open.");
        if (candidates.Count == 0) return null;
        return await session.ExecuteAsync(new JsonObject { ["@type"] = "getChat", ["chat_id"] = candidates[0].ChatId }, cancellationToken);
    }
    private async Task<string?> ValidatePrivateChannelAsync(JsonObject chat, CancellationToken cancellationToken)
    {
        if (chat["type"]?["@type"]?.GetValue<string>() != "chatTypeSupergroup" ||
            chat["type"]?["is_channel"]?.GetValue<bool>() != true)
            throw new InvalidDataException("The configured Telegram storage chat is not a channel.");

        var supergroupId = chat["type"]?["supergroup_id"]?.GetValue<long>()
            ?? throw new InvalidDataException("Telegram did not return the storage channel's supergroup ID.");
        var supergroup = await session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "getSupergroup",
            ["supergroup_id"] = supergroupId
        }, cancellationToken);
        var usernames = supergroup["usernames"]?["active_usernames"] as JsonArray;
        if (supergroup["status"]?["@type"]?.GetValue<string>() != "chatMemberStatusCreator" ||
            supergroup["status"]?["is_member"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The active account must be the owner and a member of the storage channel.");
        if (usernames is { Count: > 0 })
            throw new InvalidOperationException("The storage channel has an active public username; remove it before using the channel for private storage.");
        if (supergroup["member_count"] is null)
            throw new InvalidOperationException("TDLib did not provide the storage channel member count, so owner-only privacy could not be verified.");
        var memberCount = supergroup["member_count"]!.GetValue<int>();
        if (memberCount != 1)
            throw new InvalidOperationException($"The storage channel must contain only its owner; Telegram currently reports {memberCount} members.");

        var fullInfo = await session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "getSupergroupFullInfo",
            ["supergroup_id"] = supergroupId
        }, cancellationToken);
        var description = fullInfo["description"]?.GetValue<string>();
        if (!IsStorageDescription(description))
            throw new InvalidOperationException("The configured channel is not marked as TeleSelfCloud storage.");
        return description == StorageDescription ? null : description![(StorageDescription.Length + "\nCreation: ".Length)..];
    }

    private static bool IsStorageDescription(string? description)
    {
        if (description == StorageDescription) return true;
        var prefix = StorageDescription + "\nCreation: ";
        return description?.StartsWith(prefix, StringComparison.Ordinal) == true &&
            Guid.TryParseExact(description[prefix.Length..], "N", out var id) && id.ToString("N") == description[prefix.Length..];
    }

    public async Task<IReadOnlyList<TelegramStorageChannelInfo>> DiscoverAsync(string expectedAccountId, CancellationToken token, bool rejectUnsafeMarked = false)
    {
        var user = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMe" }, token);
        if (user["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture) != expectedAccountId)
            throw new InvalidOperationException("The configured storage channel belongs to a different Telegram account.");
        var ids = new HashSet<long>();
        foreach (var list in new[] { "chatListMain", "chatListArchive" })
        {
            var completed = false;
            for (var page = 0; page < 10000; page++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var loaded = await session.ExecuteAsync(new JsonObject { ["@type"] = "loadChats", ["chat_list"] = new JsonObject { ["@type"] = list }, ["limit"] = 100 }, token);
                    if (loaded["@type"]?.GetValue<string>() != "ok")
                        throw new InvalidDataException("Vault discovery received an invalid loadChats response. No absence was inferred.");
                }
                catch (TelegramRequestException ex) when (ex.ErrorCode == 404) { completed = true; break; }
            }
            if (!completed) throw new InvalidDataException("Vault discovery did not reach the end of the chat list. Retry discovery; no absence was inferred.");
            var snapshot = await session.ExecuteAsync(new JsonObject { ["@type"] = "getChats", ["chat_list"] = new JsonObject { ["@type"] = list }, ["limit"] = 100001 }, token);
            if (snapshot["chat_ids"] is not JsonArray chatIds || chatIds.Count > 100000)
                throw new InvalidDataException("Vault discovery returned an invalid or oversized chat list. No absence was inferred.");
            var snapshotIds = new HashSet<long>();
            foreach (var node in chatIds)
            {
                if (!long.TryParse(node?.ToString(), out var id) || id == 0 || !snapshotIds.Add(id))
                    throw new InvalidDataException("Vault discovery returned an invalid chat ID.");
                ids.Add(id);
            }
        }
        var result = new List<TelegramStorageChannelInfo>();
        foreach (var id in ids.Order())
        {
            token.ThrowIfCancellationRequested();
            var chat = await session.ExecuteAsync(new JsonObject { ["@type"] = "getChat", ["chat_id"] = id }, token);
            if (chat["id"]?.GetValue<long>() != id) throw new InvalidDataException("Telegram returned an unexpected storage chat.");
            if (chat["type"]?["@type"]?.GetValue<string>() != "chatTypeSupergroup" || chat["type"]?["is_channel"]?.GetValue<bool>() != true) continue;
            var supergroupId = chat["type"]?["supergroup_id"]?.GetValue<long>()
                ?? throw new InvalidDataException("Telegram did not return the storage channel's supergroup ID.");
            var fullInfo = await session.ExecuteAsync(new JsonObject { ["@type"] = "getSupergroupFullInfo", ["supergroup_id"] = supergroupId }, token);
            if (!IsStorageDescription(fullInfo["description"]?.GetValue<string>())) continue;
            try
            {
                var requestId = await ValidatePrivateChannelAsync(chat, token);
                result.Add(new(id, expectedAccountId, chat["title"]?.GetValue<string>() ?? "TeleSelfCloud Storage", requestId));
            }
            catch (InvalidOperationException) when (!rejectUnsafeMarked) { /* Unsafe marked channels are not selectable. Automatic initial creation must instead stop. */ }
        }
        return result;
    }

    private TelegramStorageChannelInfo? Load(string expectedAccountId)
    {
        var fullPath = Path.GetFullPath(settingsPath);
        RejectReparsePoint(fullPath);
        if (!File.Exists(settingsPath)) return null;
        using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var maxStoredBytes = settingsCipherFactory is null ? MaxSettingsBytes : MaxStoredSettingsBytes;
        if (input.Length > maxStoredBytes) throw new InvalidDataException("The saved storage settings exceed their size limit. The file was kept.");
        var raw = new byte[(int)input.Length];
        try
        {
            input.ReadExactly(raw);
            if (settingsCipherFactory is null)
                return JsonSerializer.Deserialize<TelegramStorageChannelInfo>(raw);

            using var cipher = settingsCipherFactory();
            var protectedRecord = LocalRecordCipher.IsProtectedRecord(raw);
            var plaintext = protectedRecord ? cipher.Unprotect(raw) : raw;
            try
            {
                if (plaintext.Length > MaxSettingsBytes) throw new InvalidDataException("The saved storage settings exceed their size limit. The file was kept.");
                var info = JsonSerializer.Deserialize<TelegramStorageChannelInfo>(plaintext);
                if (info is not null && !protectedRecord &&
                    string.Equals(info.AccountId, expectedAccountId, StringComparison.Ordinal))
                    Save(info, cipher);
                return info;
            }
            finally
            {
                if (protectedRecord) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    private void Save(TelegramStorageChannelInfo info)
    {
        if (settingsCipherFactory is null) { WriteSettings(info, null); return; }
        using var cipher = settingsCipherFactory();
        WriteSettings(info, cipher);
    }

    private void Save(TelegramStorageChannelInfo info, LocalRecordCipher cipher) => WriteSettings(info, cipher);

    private void WriteSettings(TelegramStorageChannelInfo info, LocalRecordCipher? cipher)
    {
        var fullPath = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        RejectReparsePoint(fullPath);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(info);
        byte[]? bytes = null;
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (plaintext.Length > MaxSettingsBytes) throw new InvalidDataException("The saved storage settings exceed their size limit.");
            bytes = cipher is null ? plaintext : cipher.Protect(plaintext);
            var maxStoredBytes = cipher is null ? MaxSettingsBytes : MaxStoredSettingsBytes;
            if (bytes.Length > maxStoredBytes) throw new InvalidDataException("The saved storage settings exceed their size limit.");
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
            else File.Move(temporaryPath, fullPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (bytes is not null && !ReferenceEquals(bytes, plaintext)) CryptographicOperations.ZeroMemory(bytes);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("The saved storage settings use an unsupported linked or directory path. The file was kept.");
    }
}

