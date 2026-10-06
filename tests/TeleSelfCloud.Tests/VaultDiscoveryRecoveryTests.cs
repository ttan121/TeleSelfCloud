using System.Text.Json.Nodes;
using TeleSelfCloud.Infrastructure.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Tests;

public sealed class VaultDiscoveryRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.VaultDiscovery", Guid.NewGuid().ToString("N"));
    private VaultCreationJournal Journal => new(root, "42");
    private TelegramStorageChannelService Service(Client client) => new(client, Path.Combine(root, "storage-channel.json"));
    private VaultCreationWorkflow Workflow(Client client) => new(Service(client), Journal, "42");
    private VaultCreationJournal ProtectedJournal(LocalProfileLease lease, string directory) => new(directory, "42",
        identity => new LocalRecordCipher(Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()),
            "ffffffffffffffffffffffffffffffff", "vault-creation", "account:42:" + identity), lease);
    private static Task Register(TelegramStorageChannelInfo _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task DiscoveryLoadsBeyondFirstHundredAndIncludesArchiveWithoutChangingSettings()
    {
        var client = new Client { LoadBatch = 37 };
        for (var i = 1; i <= 230; i++) client.Add(-1000 - i, marked: i == 230);
        client.Add(-2000, archive: true);
        var found = await Service(client).DiscoverAsync("42", default);
        Assert.Equal(new long[] { -2000, -1230 }, found.Select(v => v.ChatId).Order());
        Assert.True(client.LoadCalls >= 5);
        Assert.Equal(231, client.Inspected);
        Assert.Equal(0, client.Creates);
        Assert.False(File.Exists(Path.Combine(root, "storage-channel.json")));
    }

    [Fact]
    public async Task ForeignIdentityAndNetworkErrorDoNotProduceAnAbsenceResult()
    {
        var client = new Client { Account = 43 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(client).DiscoverAsync("42", default));
        Assert.Equal(0, client.LoadCalls);
        client.Account = 42; client.FailLoading = true;
        await Assert.ThrowsAsync<IOException>(() => Service(client).DiscoverAsync("42", default));
        Assert.Equal(0, client.Inspected);
    }

    [Fact]
    public async Task MalformedLoadResponseStopsImmediatelyWithoutLoopingOrInferringAbsence()
    {
        var client = new Client { MalformedLoad = true }; client.Add(-1001);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).DiscoverAsync("42", default));
        Assert.Equal(1, client.LoadCalls);
        Assert.Equal(0, client.Inspected);
        Assert.Equal(0, client.Creates);
    }

    [Fact]
    public async Task UnownedChannelsAreSkippedButProtocolErrorsStopDiscovery()
    {
        var client = new Client(); client.Add(-1001); client.Add(-1002);
        client.Channels[-1001] = client.Channels[-1001] with { Owner = false };
        var found = await Service(client).DiscoverAsync("42", default);
        Assert.Equal(-1002, found.Single().ChatId);
        client.WrongChat = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).DiscoverAsync("42", default));
    }

    [Fact]
    public async Task AcceptedCreationWithLostResponseIsRecoveredByNonceWithoutResending()
    {
        var client = new Client { LoseResponse = true };
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Requested title", Register, default));
        var pending = (await Journal.LoadAsync(default))!;
        Assert.Equal(VaultCreationPhase.Dispatched, pending.Phase);
        Assert.Null(pending.ChatId);
        client.LoseResponse = false;
        var recovered = await Workflow(client).ExecuteAsync("Different title must not create another", Register, default);
        Assert.Equal(pending.RequestId, recovered.CreationRequestId);
        Assert.Equal(1, client.Creates);
        Assert.False(Journal.HasPending);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "created-vaults"), "*.json"));
    }

    [Fact]
    public async Task ConfirmedCreationSurvivesRegistrationFailureAndRenamedRemoteTitle()
    {
        var client = new Client();
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Original", (_, _) => throw new IOException("registry unavailable"), default));
        var pending = (await Journal.LoadAsync(default))!;
        Assert.Equal(VaultCreationPhase.Confirmed, pending.Phase);
        client.Channels[pending.ChatId!.Value] = client.Channels[pending.ChatId.Value] with { Title = "Renamed" };
        var recovered = await Workflow(client).ExecuteAsync(null, Register, default);
        Assert.Equal("Renamed", recovered.Title);
        Assert.Equal(1, client.Creates);
        Assert.False(Journal.HasPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrAmbiguousMatchingNonceRetainsIntentAndDoesNotCreateAgain(bool ambiguous)
    {
        var client = new Client { LoseResponse = true };
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Original", Register, default));
        var pending = (await Journal.LoadAsync(default))!;
        var created = client.Channels.Single().Value;
        if (ambiguous) client.Channels[-2000] = created with { Id = -2000 };
        else client.Channels.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(client).ExecuteAsync("New", Register, default));
        Assert.Equal(1, client.Creates);
        Assert.Equal(pending, await Journal.LoadAsync(default));
    }

    [Fact]
    public async Task CorruptCreationJournalBlocksAnyRemoteWorkAndKeepsBytes()
    {
        var client = new Client { LoseResponse = true };
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Original", Register, default));
        var path = Path.Combine(root, "vault-creation.json");
        var corrupt = (await File.ReadAllTextAsync(path)).Replace("Original", "Damaged");
        await File.WriteAllTextAsync(path, corrupt);
        var requests = client.Requests;
        await Assert.ThrowsAsync<InvalidDataException>(() => Workflow(client).ExecuteAsync("New", Register, default));
        Assert.Equal(requests, client.Requests);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PreparedCrashCanDispatchOnceAndAcceptedLateCancellationStillRegisters()
    {
        var client = new Client();
        var prepared = new VaultCreationAttempt(1, "42", Guid.NewGuid().ToString("N"), "Original", DateTimeOffset.UtcNow, VaultCreationPhase.Prepared, null);
        await Journal.SaveAsync(prepared, default);
        using var cancel = new CancellationTokenSource();
        var result = await Workflow(client).ExecuteAsync(null, (_, token) => { Assert.False(token.CanBeCanceled); cancel.Cancel(); return Task.CompletedTask; }, cancel.Token);
        Assert.Equal(prepared.RequestId, result.CreationRequestId);
        Assert.Equal(1, client.Creates);
        Assert.False(Journal.HasPending);
    }

    [Fact]
    public async Task ChangedMarkerOnConfirmedChannelBlocksRegistrationWithoutResend()
    {
        var client = new Client();
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Original", (_, _) => throw new IOException(), default));
        var pending = (await Journal.LoadAsync(default))!;
        client.Channels[pending.ChatId!.Value] = client.Channels[pending.ChatId.Value] with { Description = Client.Marker };
        await Assert.ThrowsAsync<InvalidDataException>(() => Workflow(client).ExecuteAsync(null, Register, default));
        Assert.Equal(1, client.Creates);
        Assert.Equal(pending, await Journal.LoadAsync(default));
    }

    [Fact]
    public async Task ReceiptArchiveFailureRetainsConfirmedAttemptForIdempotentRegistrationRetry()
    {
        Directory.CreateDirectory(root);
        var occupied = Path.Combine(root, "created-vaults");
        await File.WriteAllTextAsync(occupied, "owned test obstacle");
        var client = new Client();
        var registered = 0;
        Task Count(TelegramStorageChannelInfo _, CancellationToken __) { registered++; return Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Original", Count, default));
        Assert.Equal(VaultCreationPhase.Confirmed, (await Journal.LoadAsync(default))!.Phase);
        Assert.Equal(1, client.Creates);
        Assert.Equal("owned test obstacle", await File.ReadAllTextAsync(occupied));
        File.Delete(occupied);
        await Workflow(client).ExecuteAsync(null, Count, default);
        Assert.Equal(2, registered);
        Assert.Equal(1, client.Creates);
        Assert.False(Journal.HasPending);
    }

    [Fact]
    public async Task CanceledRecoveryAndLeaseContentionKeepIntentWithoutRemoteRequests()
    {
        var client = new Client { LoseResponse = true };
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Original", Register, default));
        var pending = await Journal.LoadAsync(default);
        var requests = client.Requests;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Workflow(client).ExecuteAsync(null, Register, cancel.Token));
        using (Journal.AcquireLease())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(client).ExecuteAsync(null, Register, default));
        Assert.Equal(requests, client.Requests);
        Assert.Equal(pending, await Journal.LoadAsync(default));
    }

    [Fact]
    public async Task ExplicitStopArchivesIntentAndRetainsRemoteChannelAndStaleReviewsCannotStopNewAttempt()
    {
        var client = new Client { LoseResponse = true };
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Original", Register, default));
        var pending = (await Journal.LoadAsync(default))!;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "vault-creation.json"));
        var requests = client.Requests;
        await Assert.ThrowsAsync<InvalidDataException>(() => Journal.AbandonAsync(Guid.NewGuid().ToString("N"), default));
        Assert.True(Journal.HasPending);
        await Journal.AbandonAsync(pending.RequestId, default);
        Assert.False(Journal.HasPending);
        Assert.Equal(requests, client.Requests);
        Assert.Single(client.Channels);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(root, "abandoned-vault-creations")).Single()));
    }

    [Fact]
    public async Task ProtectedCreationMigratesPendingUnderOperationLeaseAndArchivesCiphertext()
    {
        var client = new Client { LoseResponse = true };
        await Assert.ThrowsAsync<IOException>(() => Workflow(client).ExecuteAsync("Original vault", Register, default));
        var legacyPath = Path.Combine(root, "vault-creation.json");
        Assert.DoesNotContain("TSCREC01", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(legacyPath)));
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var journal = ProtectedJournal(lease, root);
        var workflow = new VaultCreationWorkflow(Service(client), journal, "42");
        var created = await workflow.ExecuteAsync(null, Register, default);
        Assert.Equal(1, client.Creates);
        Assert.Equal("Original vault", created.Title);
        Assert.False(journal.HasPending);
        var receipt = Directory.GetFiles(Path.Combine(root, "created-vaults"), "*.json").Single();
        var protectedBytes = await File.ReadAllBytesAsync(receipt);
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(protectedBytes, 0, 8));
        Assert.DoesNotContain("Original vault", System.Text.Encoding.UTF8.GetString(protectedBytes));
        await ProtectedJournal(lease, root).PrepareProtectedArchivesAsync(default);
        Assert.Equal(protectedBytes, await File.ReadAllBytesAsync(receipt));
    }

    [Fact]
    public async Task ProtectedArchiveInventoryMigratesPriorPlaintextReceipts()
    {
        Directory.CreateDirectory(root);
        var attempt = new VaultCreationAttempt(1, "42", Guid.NewGuid().ToString("N"), "Old private title", DateTimeOffset.UtcNow,
            VaultCreationPhase.Confirmed, -1234);
        await Journal.SaveAsync(attempt, default);
        await Journal.ArchiveRegisteredAsync(attempt.RequestId, default);
        var receipt = Directory.GetFiles(Path.Combine(root, "created-vaults"), "*.json").Single();
        Assert.DoesNotContain("TSCREC01", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(receipt)));
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var protectedJournal = ProtectedJournal(lease, root);
        using (protectedJournal.AcquireLease()) await protectedJournal.PrepareProtectedArchivesAsync(default);
        var bytes = await File.ReadAllBytesAsync(receipt);
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.DoesNotContain("Old private title", System.Text.Encoding.UTF8.GetString(bytes));
        using (protectedJournal.AcquireLease()) await ProtectedJournal(lease, root).PrepareProtectedArchivesAsync(default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(receipt));
    }

    [Fact]
    public async Task WrongKeyCannotResumeProtectedVaultCreationOrChangeItsJournal()
    {
        var client = new Client { LoseResponse = true };
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var protectedJournal = ProtectedJournal(lease, root);
        await Assert.ThrowsAsync<IOException>(() => new VaultCreationWorkflow(Service(client), protectedJournal, "42")
            .ExecuteAsync("Private title", Register, default));
        var path = Path.Combine(root, "vault-creation.json");
        var original = await File.ReadAllBytesAsync(path);
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(original, 0, 8));
        var wrongKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var wrongJournal = new VaultCreationJournal(root, "42", identity => new LocalRecordCipher(wrongKey,
            "ffffffffffffffffffffffffffffffff", "vault-creation", "account:42:" + identity), lease);
        var requests = client.Requests;
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() =>
            new VaultCreationWorkflow(Service(client), wrongJournal, "42").ExecuteAsync(null, Register, default));
        Assert.Equal(requests, client.Requests);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed record Channel(long Id, string Title, string Description, bool Archive, bool Owner = true);
    private sealed class Client : ITelegramRequestClient
    {
        public const string Marker = "Private file storage for TeleSelfCloud. Do not add other members.";
        public Dictionary<long, Channel> Channels { get; } = new();
        private readonly Dictionary<string, int> loaded = new();
        public long Account { get; set; } = 42;
        public int Creates { get; private set; }
        public int Requests { get; private set; }
        public int Inspected { get; private set; }
        public int LoadCalls { get; private set; }
        public int LoadBatch { get; set; } = 100;
        public bool FailLoading { get; set; }
        public bool MalformedLoad { get; set; }
        public bool LoseResponse { get; set; }
        public bool WrongChat { get; set; }
        public void Add(long id, bool marked = true, bool archive = false) => Channels[id] = new(id, "Storage", marked ? Marker : "Other channel", archive);
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            var type = request["@type"]!.GetValue<string>();
            var list = request["chat_list"]?["@type"]?.GetValue<string>() ?? "chatListMain";
            var listIds = Channels.Values.Where(c => c.Archive == (list == "chatListArchive")).Select(c => c.Id).ToArray();
            JsonObject response;
            switch (type)
            {
                case "getMe": response = new() { ["id"] = Account }; break;
                case "loadChats":
                    LoadCalls++;
                    if (MalformedLoad) return Task.FromResult(new JsonObject { ["@type"] = "messages" });
                    if (FailLoading) throw new IOException("offline");
                    var count = loaded.GetValueOrDefault(list);
                    if (count >= listIds.Length) throw TelegramRequestException.From(404, "All loaded");
                    loaded[list] = Math.Min(listIds.Length, count + LoadBatch);
                    response = new() { ["@type"] = "ok" }; break;
                case "getChats": response = new() { ["chat_ids"] = new JsonArray(listIds.Take(loaded.GetValueOrDefault(list)).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }; break;
                case "getChat":
                    Inspected++; var id = request["chat_id"]!.GetValue<long>();
                    response = Chat(Channels[id]); if (WrongChat) response["id"] = id - 1; break;
                case "getSupergroup":
                    var owner = Channels[-request["supergroup_id"]!.GetValue<long>()].Owner;
                    response = new() { ["member_count"] = 1, ["usernames"] = new JsonObject { ["active_usernames"] = new JsonArray() },
                        ["status"] = new JsonObject { ["@type"] = owner ? "chatMemberStatusCreator" : "chatMemberStatusMember", ["is_member"] = true } }; break;
                case "getSupergroupFullInfo": response = new() { ["description"] = Channels[-request["supergroup_id"]!.GetValue<long>()].Description }; break;
                case "createNewSupergroupChat":
                    Creates++; var channel = new Channel(-999000 - Creates, request["title"]!.GetValue<string>(), request["description"]!.GetValue<string>(), false);
                    Channels[channel.Id] = channel;
                    if (LoseResponse) throw new IOException("response lost after creation");
                    response = Chat(channel); break;
                default: throw new NotSupportedException(type);
            }
            return Task.FromResult(response);
        }
        private static JsonObject Chat(Channel c) => new() { ["id"] = c.Id, ["title"] = c.Title,
            ["type"] = new JsonObject { ["@type"] = "chatTypeSupergroup", ["is_channel"] = true, ["supergroup_id"] = -c.Id } };
    }
}
