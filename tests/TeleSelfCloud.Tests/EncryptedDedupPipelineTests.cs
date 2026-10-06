using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class EncryptedDedupPipelineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.EncryptedDedupPipeline", Guid.NewGuid().ToString("N"));
    private const string OldPassphrase = "old encrypted candidate recovery phrase";
    private const string NewPassphrase = "new upload recovery phrase 2026";
    private sealed class Capability : IUploadCapabilityProvider
    {
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken token) => Task.FromResult<UploadCapability?>(new("42", 1000, DateTimeOffset.UtcNow, "fixture"));
    }
    private UploadPipeline Pipeline(IManifestStore store, TelegramFileTransport transport, TelegramManifestPublisher publisher, IUploadDeduplication? dedup = null) =>
        new(new FileTransferCoordinator(), new Capability(), transport, store, publisher, dedup);

    [Fact]
    public async Task EncryptedCopyRewrapsRecoveryKeyReopensCopiesIndependentlyAndRestoresLogicalBytes()
    {
        Directory.CreateDirectory(root); var source = Path.Combine(root, "source.bin"); var plaintext = "ABCDEF"u8.ToArray(); File.WriteAllBytes(source, plaintext);
        var world = new DedupOwnershipAcceptanceTests.World(root); var transport = new TelegramFileTransport(world, -100, Path.Combine(root, "downloads"));
        var db = Path.Combine(root, "db"); var store = new SqliteManifestStore(db); var publisher = new TelegramManifestPublisher(transport, Path.Combine(root, "publication"));
        var original = await Pipeline(store, transport, publisher).UploadAsync(source, Path.Combine(root, "staging"), 100, default, true, recoveryPassphrase: OldPassphrase);
        var localSends = world.LocalSends; var copies = world.IdSends;
        var staged = await Pipeline(store, transport, publisher, new TelegramUploadDeduplication(store, world, transport, "42", (_, _) => Task.FromResult<string?>(OldPassphrase)))
            .StageForUploadAsync(source, Path.Combine(root, "staging"), 2, default, true, recoveryPassphrase: NewPassphrase);
        Assert.Equal(original.Parts.Count, staged.Parts.Count); Assert.Equal(original.PartSizeBytes, staged.PartSizeBytes);
        Assert.All(staged.Parts, part => { Assert.NotNull(part.CopySource); Assert.Null(part.RemoteId); Assert.False(part.Confirmed); Assert.True(File.Exists(part.StagingPath)); });
        Assert.Equal(localSends, world.LocalSends); Assert.Equal(copies, world.IdSends);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging", "encrypted-preparation"), "*.bin"));
        Assert.NotEqual(original.Encryption!.RecoveryKey.Salt, staged.Encryption!.RecoveryKey.Salt);
        var newKey = AesGcmFileCipher.UnwrapFileKey(staged.Encryption.RecoveryKey, NewPassphrase);
        try { await Assert.ThrowsAnyAsync<CryptographicException>(() => Task.Run(() => AesGcmFileCipher.UnwrapFileKey(staged.Encryption.RecoveryKey, OldPassphrase))); }
        finally { CryptographicOperations.ZeroMemory(newKey); }

        var reopenedStore = new SqliteManifestStore(db);
        var reopened = await Pipeline(reopenedStore, transport, publisher, new TelegramUploadDeduplication(reopenedStore, world, transport, "42"))
            .ResumeAsync(staged.FileId, default);
        Assert.True(reopened.Committed); Assert.Equal(copies + staged.Parts.Count, world.IdSends); Assert.Equal(localSends + 1, world.LocalSends);
        Assert.All(reopened.Parts, part => { Assert.Null(part.CopySource); Assert.True(part.Confirmed); });
        Assert.DoesNotContain(original.Parts.Select(p => p.RemoteId), p => reopened.Parts.Any(other => other.RemoteId == p));

        var restoredPath = Path.Combine(root, "restored.bin");
        await new FileTransferCoordinator(transport, (_, _) => Task.FromResult(NewPassphrase)).ReassembleAsync(reopened, restoredPath, default);
        Assert.Equal(plaintext, File.ReadAllBytes(restoredPath));
    }

    [Fact]
    public async Task WrongExistingPassphraseSkipsCandidateAndKeepsOrdinaryEncryptedStaging()
    {
        Directory.CreateDirectory(root); var source = Path.Combine(root, "source.bin"); File.WriteAllBytes(source, "ABCDEF"u8.ToArray());
        var world = new DedupOwnershipAcceptanceTests.World(root); var transport = new TelegramFileTransport(world, -100, Path.Combine(root, "downloads"));
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var publisher = new TelegramManifestPublisher(transport, Path.Combine(root, "publication"));
        await Pipeline(store, transport, publisher).UploadAsync(source, Path.Combine(root, "staging"), 100, default, true, recoveryPassphrase: OldPassphrase);
        var sends = world.LocalSends; var copies = world.IdSends;
        var staged = await Pipeline(store, transport, publisher, new TelegramUploadDeduplication(store, world, transport, "42", (_, _) => Task.FromResult<string?>("incorrect recovery phrase")))
            .StageForUploadAsync(source, Path.Combine(root, "staging"), 100, default, true, recoveryPassphrase: NewPassphrase);
        Assert.All(staged.Parts, part => { Assert.Null(part.CopySource); Assert.Null(part.RemoteId); Assert.True(File.Exists(part.StagingPath)); });
        Assert.Equal(sends, world.LocalSends); Assert.Equal(copies, world.IdSends);
        var resumed = await Pipeline(store, transport, publisher).ResumeAsync(staged.FileId, default);
        Assert.True(resumed.Committed); Assert.Equal(sends + staged.Parts.Count + 1, world.LocalSends); Assert.Equal(copies, world.IdSends);
    }

    [Fact]
    public async Task EncryptedCopyFallbackKeepsNewRecoveryKeyAndUploadsOnlyAfterOrdinaryResume()
    {
        Directory.CreateDirectory(root); var source = Path.Combine(root, "source.bin"); var bytes = "ABCDEF"u8.ToArray(); File.WriteAllBytes(source, bytes);
        var world = new DedupOwnershipAcceptanceTests.World(root); var transport = new TelegramFileTransport(world, -100, Path.Combine(root, "downloads"));
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var publisher = new TelegramManifestPublisher(transport, Path.Combine(root, "publication"));
        await Pipeline(store, transport, publisher).UploadAsync(source, Path.Combine(root, "staging"), 100, default, true, recoveryPassphrase: OldPassphrase);
        var copies = world.IdSends;
        var staged = await Pipeline(store, transport, publisher, new TelegramUploadDeduplication(store, world, transport, "42", (_, _) => Task.FromResult<string?>(OldPassphrase)))
            .StageForUploadAsync(source, Path.Combine(root, "staging"), 2, default, true, recoveryPassphrase: NewPassphrase);
        var progress = await Pipeline(store, transport, publisher).SwitchPendingCopiesToUploadAsync(staged.FileId, default);
        Assert.All(progress.Parts, part => { Assert.Null(part.CopySource); Assert.False(part.Confirmed); Assert.Null(part.RemoteId); }); Assert.Equal(copies, world.IdSends);
        var localSends = world.LocalSends;
        var complete = await Pipeline(store, transport, publisher).ResumeAsync(staged.FileId, default);
        Assert.True(complete.Committed); Assert.Equal(copies, world.IdSends); Assert.Equal(localSends + complete.Parts.Count + 1, world.LocalSends);
        var restoredPath = Path.Combine(root, "restored.bin");
        await new FileTransferCoordinator(transport, (_, _) => Task.FromResult(NewPassphrase)).ReassembleAsync(complete, restoredPath, default);
        Assert.Equal(bytes, File.ReadAllBytes(restoredPath));
    }

    [Fact]
    public async Task NoEncryptedCandidateLeavesTheNewCiphertextPlanOrdinary()
    {
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var transportClient = new EmptyRequests();
        var transport = new TelegramFileTransport(transportClient, -100, Path.Combine(root, "downloads"));
        using var key = new MemoryStream(); var raw = RandomNumberGenerator.GetBytes(32);
        PassphraseKeyEnvelope envelope;
        try { envelope = AesGcmFileCipher.WrapFileKey(raw, NewPassphrase); }
        finally { CryptographicOperations.ZeroMemory(raw); }
        var invalid = new FileManifest(1, "bad", "bad.bin", 1, new string('A', 64), 1, [new(0, 0, 1, new string('A', 64), null, false)], false, "42",
            Encryption: new(1, 1, new string('B', 64), envelope));
        var service = new TelegramUploadDeduplication(store, transportClient, transport, "42", (_, _) => Task.FromResult<string?>(OldPassphrase));
        Assert.Null(await service.PlanEncryptedAsync(invalid, 100, NewPassphrase, default));
    }
    private sealed class EmptyRequests : ITelegramUpdateSource
    {
        public event EventHandler<System.Text.Json.Nodes.JsonObject>? UpdateReceived { add { } remove { } }
        public Task<System.Text.Json.Nodes.JsonObject> ExecuteAsync(System.Text.Json.Nodes.JsonObject request, CancellationToken token = default) => throw new InvalidOperationException("No request expected.");
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
