using System.Text.Json.Nodes;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Tests;

public sealed class TelegramRecoveryTests : IDisposable
{
    private const long Chat = -100123;
    private const string Caption = "TSC-PART|1|file-a|0";
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.TelegramRecovery", Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(root, "local.bin");
    private TelegramFileTransport Transport(Source source) => new(source, Chat, Path.Combine(root, "copies"));
    private async Task SeedAsync() { Directory.CreateDirectory(root); await File.WriteAllBytesAsync(Local, [1, 2, 3]); }
    private static JsonObject Message(long id, string caption = "other", long chat = Chat) => new()
    { ["id"] = id, ["chat_id"] = chat, ["content"] = new JsonObject { ["caption"] = new JsonObject { ["text"] = caption } } };
    private static JsonObject History(params JsonNode?[] messages) => new() { ["@type"] = "messages", ["messages"] = new JsonArray(messages) };

    [Theory]
    [InlineData("missing-array")]
    [InlineData("wrong-type")]
    [InlineData("null-message")]
    [InlineData("foreign-chat")]
    [InlineData("invalid-id")]
    [InlineData("wrong-order")]
    [InlineData("stuck-cursor")]
    [InlineData("replay-newer")]
    public async Task InvalidRecoveryHistoryNeverFallsThroughToSendingNewDocument(string fault)
    {
        await SeedAsync();
        var source = new Source(Local) { History = (page, _) => fault switch
        {
            "missing-array" => new JsonObject { ["@type"] = "messages" },
            "wrong-type" => new JsonObject { ["@type"] = "error", ["messages"] = new JsonArray() },
            "null-message" => History((JsonNode?)null),
            "foreign-chat" => History(Message(10, chat: -100456)),
            "invalid-id" => History(Message(0)),
            "wrong-order" => History(Message(10), Message(11)),
            "stuck-cursor" => History(Message(10), Message(9)),
            _ => History(Message(page == 1 ? 10 : 11))
        }};
        await Assert.ThrowsAsync<InvalidDataException>(() => Transport(source).UploadStorageDocumentAsync(Local, Caption, default));
        Assert.Equal(0, source.Sends);
        Assert.Empty(source.FetchedMessages);
    }

