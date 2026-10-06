using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Tests;

public sealed class RemoteContentDedupVerifierTests
{
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static FileManifest Candidate() => new(1, "candidate", "Private.txt", 6, Hash("ABCDEF"), 3,
        [new(0, 0, 3, Hash("ABC"), "-100/10", true), new(1, 3, 3, Hash("DEF"), "-100/11", true)], true, "42");
    private sealed class Remote(string? damage = null) : ITelegramRequestClient, IPartTransport
    {
        public int Requests, Downloads, Uploads; public List<OwnedStream> Streams { get; } = [];
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests++; Assert.Equal("getMessage", request["@type"]!.GetValue<string>()); Assert.Equal(-100, request["chat_id"]!.GetValue<long>());
            var id = request["message_id"]!.GetValue<long>(); var index = id == 10 ? 0 : 1;
            return Task.FromResult(new JsonObject { ["@type"] = damage == "envelope" ? "error" : "message", ["sending_state"] = damage == "pending" ? new JsonObject { ["@type"] = "messageSendingStatePending" } : null,
                ["scheduling_state"] = damage == "scheduled" ? new JsonObject { ["@type"] = "messageSchedulingStateSendAtDate" } : null,
                ["id"] = damage == "message-id" ? id + 1 : id, ["chat_id"] = damage == "message-chat" ? -200L : -100L,
                ["content"] = new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = damage == "caption" ? "TSC-PART|1|other|0" : $"TSC-PART|1|candidate|{index}" },
                    ["document"] = new JsonObject { ["document"] = new JsonObject { ["id"] = 7, ["size"] = 3,
                        ["remote"] = new JsonObject { ["unique_id"] = damage == "unknown-id" ? "" : damage == "changed" && Requests > 1 ? "changed" : "stable", ["is_uploading_completed"] = damage != "incomplete" } } } } });
        }
        public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Downloads++; var text = remoteId.EndsWith("/10") ? "ABC" : "DEF";
            if (damage == "short") text = text[..2]; if (damage == "extra") text += "G"; if (damage == "hash") text = "BAD";
            var stream = new OwnedStream(Encoding.UTF8.GetBytes(text)); Streams.Add(stream); return Task.FromResult<Stream>(stream);
        }
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken cancellationToken) { Uploads++; throw new InvalidOperationException(); }
    }
    private sealed class OwnedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Closed { get; private set; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    [Fact]
    public async Task ActualMultipartBytesAndIdentityVerifyWithoutUploadingOrPublishing()
    {
        var remote = new Remote(); Assert.True(await new RemoteContentDedupVerifier(remote, remote, "42", -100).VerifyAsync(Candidate(), Hash("ABCDEF"), 6, default));
        Assert.Equal(4, remote.Requests); Assert.Equal(2, remote.Downloads); Assert.Equal(0, remote.Uploads); Assert.All(remote.Streams, s => Assert.True(s.Closed));
    }
    [Theory]
    [InlineData("account")]
    [InlineData("locator")]
    public async Task ForeignScopeFailsBeforeAnyRemoteRequest(string damage)
    {
        var candidate = Candidate(); if (damage == "account") candidate = candidate with { AccountId = "99" };
        else candidate = candidate with { Parts = candidate.Parts.Select(p => p with { RemoteId = "-200/" + (10 + p.Index) }).ToArray() };
        var remote = new Remote(); await Assert.ThrowsAsync<InvalidDataException>(() => new RemoteContentDedupVerifier(remote, remote, "42", -100).VerifyAsync(candidate, Hash("ABCDEF"), 6, default)); Assert.Equal(0, remote.Requests);
    }
    [Theory]
    [InlineData("message-id")]
    [InlineData("message-chat")]
    [InlineData("caption")]
    [InlineData("envelope")]
    [InlineData("pending")]
    [InlineData("scheduled")]
    [InlineData("unknown-id")]
    [InlineData("incomplete")]
    public async Task WrongRemoteIdentityCannotProveDuplication(string damage)
    {
        var remote = new Remote(damage); await Assert.ThrowsAsync<InvalidDataException>(() => new RemoteContentDedupVerifier(remote, remote, "42", -100).VerifyAsync(Candidate(), Hash("ABCDEF"), 6, default)); Assert.Equal(0, remote.Downloads);
    }
    [Theory]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("hash")]
    public async Task HashOrLengthMismatchRejectsCandidateAndClosesSource(string damage)
    {
        var remote = new Remote(damage); Assert.False(await new RemoteContentDedupVerifier(remote, remote, "42", -100).VerifyAsync(Candidate(), Hash("ABCDEF"), 6, default)); Assert.All(remote.Streams, s => Assert.True(s.Closed));
    }
    [Fact]
    public async Task PartHashesAloneDoNotProveLogicalContentAndTrashCannotBeReused()
    {
        var remote = new Remote(); var verifier = new RemoteContentDedupVerifier(remote, remote, "42", -100);
        Assert.False(await verifier.VerifyAsync(Candidate() with { TotalSha256 = Hash("ZZZZZZ") }, Hash("ZZZZZZ"), 6, default));
        var previous = remote.Requests; Assert.False(await verifier.VerifyAsync(Candidate() with { IsInTrash = true }, Hash("ABCDEF"), 6, default)); Assert.Equal(previous, remote.Requests);
    }
    [Fact]
    public async Task CanceledVerificationNeverProducesReuseProof()
    {
        var remote = new Remote(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemoteContentDedupVerifier(remote, remote, "42", -100).VerifyAsync(Candidate(), Hash("ABCDEF"), 6, stop.Token)); Assert.Equal(0, remote.Requests);
    }
    [Fact]
    public async Task ChangedIdentityAfterByteVerificationRejectsReuse()
    {
        var remote = new Remote("changed"); await Assert.ThrowsAsync<InvalidDataException>(() => new RemoteContentDedupVerifier(remote, remote, "42", -100).VerifyAsync(Candidate(), Hash("ABCDEF"), 6, default));
        Assert.All(remote.Streams, s => Assert.True(s.Closed)); Assert.Equal(1, remote.Downloads);
    }
}
