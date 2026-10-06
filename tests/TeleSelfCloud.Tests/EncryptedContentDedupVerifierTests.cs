using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class EncryptedContentDedupVerifierTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.EncryptedProof", Guid.NewGuid().ToString("N"));
    private const string Passphrase = "isolated encrypted proof passphrase";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed class OwnedStream(byte[] bytes, CancellationTokenSource? stop) : MemoryStream(bytes)
    {
        public bool Closed; public int MaxRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { MaxRead = Math.Max(MaxRead, buffer.Length); var count = await base.ReadAsync(buffer, token); stop?.Cancel(); return count; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class Remote(FileManifest manifest, byte[][] parts, string fault, CancellationTokenSource stop) : ITelegramUpdateSource, IPartTransport
    {
        public event EventHandler<JsonObject>? UpdateReceived { add { } remove { } }
        public int Requests, Downloads; public List<OwnedStream> Streams = [];
        private readonly Dictionary<long, int> reads = [];
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Requests++; Assert.Equal("getMessage", request["@type"]!.GetValue<string>());
            var id = request["message_id"]!.GetValue<long>(); var index = (int)(id - 10); reads[id] = reads.GetValueOrDefault(id) + 1;
            return Task.FromResult(new JsonObject { ["@type"] = "message", ["chat_id"] = -100L, ["id"] = id,
                ["content"] = new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = $"TSC-PART|1|{manifest.FileId}|{index}" },
                    ["document"] = new JsonObject { ["document"] = new JsonObject { ["id"] = index + 1, ["size"] = manifest.Parts[index].Length,
                        ["remote"] = new JsonObject { ["is_uploading_completed"] = true, ["unique_id"] = fault == "identity" && reads[id] > 1 ? "changed" : "stable-" + index } } } } });
        }
        public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Downloads++; var index = (int)(TelegramRemoteMessageId.Parse(remoteId).MessageId - 10);
            var bytes = fault == "remote-corruption" ? parts[index].Select(b => (byte)(b ^ 1)).ToArray() : parts[index];
            var stream = new OwnedStream(bytes, fault == "cancel" ? stop : null); Streams.Add(stream); return Task.FromResult<Stream>(stream);
        }
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken token) => throw new Xunit.Sdk.XunitException("Proof must be read-only.");
    }
    private async Task<(FileManifest Manifest, byte[][] Parts)> Candidate(string fault)
    {
        var logical = fault == "valid-empty" ? Array.Empty<byte>() : fault == "valid-large" ? Enumerable.Range(0, 1_100_000).Select(i => (byte)(i % 251)).ToArray() : "ABCDEF"u8.ToArray();
        var key = AesGcmFileCipher.CreateFileKey(); byte[] ciphertext; PassphraseKeyEnvelope envelope;
        try
        {
            envelope = AesGcmFileCipher.WrapFileKey(key, Passphrase); using var input = new MemoryStream(logical); using var output = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(input, output, key, frameSize: fault == "valid-large" ? 262144 : 3); ciphertext = output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        if (fault == "tag") ciphertext[^1] ^= 1;
        if (fault == "header") ciphertext[0] ^= 1;
        if (fault == "truncated") ciphertext = ciphertext[..^1];
        if (fault == "extra") ciphertext = [.. ciphertext, 0];
        if (fault == "order") { var first = ciphertext.AsSpan(33, 19).ToArray(); ciphertext.AsSpan(52, 19).CopyTo(ciphertext.AsSpan(33, 19)); first.CopyTo(ciphertext, 52); }
        var size = fault == "valid-large" ? 300000 : 31;
        var chunks = Enumerable.Range(0, (ciphertext.Length + size - 1) / size).Select(i => ciphertext.Skip(i * size).Take(size).ToArray()).ToArray();
        var parts = chunks.Select((b, i) => new PartRecord(i, i * size, b.Length, Hash(b), "-100/" + (10 + i), true)).ToArray();
        var manifest = new FileManifest(1, "encrypted-owner", "source.bin", fault == "logical-size" ? 3 : logical.Length,
            Hash(fault == "logical-hash" ? "ZZZZZZ"u8.ToArray() : logical), size, parts, true, "42",
            Encryption: new(1, ciphertext.Length, Hash(ciphertext), envelope));
        if (fault == "account") manifest = manifest with { AccountId = "99" };
        if (fault == "locator") manifest = manifest with { Parts = parts.Select(p => p with { RemoteId = "-200/" + (10 + p.Index) }).ToArray() };
        return (manifest, chunks);
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("valid-empty")]
    [InlineData("valid-large")]
    [InlineData("wrong-passphrase")]
    [InlineData("tag")]
    [InlineData("header")]
    [InlineData("truncated")]
    [InlineData("extra")]
    [InlineData("order")]
    [InlineData("logical-hash")]
    [InlineData("logical-size")]
    [InlineData("account")]
    [InlineData("locator")]
    [InlineData("identity")]
    [InlineData("remote-corruption")]
    [InlineData("cancel")]
    public async Task ProofRequiresAuthenticatedLogicalBytesScopeKeyAndStableRemoteIdentity(string fault)
    {
        Directory.CreateDirectory(root); var sentinel = Path.Combine(root, "keep.bin"); File.WriteAllBytes(sentinel, "KEEP"u8.ToArray());
        var (manifest, chunks) = await Candidate(fault); using var stop = new CancellationTokenSource(); var remote = new Remote(manifest, chunks, fault, stop);
        var temporary = Path.Combine(root, "proof"); var verifier = new EncryptedContentDedupVerifier(remote, remote, "42", -100, temporary);
        var proved = false;
        var error = await Record.ExceptionAsync(async () => proved = await verifier.VerifyAsync(manifest, manifest.TotalSha256, manifest.LogicalSize,
            fault == "wrong-passphrase" ? "wrong isolated passphrase" : Passphrase, stop.Token));
        if (fault.StartsWith("valid", StringComparison.Ordinal)) { Assert.Null(error); Assert.True(proved); Assert.Equal(manifest.Parts.Count * 2, remote.Requests); }
        else
        {
            Assert.False(proved);
            if (fault is "logical-hash" or "remote-corruption") Assert.Null(error); else Assert.NotNull(error);
            if (fault is "wrong-passphrase" or "account" or "locator") Assert.Equal(0, remote.Requests);
            if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        Assert.All(remote.Streams, stream => { Assert.True(stream.Closed); Assert.InRange(stream.MaxRead, 1, 65536); });
        if (Directory.Exists(temporary)) Assert.Empty(Directory.GetFiles(temporary));
        Assert.Equal("KEEP"u8.ToArray(), File.ReadAllBytes(sentinel));
        await Assert.ThrowsAsync<NotSupportedException>(() => new RemoteContentDedupVerifier(remote, remote, "42", -100).VerifyAsync(manifest, manifest.TotalSha256, manifest.LogicalSize, default));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTransportMustMatchSessionAndVaultBeforeAnyRequest(bool foreignChat)
    {
        var (manifest, parts) = await Candidate("valid"); using var stop = new CancellationTokenSource(); var remote = new Remote(manifest, parts, "valid", stop);
        var other = new Remote(manifest, parts, "valid", stop);
        var transport = new TelegramFileTransport(foreignChat ? remote : other, foreignChat ? -200 : -100, Path.Combine(root, "downloads"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new EncryptedContentDedupVerifier(remote, transport, "42", -100, Path.Combine(root, "proof"))
            .VerifyAsync(manifest, manifest.TotalSha256, manifest.LogicalSize, Passphrase, default));
        Assert.Equal(0, remote.Requests); Assert.Equal(0, other.Requests); Assert.False(Directory.Exists(Path.Combine(root, "proof")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
