using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Authenticated local-record format; callers explicitly supply the existing profile key/scope.</summary>
public sealed class LocalRecordCipher : IDisposable
{
    public const int MaxPlaintextBytes = 64 * 1024 * 1024;
    public const int MaxCiphertextBytes = MaxPlaintextBytes + 76;
    private static ReadOnlySpan<byte> Magic => "TSCREC01"u8;
    public static bool IsProtectedRecord(ReadOnlySpan<byte> bytes) => bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);
    private readonly object gate = new();
    private byte[]? master;
    private readonly byte[] context;
    public LocalRecordCipher(string databaseKey, string protectionId, string purpose, string identity)
    {
        if (!Guid.TryParseExact(protectionId, "N", out var id) || id.ToString("N") != protectionId ||
            purpose is not ("cache-verification" or "vault-registry" or "folder-journal" or "metadata-history" or "sync-report" or "vault-creation" or "account-storage-settings") ||
            string.IsNullOrWhiteSpace(identity) || identity.Length > 512)
            throw new InvalidDataException("The local record context is invalid.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(databaseKey); }
        catch (FormatException) { throw new InvalidDataException("The local record key is invalid."); }
        if (bytes.Length != 32 || Convert.ToBase64String(bytes) != databaseKey)
        { CryptographicOperations.ZeroMemory(bytes); throw new InvalidDataException("The local record key is invalid."); }
        master = bytes;
        context = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, protectionId, purpose, identity });
    }
    private byte[] Derive(ReadOnlySpan<byte> salt)
    {
        byte[] copy; lock (gate) copy = (master ?? throw new ObjectDisposedException(nameof(LocalRecordCipher))).ToArray();
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, copy, 32, salt.ToArray(), "TeleSelfCloud local record AES-GCM v1"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaxPlaintextBytes) throw new InvalidDataException("The local record exceeds its size limit.");
        var output = new byte[plaintext.Length + 76]; Magic.CopyTo(output);
        RandomNumberGenerator.Fill(output.AsSpan(8, 32)); RandomNumberGenerator.Fill(output.AsSpan(40, 12));
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(52, 8), plaintext.Length);
        var key = Derive(output.AsSpan(8, 32));
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(40, 12), plaintext, output.AsSpan(60, plaintext.Length), output.AsSpan(60 + plaintext.Length, 16), context.Concat(output.AsSpan(0, 60).ToArray()).ToArray());
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public byte[] Unprotect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 76 or > MaxCiphertextBytes || !bytes[..8].SequenceEqual(Magic)) throw new InvalidDataException("The protected local record has an invalid format. Its file was kept.");
        var length = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(52, 8));
        if (length < 0 || length > MaxPlaintextBytes || length != bytes.Length - 76) throw new InvalidDataException("The protected local record has an invalid length. Its file was kept.");
        var plaintext = new byte[(int)length]; var key = Derive(bytes.Slice(8, 32));
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.Slice(40, 12), bytes.Slice(60, (int)length), bytes[^16..], plaintext, context.Concat(bytes[..60].ToArray()).ToArray());
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public void Dispose() { lock (gate) { if (master is not null) { CryptographicOperations.ZeroMemory(master); master = null; } } }
}
