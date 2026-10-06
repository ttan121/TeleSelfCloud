using System.Security.Cryptography;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class MetadataKeyLifetimeTests
{
    [Fact]
    public void MissingPolicyRecoveryCannotReplaceAnExistingMetadataKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MissingPolicy", Guid.NewGuid().ToString("N"));
        using var original = VaultMetadataKey.Create("42", -100); using var replacement = VaultMetadataKey.Create("42", -100);
        try
        {
            VaultMetadataKeyStore.Save(root, original); File.Delete(Path.Combine(root, "metadata-key-policy.json"));
            var path = Path.Combine(root, "metadata-key.dpapi.json"); var before = File.ReadAllBytes(path);
            Assert.Throws<InvalidDataException>(() => VaultMetadataKeyStore.Save(root, replacement, recoverExisting: true));
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(File.Exists(Path.Combine(root, "metadata-key-policy.json")));
            VaultMetadataKeyStore.Save(root, original, recoverExisting: true); using var loaded = VaultMetadataKeyStore.Load(root, "42", -100);
            Assert.Equal(original.ScopeProof(), loaded!.ScopeProof());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void CorruptRecordWithMissingPolicyRequiresProfileRepairBeforeRecoveryWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MissingPolicy", Guid.NewGuid().ToString("N"));
        using var original = VaultMetadataKey.Create("42", -100);
        try
        {
            VaultMetadataKeyStore.Save(root, original); File.Delete(Path.Combine(root, "metadata-key-policy.json"));
            var path = Path.Combine(root, "metadata-key.dpapi.json"); File.WriteAllText(path, "damaged record");
            Assert.Throws<System.Text.Json.JsonException>(() => VaultMetadataKeyStore.Save(root, original, recoverExisting: true));
            Assert.Equal("damaged record", File.ReadAllText(path)); Assert.False(File.Exists(Path.Combine(root, "metadata-key-policy.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task OwnedSnapshotRetainsExactScopeAndReplayAfterOriginalIsDisposed()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.KeySnapshot", Guid.NewGuid().ToString("N"));
        using var original = VaultMetadataKey.Create("42", -100); using var snapshot = original.Clone(); var proof = original.ScopeProof();
        original.Dispose();
        try
        {
            Assert.Equal(proof, snapshot.ScopeProof()); Assert.Throws<ObjectDisposedException>(() => original.Clone());
            var backup = snapshot.ExportBackup("snapshot recovery passphrase"); using var recovered = VaultMetadataKey.Recover(backup, "42", -100, "snapshot recovery passphrase");
            Assert.Equal(proof, recovered.ScopeProof());
            var cipher = await snapshot.ProtectForReplayAsync(root, "manifest", "id", [1, 2, 3], default);
            Assert.Equal(new byte[] { 1, 2, 3 }, recovered.Unprotect("manifest", "id", cipher));
            Assert.Equal(cipher, await recovered.ProtectForReplayAsync(root, "manifest", "id", [1, 2, 3], default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ExportAndDisposeRaceEitherRejectsOrRecoversExactOriginalKey(int iteration)
    {
        using var key = VaultMetadataKey.Create("42", -100); var expected = key.ExportKeyBytes();
        try
        {
            var work = Task.Run(() => key.ExportBackup("racing recovery passphrase"));
            if (iteration != 0) await Task.Delay(15 * iteration);
            key.Dispose();
            VaultMetadataBackup backup;
            try { backup = await work; } catch (ObjectDisposedException) { return; }
            using var recovered = VaultMetadataKey.Recover(backup, "42", -100, "racing recovery passphrase");
            var actual = recovered.ExportKeyBytes();
            try { Assert.Equal(expected, actual); } finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }
}
