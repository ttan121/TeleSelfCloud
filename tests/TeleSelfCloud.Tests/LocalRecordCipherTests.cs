using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalRecordCipherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LocalRecords", Guid.NewGuid().ToString("N"));
    private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string scope = Guid.NewGuid().ToString("N");
    private LocalRecordCipher Cipher(string? encoded = null, string? id = null, string purpose = "cache-verification", string identity = "state") => new(encoded ?? key, id ?? scope, purpose, identity);
    [Fact]
    public void AuthenticatesPurposeIdentityScopeKeyAndFreshHeader()
    {
        using var cipher = Cipher(); var plain = "Private path / sensitive-name.txt"u8.ToArray();
        var first = cipher.Protect(plain); var second = cipher.Protect(plain); Assert.NotEqual(first, second);
        Assert.Equal(plain, cipher.Unprotect(first)); Assert.DoesNotContain("sensitive-name", Encoding.UTF8.GetString(first));
        using var otherScope = Cipher(id: Guid.NewGuid().ToString("N")); using var otherPurpose = Cipher(purpose: "sync-report");
        using var otherIdentity = Cipher(identity: "other"); using var otherKey = Cipher(encoded: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        foreach (var other in new[] { otherScope, otherPurpose, otherIdentity, otherKey }) Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(first));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(40)]
    [InlineData(52)]
    [InlineData(60)]
    [InlineData(78)]
    public void ChangedHeaderCiphertextOrTagNeverReturnsPlaintext(int offset)
    {
        using var cipher = Cipher(); var bytes = cipher.Protect([1, 2, 3]); bytes[offset] ^= 1;
        var error = Record.Exception(() => cipher.Unprotect(bytes)); Assert.True(error is InvalidDataException or CryptographicException);
    }
    [Fact]
    public void RejectsPlainTruncatedTrailingAndDisposedRecords()
    {
        using var cipher = Cipher(); var bytes = cipher.Protect([]);
        Assert.Empty(cipher.Unprotect(bytes)); Assert.Throws<InvalidDataException>(() => cipher.Unprotect("{}"u8));
        Assert.Throws<InvalidDataException>(() => cipher.Unprotect(bytes[..^1])); Assert.Throws<InvalidDataException>(() => cipher.Unprotect(bytes.Concat(new byte[] { 0 }).ToArray()));
        cipher.Dispose(); Assert.Throws<ObjectDisposedException>(() => cipher.Protect([])); Assert.Throws<ObjectDisposedException>(() => cipher.Unprotect(bytes));
    }
    [Fact]
    public async Task ProtectedCachePreservesExactVerificationAndRejectsWrongKeyWithoutMutation()
    {
        Directory.CreateDirectory(root); var part = Path.Combine(root, "Private-part.bin"); await File.WriteAllBytesAsync(part, [1, 2, 3]);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));
        var manifest = new FileManifest(1, "Private-id", "Private-name", 3, hash, 3, [new(0, 0, 3, hash, null, false, part)], false, "42");
        var path = Path.Combine(root, "local-cache-verifications.json"); using var cipher = Cipher();
        var store = new LocalCacheVerificationStore(path, cipher); var result = await store.VerifyAndSaveAsync(manifest, default);
        Assert.Equal(LocalCacheIntegrityState.AvailableOffline, result.State);
        var saved = Assert.Single(await store.LoadAllAsync(default)).Value; Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(saved));
        var before = File.ReadAllBytes(path); Assert.DoesNotContain("Private-id", Encoding.UTF8.GetString(before));
        using var wrong = Cipher(id: Guid.NewGuid().ToString("N")); var foreign = new LocalCacheVerificationStore(path, wrong);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => foreign.RemoveManyAsync(["Private-id"], default)); Assert.Equal(before, File.ReadAllBytes(path));
        await Assert.ThrowsAsync<JsonException>(() => new LocalCacheVerificationStore(path).LoadAllAsync(default)); Assert.Equal(before, File.ReadAllBytes(path));
        await store.RemoveManyAsync(["Private-id"], default); Assert.Empty(await store.LoadAllAsync(default)); Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public async Task ProtectedReaderDoesNotConvertLegacyPlainStateOrWriteAfterAuthenticationFailure()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cache.json"); File.WriteAllText(path, "{}"); var before = File.ReadAllBytes(path);
        using var cipher = Cipher(); var store = new LocalCacheVerificationStore(path, cipher);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveManyAsync(["id"], default)); Assert.Equal(before, File.ReadAllBytes(path));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