    [Fact]
    public async Task RecoveryContinuesBeyondOneHundredPagesUntilVerifiedMatch()
    {
        await SeedAsync();
        var source = new Source(Local) { History = (page, _) => History(Message(201 - page, page == 101 ? Caption : "other")) };
        Assert.Equal("-100123/100", await Transport(source).FindAcceptedPartAsync(Local, "file-a", 0, default));
        Assert.Equal(101, source.Pages);
        Assert.Equal(new long[] { 100 }, source.FetchedMessages);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "copies")));
    }

    [Fact]
    public async Task CaptionMatchesRequireExactContentAndBoundaryMessageIsNotFetchedTwice()
    {
        await SeedAsync();
        var wrong = Path.Combine(root, "wrong.bin");
        await File.WriteAllBytesAsync(wrong, [1, 2, 4]);
        var source = new Source(Local) { History = (page, _) => page == 1
            ? History(Message(10, Caption), Message(9, Caption)) : History(Message(9, Caption), Message(8, Caption)),
            PathForMessage = id => id == 8 ? Local : wrong };
        Assert.Equal("-100123/8", await Transport(source).FindAcceptedPartAsync(Local, "file-a", 0, default));
        Assert.Equal(new long[] { 10, 9, 8 }, source.FetchedMessages);
        Assert.Equal(0, source.Sends);
    }

    [Fact]
    public async Task CancellationOnEmptyPageDoesNotMeanNoExistingDocument()
    {
        await SeedAsync();
        using var cancel = new CancellationTokenSource();
        var source = new Source(Local) { History = (_, _) => { cancel.Cancel(); return History(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Transport(source).UploadStorageDocumentAsync(Local, Caption, cancel.Token));
        Assert.Equal(0, source.Sends);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadRejectsForeignMessageOrFileResponseBeforeCopying(bool messageMismatch)
    {
        await SeedAsync();
        var source = new Source(Local) { WrongMessage = messageMismatch, WrongFile = !messageMismatch };
        await Assert.ThrowsAsync<InvalidDataException>(() => Transport(source).DownloadPartAsync("-100123/10", default));
        Assert.Equal(messageMismatch ? 0 : 1, source.Downloads);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "copies")));
    }

    [Theory]
    [InlineData("-100123/0")]
    [InlineData("-100123/-7")]
    [InlineData("-100123/010")]
    public async Task DownloadRejectsNoncanonicalLocatorWithoutTdlibRequest(string locator)
    {
        await SeedAsync();
        var source = new Source(Local);
        await Assert.ThrowsAsync<InvalidDataException>(() => Transport(source).DownloadPartAsync(locator, default));
        Assert.Empty(source.FetchedMessages);
    }

    [Fact(Timeout = 10000)]
    public async Task SameCaptionFromAnotherSendDoesNotConfirmThisUpload()
    {
        await SeedAsync();
        var source = new Source(Local) { SendUpdates = caption => [Succeeded(999, -99, caption), Succeeded(456, -7, caption)] };
        Assert.Equal("-100123/456", await Transport(source).UploadPartAsync(Local, "file-a", 0, default));
        Assert.Equal(0, source.Subscribers);
    }

    [Theory(Timeout = 10000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidConfirmationKeepsUploadUnconfirmedAndRemovesSubscription(bool wrongCaption)
    {
        await SeedAsync();
        var source = new Source(Local) { SendUpdates = caption => [Succeeded(wrongCaption ? 456 : -456, -7, wrongCaption ? "other" : caption)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => Transport(source).UploadPartAsync(Local, "file-a", 0, default));
        Assert.Equal(0, source.Subscribers);
    }

    [Fact(Timeout = 10000)]
    public async Task InclusiveOldestBoundaryAllowsNewDocumentAfterVerifiedHistoryEnd()
    {
        await SeedAsync();
        var source = new Source(Local) { History = (_, _) => History(Message(10)) };
        Assert.Equal("-100123/456", await Transport(source).UploadStorageDocumentAsync(Local, Caption, default));
        Assert.Equal(2, source.Pages);
        Assert.Equal(1, source.Sends);
    }

    [Fact(Timeout = 10000)]
    public async Task PendingUploadIsNotReusedOrDuplicatedBeforeAcknowledgment()
    {
        await SeedAsync();
        var source = new Source(Local) { History = (_, _) =>
        {
            var message = Message(10, Caption);
            message["sending_state"] = new JsonObject { ["@type"] = "messageSendingStatePending" };
            return History(message);
        }};
        await Assert.ThrowsAsync<IOException>(() => Transport(source).UploadStorageDocumentAsync(Local, Caption, default));
        Assert.Equal(0, source.Sends);
        Assert.Empty(source.FetchedMessages);
    }

    [Fact(Timeout = 10000)]
    public async Task EarlyUpdateOverflowReportsAmbiguityInsteadOfWaitingForTwoHours()
    {
        await SeedAsync();
        var source = new Source(Local) { SendUpdates = caption => Enumerable.Range(0, 129).Select(i => Succeeded(1000 + i, -1000 - i, caption)).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => Transport(source).UploadPartAsync(Local, "file-a", 0, default));
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public async Task DownloadNeverUsesPendingMessageEvenIfTdlibAlreadyHasLocalBytes()
    {
        await SeedAsync();
        var source = new Source(Local) { PendingMessage = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => Transport(source).DownloadPartAsync("-100123/10", default));
        Assert.Equal(0, source.Downloads);
    }

    [Fact(Timeout = 10000)]
    public async Task FailedOldSendDoesNotBlockNewAttemptOrBecomeAcceptedPart()
    {
        await SeedAsync();
        var source = new Source(Local) { History = (_, _) =>
        {
            var message = Message(10, Caption);
            message["sending_state"] = new JsonObject { ["@type"] = "messageSendingStateFailed" };
            return History(message);
        }};
        Assert.Equal("-100123/456", await Transport(source).UploadStorageDocumentAsync(Local, Caption, default));
        Assert.Equal(1, source.Sends);
        Assert.Empty(source.FetchedMessages);
    }

    private static JsonObject Succeeded(long id, long oldId, string caption) => new()
    { ["@type"] = "updateMessageSendSucceeded", ["old_message_id"] = oldId, ["message"] = Message(id, caption) };

    private sealed class Source(string path) : ITelegramUpdateSource
    {
        private EventHandler<JsonObject>? updates;
        public int Subscribers { get; private set; }
        public event EventHandler<JsonObject>? UpdateReceived { add { updates += value; Subscribers++; } remove { updates -= value; Subscribers--; } }
        public Func<int, long, JsonObject> History { get; init; } = (_, _) => TelegramRecoveryTests.History();
        public Func<long, string>? PathForMessage { get; init; }
        public Func<string, JsonObject[]> SendUpdates { get; init; } = caption => [Succeeded(456, -7, caption)];
        public bool WrongMessage { get; init; }
        public bool WrongFile { get; init; }
        public bool PendingMessage { get; init; }
        public int Pages { get; private set; }
        public int Sends { get; private set; }
        public int Downloads { get; private set; }
        public List<long> FetchedMessages { get; } = [];
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            switch (request["@type"]!.GetValue<string>())
            {
                case "getChatHistory": return Task.FromResult(History(++Pages, request["from_message_id"]!.GetValue<long>()));
                case "getMessage":
                    var id = request["message_id"]!.GetValue<long>(); FetchedMessages.Add(id);
                    return Task.FromResult(new JsonObject { ["id"] = id, ["chat_id"] = WrongMessage ? -100456 : Chat,
                        ["sending_state"] = PendingMessage ? new JsonObject { ["@type"] = "messageSendingStatePending" } : null,
                        ["content"] = new JsonObject { ["document"] = new JsonObject { ["document"] = new JsonObject { ["id"] = (int)id } } } });
                case "downloadFile":
                    Downloads++; var fileId = request["file_id"]!.GetValue<int>();
                    return Task.FromResult(new JsonObject { ["id"] = WrongFile ? fileId + 1 : fileId,
                        ["local"] = new JsonObject { ["is_downloading_completed"] = true, ["path"] = PathForMessage?.Invoke(fileId) ?? path } });
                case "sendMessage":
                    Sends++; var caption = request["input_message_content"]!["caption"]!["text"]!.GetValue<string>();
                    foreach (var update in SendUpdates(caption)) updates?.Invoke(this, update);
                    return Task.FromResult(new JsonObject { ["@type"] = "message", ["id"] = -7L, ["chat_id"] = Chat });
                default: throw new InvalidOperationException("Unexpected fixture request");
            }
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
