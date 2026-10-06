using System.Text.Json.Nodes;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Tests;

public sealed class VaultMetadataKeyCreationGuardTests
{
    private const long Chat = -100;
    private static JsonObject Page(params (long Id, string Caption)[] entries) => new()
    {
        ["@type"] = "messages", ["messages"] = new JsonArray(entries.Select(e => (JsonNode?)new JsonObject
        { ["id"] = e.Id, ["chat_id"] = Chat, ["content"] = new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = e.Caption } } }).ToArray())
    };
    [Fact]
    public async Task LegacyHistoryMustReachOldestBoundaryBeforeKeyCreation()
    {
        var client = new Client(Page((30, "TSC-MANIFEST|1|file|hash")), Page((30, "TSC-MANIFEST|1|file|hash"), (10, "TSC-FOLDERS|2|42|hash")), Page((10, "TSC-FOLDERS|2|42|hash")));
        await VaultMetadataKeyCreationGuard.EnsureNoProtectedHistoryAsync(client, Chat, default);
        Assert.Equal(new long[] { 0, 30, 10 }, client.Anchors);
    }
    [Theory]
    [InlineData("TSC-MANIFEST|2|file|hash")]
    [InlineData("TSC-FOLDERS|3|42|hash")]
    [InlineData("TSC-MANIFEST|99|file|hash")]
    [InlineData("TSC-FOLDERS|2")]
    public async Task ProtectedOrUnknownMetadataInOlderHistoryRequiresRecovery(string caption)
    {
        var client = new Client(Page((30, "ordinary")), Page((20, caption)));
        await Assert.ThrowsAsync<InvalidDataException>(() => VaultMetadataKeyCreationGuard.EnsureNoProtectedHistoryAsync(client, Chat, default));
        Assert.Equal(2, client.Anchors.Count);
    }
    [Fact]
    public async Task MalformedForeignOrNonAdvancingHistoryCannotEstablishAbsence()
    {
        var foreign = Page((20, "ordinary")); foreign["messages"]![0]!["chat_id"] = -101;
        foreach (var page in new[] { new JsonObject { ["messages"] = new JsonArray() }, foreign, Page((20, "a"), (20, "b")), Page((0, "a")) })
            await Assert.ThrowsAsync<InvalidDataException>(() => VaultMetadataKeyCreationGuard.EnsureNoProtectedHistoryAsync(new Client(page), Chat, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => VaultMetadataKeyCreationGuard.EnsureNoProtectedHistoryAsync(new Client(Page((10, "a")), Page((20, "b"))), Chat, default));
    }
    [Fact]
    public async Task CancellationAfterEmptyResponseCannotEstablishAbsence()
    {
        using var cancel = new CancellationTokenSource();
        var client = new Client(Page()) { AfterResponse = () => cancel.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VaultMetadataKeyCreationGuard.EnsureNoProtectedHistoryAsync(client, Chat, cancel.Token));
    }
    private sealed class Client(params JsonObject[] pages) : ITelegramRequestClient
    {
        private int index;
        public List<long> Anchors { get; } = [];
        public Action? AfterResponse { get; init; }
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken token)
        {
            Assert.Equal("getChatHistory", request["@type"]!.ToString());
            Assert.Equal(Chat, request["chat_id"]!.GetValue<long>());
            Anchors.Add(request["from_message_id"]!.GetValue<long>());
            var response = index < pages.Length ? pages[index++] : Page(); AfterResponse?.Invoke(); return Task.FromResult(response);
        }
    }
}
