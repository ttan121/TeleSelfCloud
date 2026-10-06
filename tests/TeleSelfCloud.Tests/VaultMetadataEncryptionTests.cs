using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class VaultMetadataEncryptionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MetadataCrypto", Guid.NewGuid().ToString("N"));
    private const string Passphrase = "metadata recovery test only";
    [Fact]
    public void EnvelopeHidesMetadataAndAuthenticatesContextWithFreshNonces()
    {
        using var key = VaultMetadataKey.Create("42", -100);
        var plaintext = Encoding.UTF8.GetBytes("Secret report / Finance / account-password.txt");
        var first = key.Protect("manifest", "file", plaintext);
        var second = key.Protect("manifest", "file", plaintext);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("Secret report", Encoding.UTF8.GetString(first));
        Assert.Equal(plaintext, key.Unprotect("manifest", "file", first));
        Assert.ThrowsAny<CryptographicException>(() => key.Unprotect("manifest", "other-file", first));
        Assert.ThrowsAny<CryptographicException>(() => key.Unprotect("folders", "file", first));
        var bytes = key.ExportKeyBytes();
        try
        {
            using var foreign = new VaultMetadataKey("43", -100, key.KeyId, bytes);
            using var vault = new VaultMetadataKey("42", -101, key.KeyId, bytes);
            Assert.ThrowsAny<CryptographicException>(() => foreign.Unprotect("manifest", "file", first));
            Assert.ThrowsAny<CryptographicException>(() => vault.Unprotect("manifest", "file", first));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    [Theory]
    [InlineData("nonce")]
    [InlineData("salt")]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    public void TamperedEncryptedFieldsNeverReturnPlaintext(string field)
    {
        using var key = VaultMetadataKey.Create("42", -100);
        var envelope = JsonNode.Parse(key.Protect("manifest", "file", "sensitive"u8))!;
        var bytes = Convert.FromBase64String(envelope[field]!.GetValue<string>()); bytes[0] ^= 1;
        envelope[field] = Convert.ToBase64String(bytes);
        Assert.ThrowsAny<CryptographicException>(() => key.Unprotect("manifest", "file", Encoding.UTF8.GetBytes(envelope.ToJsonString())));
    }
    [Fact]
    public async Task PersistedOutboxReplaysExactCiphertextAndKeepsCorruptFiles()
    {
        using var key = VaultMetadataKey.Create("42", -100);
        var plaintext = Encoding.UTF8.GetBytes("Sensitive metadata");
        var first = await key.ProtectForReplayAsync(root, "manifest", "file", plaintext, default);
        var replay = await key.ProtectForReplayAsync(root, "manifest", "file", plaintext, default);
        Assert.Equal(first, replay);
        var changed = await key.ProtectForReplayAsync(root, "manifest", "file", "Changed"u8.ToArray(), default);
        Assert.NotEqual(first, changed);
        Assert.Equal(2, Directory.GetFiles(root, "*.json").Length);
        var path = Directory.GetFiles(root, "*.json").Single(p => File.ReadAllBytes(p).AsSpan().SequenceEqual(first));
        await File.WriteAllTextAsync(path, "corrupt");
        await Assert.ThrowsAsync<JsonException>(() => key.ProtectForReplayAsync(root, "manifest", "file", plaintext, default));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public void PassphraseRecoveryWorksWithoutLocalDpapiAndRejectsWrongScopeOrHeaderEdits()
    {
        using var key = VaultMetadataKey.Create("42", -100);
        var backup = key.ExportBackup(Passphrase);
        using var recovered = VaultMetadataKey.Recover(backup, "42", -100, Passphrase);
        var ciphertext = key.Protect("folders", "42", "private folder names"u8);
        Assert.Equal("private folder names"u8.ToArray(), recovered.Unprotect("folders", "42", ciphertext));
        Assert.ThrowsAny<CryptographicException>(() => VaultMetadataKey.Recover(backup, "42", -100, "wrong passphrase long enough"));
        Assert.Throws<InvalidDataException>(() => VaultMetadataKey.Recover(backup, "43", -100, Passphrase));
        Assert.ThrowsAny<CryptographicException>(() => VaultMetadataKey.Recover(backup with { KeyId = Guid.NewGuid().ToString("N") }, "42", -100, Passphrase));
        var serialized = JsonSerializer.Serialize(backup);
        Assert.DoesNotContain(Convert.ToBase64String(key.ExportKeyBytes()), serialized);
    }
    [Fact]
    public void DpapiRecordAndPolicyRejectReplacementAndRecoverSameKeyAfterCorruption()
    {
        using var key = VaultMetadataKey.Create("42", -100);
        VaultMetadataKeyStore.Save(root, key);
        using var loaded = VaultMetadataKeyStore.Load(root, "42", -100)!;
        Assert.Equal(key.KeyId, loaded.KeyId);
        using var other = VaultMetadataKey.Create("42", -100);
        Assert.Throws<InvalidOperationException>(() => VaultMetadataKeyStore.Save(root, other));
        Assert.Throws<InvalidDataException>(() => VaultMetadataKeyStore.Save(root, other, recoverExisting: true));
        var path = Path.Combine(root, "metadata-key.dpapi.json");
        File.WriteAllText(path, "damaged record");
        Assert.Throws<JsonException>(() => VaultMetadataKeyStore.Load(root, "42", -100));
        Assert.True(VaultMetadataKeyStore.IsConfigured(root));
        using var restored = VaultMetadataKey.Recover(key.ExportBackup(Passphrase), "42", -100, Passphrase);
        VaultMetadataKeyStore.Save(root, restored, recoverExisting: true);
        using var reopened = VaultMetadataKeyStore.Load(root, "42", -100)!;
        Assert.Equal(key.KeyId, reopened.KeyId);
        Assert.Equal("damaged record", File.ReadAllText(Directory.GetFiles(root, "*.backup").Single()));
    }
    [Fact]
    public void MissingDpapiWithPolicyNeverCreatesAFreshReplacement()
    {
        using var key = VaultMetadataKey.Create("42", -100);
        VaultMetadataKeyStore.Save(root, key);
        File.Delete(Path.Combine(root, "metadata-key.dpapi.json"));
        Assert.True(VaultMetadataKeyStore.IsConfigured(root));
        Assert.Throws<FileNotFoundException>(() => VaultMetadataKeyStore.Load(root, "42", -100));
        using var other = VaultMetadataKey.Create("42", -100);
        Assert.Throws<InvalidOperationException>(() => VaultMetadataKeyStore.Save(root, other));
        VaultMetadataKeyStore.Save(root, key, recoverExisting: true);
        using var loaded = VaultMetadataKeyStore.Load(root, "42", -100)!;
        Assert.Equal(key.KeyId, loaded.KeyId);
    }
    [Fact]
    public void DisposedKeysAndOversizedPlaintextFailBeforePublishing()
    {
        var key = VaultMetadataKey.Create("42", -100);
        Assert.Throws<InvalidDataException>(() => key.Protect("manifest", "file", new byte[12 * 1024 * 1024]));
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.Protect("manifest", "file", "secret"u8));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
