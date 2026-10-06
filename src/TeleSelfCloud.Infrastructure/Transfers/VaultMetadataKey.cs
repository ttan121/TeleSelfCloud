using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed record VaultMetadataBackup(int Version, string AccountId, long ChatId, string KeyId,
    PassphraseKeyEnvelope RecoveryKey, string Proof);

public sealed class VaultMetadataKey : IDisposable
{
    private const int MaxPlaintext = 12 * 1024 * 1024 - 4096;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record Envelope(int Version, string Cipher, string KeyId, string Salt, string Nonce, string Ciphertext, string Tag);
    private byte[]? key;
    private readonly object keyGate = new();
    public string AccountId { get; }
    public long ChatId { get; }
    public string KeyId { get; }
    public VaultMetadataKey(string accountId, long chatId, string keyId, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (chatId == 0 || bytes.Length != 32 || !Guid.TryParseExact(keyId, "N", out var id) || id.ToString("N") != keyId)
            throw new InvalidDataException("The vault metadata key has invalid scope or format.");
        AccountId = accountId; ChatId = chatId; KeyId = keyId; key = bytes.ToArray();
    }
    public static VaultMetadataKey Create(string accountId, long chatId)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try { return new(accountId, chatId, Guid.NewGuid().ToString("N"), bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public byte[] ExportKeyBytes() { lock (keyGate) return (key ?? throw new ObjectDisposedException(nameof(VaultMetadataKey))).ToArray(); }
    public VaultMetadataKey Clone()
    {
        var bytes = ExportKeyBytes();
        try { return new(AccountId, ChatId, KeyId, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public VaultMetadataBackup ExportBackup(string passphrase)
    {
        // The wrapper and scope proof must use one owned key even if the UI closes
        // or switches vaults while PBKDF2 is running.
        using var snapshot = Clone();
        return new(1, AccountId, ChatId, KeyId, AesGcmFileCipher.WrapFileKey(snapshot.key!, passphrase), snapshot.ScopeProof());
    }
    public static VaultMetadataKey Recover(VaultMetadataBackup backup, string accountId, long chatId, string passphrase)
    {
        if (backup.Version != 1 || backup.AccountId != accountId || backup.ChatId != chatId) throw new InvalidDataException("The metadata key backup belongs to another account or vault.");
        if (backup.RecoveryKey is null || string.IsNullOrWhiteSpace(backup.KeyId) || string.IsNullOrWhiteSpace(backup.Proof))
            throw new InvalidDataException("The metadata key backup is empty.");
        var bytes = AesGcmFileCipher.UnwrapFileKey(backup.RecoveryKey, passphrase);
        VaultMetadataKey? recovered = null;
        try
        {
            recovered = new(accountId, chatId, backup.KeyId, bytes);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(backup.Proof), Convert.FromBase64String(recovered.ScopeProof())))
                throw new CryptographicException("The metadata key backup failed scope authentication.");
            return recovered;
        }
        catch { recovered?.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string ScopeProof()
    {
        var mac = Derive("TeleSelfCloud metadata scope proof v1");
        try { return Convert.ToBase64String(HMACSHA256.HashData(mac, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, AccountId, ChatId, KeyId }, Options))); }
        finally { CryptographicOperations.ZeroMemory(mac); }
    }
    private byte[] Derive(string purpose, byte[]? salt = null)
    {
        var bytes = ExportKeyBytes();
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, bytes, 32, salt: salt ?? [], info: System.Text.Encoding.UTF8.GetBytes(purpose)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private byte[] Aad(string kind, string identity)
    {
        if (kind is not "manifest" and not "folders" || string.IsNullOrWhiteSpace(identity) || identity.Length > 512)
            throw new InvalidDataException("The metadata envelope has an invalid document context.");
        return JsonSerializer.SerializeToUtf8Bytes(new { version = 1, AccountId, ChatId, KeyId, kind, identity }, Options);
    }
    public byte[] Protect(string kind, string identity, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaxPlaintext) throw new InvalidDataException("The metadata document exceeds the encryption size limit.");
        var nonce = RandomNumberGenerator.GetBytes(12); var ciphertext = new byte[plaintext.Length]; var tag = new byte[16];
        var salt = RandomNumberGenerator.GetBytes(32);
        var encryptionKey = Derive("TeleSelfCloud metadata AES-GCM v1", salt);
        try
        {
            using var aes = new AesGcm(encryptionKey, 16); aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(kind, identity).Concat(salt).ToArray());
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, "AES-256-GCM", KeyId, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)), Options);
        }
        finally { CryptographicOperations.ZeroMemory(encryptionKey); }
    }
    public byte[] Unprotect(string kind, string identity, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("The protected metadata document exceeds the safe parsing limit.");
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new InvalidDataException("The protected metadata envelope is empty.");
        if (envelope.Version != 1 || envelope.Cipher != "AES-256-GCM" || envelope.KeyId != KeyId)
            throw new InvalidDataException("Unlock this vault with the metadata key matching its encrypted documents.");
        if (envelope.Nonce is null || envelope.Ciphertext is null || envelope.Tag is null || envelope.Salt is null)
            throw new InvalidDataException("The protected metadata envelope has invalid field lengths.");
        var nonce = Convert.FromBase64String(envelope.Nonce); var ciphertext = Convert.FromBase64String(envelope.Ciphertext); var tag = Convert.FromBase64String(envelope.Tag); var salt = Convert.FromBase64String(envelope.Salt);
        if (salt.Length != 32 || nonce.Length != 12 || tag.Length != 16 || ciphertext.Length > MaxPlaintext) throw new InvalidDataException("The protected metadata envelope has invalid field lengths.");
        var plaintext = new byte[ciphertext.Length]; var encryptionKey = Derive("TeleSelfCloud metadata AES-GCM v1", salt);
        try { using var aes = new AesGcm(encryptionKey, 16); aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad(kind, identity).Concat(salt).ToArray()); return plaintext; }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(encryptionKey); }
    }
    public async Task<byte[]> ProtectForReplayAsync(string directory, string kind, string identity, byte[] plaintext, CancellationToken token)
    {
        using var snapshot = Clone();
        return await snapshot.ProtectForReplayCoreAsync(directory, kind, identity, plaintext, token);
    }
    private async Task<byte[]> ProtectForReplayCoreAsync(string directory, string kind, string identity, byte[] plaintext, CancellationToken token)
    {
        var mac = Derive("TeleSelfCloud metadata outbox lookup v1");
        string name;
        try { name = Convert.ToHexString(HMACSHA256.HashData(mac, Aad(kind, identity).Concat(SHA256.HashData(plaintext)).ToArray())); }
        finally { CryptographicOperations.ZeroMemory(mac); }
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".json");
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("The protected metadata outbox is invalid. Its file was kept.");
            var existing = await File.ReadAllBytesAsync(path, token); var decoded = Unprotect(kind, identity, existing);
            try { if (!decoded.AsSpan().SequenceEqual(plaintext)) throw new InvalidDataException("The protected metadata outbox differs from its document. Both were kept."); }
            finally { CryptographicOperations.ZeroMemory(decoded); }
            return existing;
        }
        var protectedBytes = Protect(kind, identity, plaintext);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await stream.WriteAsync(protectedBytes, token); await stream.FlushAsync(token); stream.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, false); return protectedBytes;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { lock (keyGate) { if (key is not null) { CryptographicOperations.ZeroMemory(key); key = null; } } }
}
