using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class TelegramStorageChannelServiceTests
{
    [Fact]
    public async Task ColdSessionLoadsChatListsAndReopensTheRegisteredVaultWithoutCreatingOrRewriting()
    {
        var path = NewSettingsPath();
        var saved = JsonSerializer.Serialize(new TelegramStorageChannelInfo(101, "42", "Saved vault"));
        File.WriteAllText(path, saved);
        var telegram = new FakeTelegramClient([101], requiresChatListLoad: true);
        var service = new TelegramStorageChannelService(telegram, path);
        Assert.Equal(101, (await service.VerifyExistingAsync(101, "42", default)).ChatId);
        Assert.True(telegram.ChatListRequests > 0);
        Assert.Equal(0, telegram.CreatedChannels);
        Assert.Equal(saved, File.ReadAllText(path));
        var lists = telegram.ChatListRequests;
        Assert.Equal(101, (await service.VerifyExistingAsync(101, "42", default)).ChatId);
        Assert.Equal(lists, telegram.ChatListRequests);
    }

    [Fact]
    public async Task MissingRegisteredVaultIsNotReplacedOrErasedAfterLoadingChatLists()
    {
        var path = NewSettingsPath();
        var saved = JsonSerializer.Serialize(new TelegramStorageChannelInfo(101, "42", "Saved vault"));
        File.WriteAllText(path, saved);
        var telegram = new FakeTelegramClient([], savedChatUnavailable: true);
        var service = new TelegramStorageChannelService(telegram, path);
        await Assert.ThrowsAsync<TelegramRequestException>(() => service.VerifyExistingAsync(101, "42", default));
        Assert.True(telegram.ChatListRequests > 0);
        Assert.Equal(0, telegram.CreatedChannels);
        Assert.Equal(saved, File.ReadAllText(path));
    }
    [Fact]
    public async Task ReusesDiscoveredPrivateStorageAndDoesNotCreateAnotherChannel()
    {
        var path = NewSettingsPath();
        var telegram = new FakeTelegramClient(existingChatIds: [101]);
        var service = new TelegramStorageChannelService(telegram, path);

        var first = await service.GetOrCreateAsync(createIfMissing: true, CancellationToken.None);
        var second = await service.GetOrCreateAsync(createIfMissing: true, CancellationToken.None);

        Assert.Equal(101, first.ChatId);
        Assert.Equal(first, second);
        Assert.Equal(0, telegram.CreatedChannels);
        Assert.Equal("42", first.AccountId);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CreatesStorageOnceWhenAccountHasNoExistingChannel()
    {
        var path = NewSettingsPath();
        var telegram = new FakeTelegramClient(existingChatIds: []);
        var service = new TelegramStorageChannelService(telegram, path);

        var first = await service.GetOrCreateAsync(createIfMissing: true, CancellationToken.None);
        var second = await service.GetOrCreateAsync(createIfMissing: true, CancellationToken.None);

        Assert.Equal(9001, first.ChatId);
        Assert.Equal(first, second);
        Assert.Equal(1, telegram.CreatedChannels);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DoesNotPersistOrCreateWhenDiscoveredChannelIsNotPrivate()
    {
        var path = NewSettingsPath();
        var telegram = new FakeTelegramClient(existingChatIds: [101], memberCount: 2);
        var service = new TelegramStorageChannelService(telegram, path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetOrCreateAsync(createIfMissing: true, CancellationToken.None));

        Assert.Contains("only its owner", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, telegram.CreatedChannels);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RejectsSavedStorageOwnedByAnotherAccountWithoutCreatingOrInspectingChats()
    {
        var path = NewSettingsPath();
        File.WriteAllText(path, JsonSerializer.Serialize(new TelegramStorageChannelInfo(101, "other-account", "Storage")));
        var telegram = new FakeTelegramClient(existingChatIds: []);
        var service = new TelegramStorageChannelService(telegram, path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetOrCreateAsync(createIfMissing: true, CancellationToken.None));

        Assert.Contains("different Telegram account", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, telegram.CreatedChannels);
        Assert.Equal(0, telegram.ChatListRequests);
    }

    [Fact]
    public async Task KeepsLocalChannelConfigAndExplainsWhenSavedRemoteChannelIsMissing()
    {
        var path = NewSettingsPath();
        File.WriteAllText(path, JsonSerializer.Serialize(new TelegramStorageChannelInfo(101, "42", "Storage")));
        var telegram = new FakeTelegramClient(existingChatIds: [], savedChatUnavailable: true);
        var service = new TelegramStorageChannelService(telegram, path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FindExistingAsync(CancellationToken.None));

        Assert.Contains("Local manifests and staged parts were preserved", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.Equal(0, telegram.CreatedChannels);
    }

    [Fact]
    public async Task DoesNotCreateStorageWhenCreationWasNotRequested()
    {
        var path = NewSettingsPath();
        var telegram = new FakeTelegramClient(existingChatIds: []);
        var service = new TelegramStorageChannelService(telegram, path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetOrCreateAsync(createIfMissing: false, CancellationToken.None));

        Assert.Equal(0, telegram.CreatedChannels);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("chatMemberStatusAdministrator", true)]
    [InlineData("chatMemberStatusCreator", false)]
    [InlineData("", true)]
    public async Task ExplicitVaultSelectionRequiresOwnerMembership(string status, bool member)
    {
        var path = NewSettingsPath();
        var service = new TelegramStorageChannelService(new FakeTelegramClient([101], status: status, isMember: member), path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyExistingAsync(101, "42", default));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AdditionalCreationAndSelectionDoNotOverwriteLegacyPrimarySetting()
    {
        var path = NewSettingsPath();
        var source = JsonSerializer.Serialize(new TelegramStorageChannelInfo(101, "42", "Primary"));
        File.WriteAllText(path, source);
        var client = new FakeTelegramClient([101]);
        var service = new TelegramStorageChannelService(client, path);
        var created = await service.CreateAdditionalAsync("Additional", "42", default);
        Assert.Equal(9001, created.ChatId);
        var verified = await service.VerifyExistingAsync(102, "42", default);
        Assert.Equal(102, verified.ChatId);
        Assert.Equal(source, File.ReadAllText(path));
        Assert.Equal(1, client.CreatedChannels);
    }

    [Fact]
    public async Task ForeignIdentityCannotCreateAnotherVault()
    {
        var path = NewSettingsPath();
        var client = new FakeTelegramClient([]);
        var service = new TelegramStorageChannelService(client, path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAdditionalAsync("Additional", "43", default));
        Assert.Equal(0, client.CreatedChannels);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AcceptedCreationWithFailedVerificationIncludesRecoveryChatIdWithoutDeletingIt()
    {
        var path = NewSettingsPath();
        var client = new FakeTelegramClient([], memberCount: 2);
        var service = new TelegramStorageChannelService(client, path);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAdditionalAsync("Additional", "42", default));
        Assert.Contains("9001", error.Message);
        Assert.Contains("Add this existing chat", error.Message);
        Assert.Equal(1, client.CreatedChannels);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ProtectedAccountSettingsAreCiphertextAndReopenWithTheScopedKey()
    {
        var path = NewSettingsPath();
        var client = new FakeTelegramClient([101]);
        var info = new TelegramStorageChannelInfo(101, "42", "Primary storage");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(info);
        using (var cipher = TestCipher()) File.WriteAllBytes(path, cipher.Protect(plaintext));
        CryptographicOperations.ZeroMemory(plaintext);

        var saved = await new TelegramStorageChannelService(client, path, TestCipher).FindExistingAsync(CancellationToken.None);

        Assert.Equal(info, saved);
        var bytes = File.ReadAllBytes(path);
        Assert.True(LocalRecordCipher.IsProtectedRecord(bytes));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(JsonSerializer.SerializeToUtf8Bytes(info)));
        Assert.Equal(info, await new TelegramStorageChannelService(client, path, TestCipher)
            .FindExistingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExistingPlaintextAccountSettingsMigrateAtomicallyWhenCatalogProtectionIsEnabled()
    {
        var path = NewSettingsPath();
        var info = new TelegramStorageChannelInfo(101, "42", "Primary storage");
        File.WriteAllText(path, JsonSerializer.Serialize(info));
        var client = new FakeTelegramClient([101]);

        var actual = await new TelegramStorageChannelService(client, path, TestCipher)
            .FindExistingAsync(CancellationToken.None);

        Assert.Equal(info, actual);
        Assert.True(LocalRecordCipher.IsProtectedRecord(File.ReadAllBytes(path)));
        Assert.Equal(info, await new TelegramStorageChannelService(client, path, TestCipher)
            .FindExistingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WrongCatalogKeyPreservesProtectedAccountSettingsAndStopsBeforeChatLookup()
    {
        var path = NewSettingsPath();
        var client = new FakeTelegramClient([101]);
        await new TelegramStorageChannelService(client, path, TestCipher).FindExistingAsync(CancellationToken.None);
        var original = File.ReadAllBytes(path);
        var chatListRequests = client.ChatListRequests;

        await Assert.ThrowsAnyAsync<CryptographicException>(() => new TelegramStorageChannelService(client, path,
            () => new LocalRecordCipher(WrongTestKey, TestProtectionId,
                "account-storage-settings", "account:42:storage-channel-settings"))
            .FindExistingAsync(CancellationToken.None));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(chatListRequests, client.ChatListRequests);
        Assert.Equal(0, client.CreatedChannels);
    }

    [Fact]
    public async Task OversizedStorageSettingsArePreservedAndDoNotTriggerRecoveryCreation()
    {
        var path = NewSettingsPath();
        var original = Enumerable.Repeat((byte)'x', 16 * 1024 + 1).ToArray();
        File.WriteAllBytes(path, original);
        var client = new FakeTelegramClient([]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramStorageChannelService(client, path, TestCipher)
            .FindExistingAsync(CancellationToken.None));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, client.CreatedChannels);
        Assert.Equal(0, client.ChatListRequests);
    }

    [Fact]
    public async Task DirectoryJunctionAtSettingsPathIsRejectedWithoutFollowingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-StorageChannelJunctionTests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "outside");
        var path = Path.Combine(root, "storage-channel.json");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        try
        {
            using var create = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    ArgumentList = { "/c", "mklink", "/J", path, target }
                }
            };
            Assert.True(create.Start());
            await create.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, create.ExitCode);
            var client = new FakeTelegramClient([]);

            await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramStorageChannelService(client, path, TestCipher)
                .FindExistingAsync(CancellationToken.None));

            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.Equal(0, client.ChatListRequests);
            Assert.Equal(0, client.CreatedChannels);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string TestKey => Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
    private static string WrongTestKey => Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray());
    private static string TestProtectionId => "4a1539415f37484eb2354bd795fe0a14";
    private static LocalRecordCipher TestCipher() => new(TestKey, TestProtectionId,
        "account-storage-settings", "account:42:storage-channel-settings");
    private static string NewSettingsPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-StorageChannelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "storage-channel.json");
    }

    private sealed class FakeTelegramClient(IReadOnlyList<long> existingChatIds, int memberCount = 1, bool savedChatUnavailable = false, string status = "chatMemberStatusCreator", bool isMember = true, bool requiresChatListLoad = false) : ITelegramRequestClient
    {
        public int CreatedChannels { get; private set; }
        public int ChatListRequests { get; private set; }

        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = request["@type"]?.GetValue<string>();
            var response = type switch
            {
                "getMe" => new JsonObject { ["id"] = 42L, ["first_name"] = "Tester" },
                "getChats" => GetChats(),
                "loadChats" => throw TelegramRequestException.From(404, "All chats loaded"),
                "getChat" => GetChat(request["chat_id"]?.GetValue<long>() ?? 101),
                "createNewSupergroupChat" => CreateChannel(),
                "getSupergroup" => new JsonObject
                {
                    ["member_count"] = memberCount,
                    ["status"] = new JsonObject { ["@type"] = status, ["is_member"] = isMember },
                    ["usernames"] = new JsonObject { ["active_usernames"] = new JsonArray() }
                },
                "getSupergroupFullInfo" => new JsonObject
                {
                    ["description"] = "Private file storage for TeleSelfCloud. Do not add other members."
                },
                _ => throw new NotSupportedException($"Unexpected Telegram request: {type}")
            };
            return Task.FromResult(response);
        }

        private JsonObject GetChats()
        {
            ChatListRequests++;
            return new JsonObject { ["chat_ids"] = new JsonArray(existingChatIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
        }

        private JsonObject CreateChannel()
        {
            CreatedChannels++;
            var chat = BuildChat(9001);
            chat["title"] = "TeleSelfCloud Storage Tester";
            return chat;
        }

        private JsonObject GetChat(long id)
        {
            if (savedChatUnavailable || requiresChatListLoad && ChatListRequests == 0) throw TelegramRequestException.From(400, "Chat not found");
            return BuildChat(id);
        }

        private static JsonObject BuildChat(long id) => new()
        {
            ["id"] = id,
            ["title"] = id == 9001 ? "TeleSelfCloud Storage Tester" : "Existing TeleSelfCloud Storage",
            ["type"] = new JsonObject
            {
                ["@type"] = "chatTypeSupergroup",
                ["is_channel"] = true,
                ["supergroup_id"] = 99L
            }
        };
    }
}
