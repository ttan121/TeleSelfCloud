using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>
/// Sequential authenticated file format for large local staging payloads. Callers must decrypt
/// into an owned scratch file and publish it only after this method returns successfully.
/// </summary>
public sealed class LocalStagingCipher : IDisposable
{
    public const int FrameSize = 1024 * 1024;
    public const int HeaderSize = 52;
    public const int FrameOverhead = 32;
    public const long MaximumPlaintextBytes = 1L << 40;
    private static ReadOnlySpan<byte> Magic => "TSCSTG01"u8;
    private readonly object gate = new();
    private byte[]? master;
    private readonly byte[] context;

    public LocalStagingCipher(string databaseKey, string protectionId, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (!Guid.TryParseExact(protectionId, "N", out var parsed) || parsed.ToString("N") != protectionId || identity.Length > 512)
            throw new InvalidDataException("The local staging protection context is invalid.");
        byte[] key;
        try { key = Convert.FromBase64String(databaseKey); }
        catch (FormatException ex) { throw new InvalidDataException("The local staging protection key is invalid.", ex); }
        if (key.Length != 32 || Convert.ToBase64String(key) != databaseKey)
        { CryptographicOperations.ZeroMemory(key); throw new InvalidDataException("The local staging protection key is invalid."); }
        master = key;
        context = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, protectionId, purpose = "staging-file", identity });
    }

    public static bool HasProtectedHeader(ReadOnlySpan<byte> prefix) => prefix.Length >= Magic.Length && prefix[..Magic.Length].SequenceEqual(Magic);

    public async Task EncryptAsync(Stream plaintext, Stream ciphertext, long plaintextLength, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!plaintext.CanRead || !ciphertext.CanWrite) throw new ArgumentException("Staging protection requires readable input and writable output streams.");
        if (plaintextLength < 0 || plaintextLength > MaximumPlaintextBytes) throw new InvalidDataException("The local staging payload length is outside the supported range.");

        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        RandomNumberGenerator.Fill(header.AsSpan(8, 32));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40, 8), plaintextLength);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48, 4), FrameSize);
        var key = Derive(header.AsSpan(8, 32));
        var plainFrame = new byte[FrameSize];
        var encryptedFrame = new byte[FrameSize];
        var nonce = new byte[12];
        var tag = new byte[16];
        try
        {
            await ciphertext.WriteAsync(header, token);
            using var aes = new AesGcm(key, 16);
            long remaining = plaintextLength;
            long index = 0;
            do
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Math.Min(FrameSize, remaining);
                if (length > 0) await ReadExactlyAsync(plaintext, plainFrame.AsMemory(0, length), token);
                RandomNumberGenerator.Fill(nonce);
                var aad = MakeAad(header, index, length);
                aes.Encrypt(nonce, plainFrame.AsSpan(0, length), encryptedFrame.AsSpan(0, length), tag, aad);
                var frameLength = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(frameLength, length);
                await ciphertext.WriteAsync(frameLength, token);
                await ciphertext.WriteAsync(nonce, token);
                if (length > 0) await ciphertext.WriteAsync(encryptedFrame.AsMemory(0, length), token);
                await ciphertext.WriteAsync(tag, token);
                CryptographicOperations.ZeroMemory(plainFrame.AsSpan(0, length));
                CryptographicOperations.ZeroMemory(encryptedFrame.AsSpan(0, length));
                CryptographicOperations.ZeroMemory(aad);
                remaining -= length;
                index++;
            } while (remaining > 0 || index == 0);
            await ciphertext.FlushAsync(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(plainFrame);
            CryptographicOperations.ZeroMemory(encryptedFrame);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public async Task DecryptAsync(Stream ciphertext, Stream plaintext, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!ciphertext.CanRead || !plaintext.CanWrite) throw new ArgumentException("Staging protection requires readable input and writable output streams.");

        var header = new byte[HeaderSize];
        await ReadExactlyAsync(ciphertext, header, token);
        if (!HasProtectedHeader(header) || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(48, 4)) != FrameSize)
            throw new InvalidDataException("The protected staging header is invalid; the file was kept.");
        var length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40, 8));
        if (length < 0 || length > MaximumPlaintextBytes) throw new InvalidDataException("The protected staging length is invalid; the file was kept.");

        var key = Derive(header.AsSpan(8, 32));
        var plainFrame = new byte[FrameSize];
        var encryptedFrame = new byte[FrameSize];
        var nonce = new byte[12];
        var tag = new byte[16];
        var frameLength = new byte[4];
        try
        {
            using var aes = new AesGcm(key, 16);
            long remaining = length;
            long index = 0;
            do
            {
                token.ThrowIfCancellationRequested();
                var expectedLength = (int)Math.Min(FrameSize, remaining);
                await ReadExactlyAsync(ciphertext, frameLength, token);
                var actualLength = BinaryPrimitives.ReadInt32LittleEndian(frameLength);
                if (actualLength != expectedLength) throw new InvalidDataException("The protected staging frame sequence is invalid; the file was kept.");
                await ReadExactlyAsync(ciphertext, nonce, token);
                if (actualLength > 0) await ReadExactlyAsync(ciphertext, encryptedFrame.AsMemory(0, actualLength), token);
                await ReadExactlyAsync(ciphertext, tag, token);
                var aad = MakeAad(header, index, actualLength);
                aes.Decrypt(nonce, encryptedFrame.AsSpan(0, actualLength), tag, plainFrame.AsSpan(0, actualLength), aad);
                if (actualLength > 0) await plaintext.WriteAsync(plainFrame.AsMemory(0, actualLength), token);
                CryptographicOperations.ZeroMemory(plainFrame.AsSpan(0, actualLength));
                CryptographicOperations.ZeroMemory(encryptedFrame.AsSpan(0, actualLength));
                CryptographicOperations.ZeroMemory(aad);
                remaining -= actualLength;
                index++;
            } while (remaining > 0 || index == 0);

            var trailing = new byte[1];
            if (await ciphertext.ReadAsync(trailing, token) != 0)
                throw new InvalidDataException("The protected staging file contains trailing bytes; the file was kept.");
            await plaintext.FlushAsync(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(plainFrame);
            CryptographicOperations.ZeroMemory(encryptedFrame);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(frameLength);
        }
    }

    private byte[] Derive(ReadOnlySpan<byte> salt)
    {
        byte[] copy;
        lock (gate) copy = (master ?? throw new ObjectDisposedException(nameof(LocalStagingCipher))).ToArray();
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, copy, 32, salt.ToArray(), "TeleSelfCloud staging AES-GCM v1"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    private byte[] MakeAad(ReadOnlySpan<byte> header, long frameIndex, int frameLength)
    {
        var aad = new byte[context.Length + HeaderSize + 12];
        context.CopyTo(aad, 0);
        header.CopyTo(aad.AsSpan(context.Length));
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(context.Length + HeaderSize, 8), frameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(context.Length + HeaderSize + 8, 4), frameLength);
        return aad;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], token);
            if (count == 0) throw new EndOfStreamException("The protected staging file ended before its authenticated frame was complete.");
            read += count;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (master is not null)
            {
                CryptographicOperations.ZeroMemory(master);
                master = null;
            }
            CryptographicOperations.ZeroMemory(context);
        }
    }
}
