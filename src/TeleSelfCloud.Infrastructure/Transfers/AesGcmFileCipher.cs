using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Versioned AES-256-GCM frame format and passphrase wrapping for 32-byte file keys.</summary>
public static class AesGcmFileCipher
{
    private static readonly byte[] Magic = "TSCENC01"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int HeaderLength = 33;
    private const int TagLength = 16;
    private const int NoncePrefixLength = 8;
    private const int KeyLength = 32;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int FrameSizeLimit = 4 * 1024 * 1024;
    private const int PassphraseIterations = 600_000;
    private const string KdfName = "PBKDF2-SHA256";
    private const string CipherName = "AES-256-GCM";

    public readonly record struct EncryptedFrameLayout(long PlaintextSize, int FrameSize, uint FrameCount, long PayloadSize);

    public static EncryptedFrameLayout ParseFrameLayout(ReadOnlySpan<byte> header, long expectedPlaintextSize, long expectedPayloadSize)
    {
        if (header.Length != HeaderLength || !header[..Magic.Length].SequenceEqual(Magic) || header[8] != FormatVersion)
            throw new InvalidDataException("The encrypted media header is invalid or unsupported.");
        var frameSize = BinaryPrimitives.ReadInt32LittleEndian(header[9..13]);
        var plaintextSize = BinaryPrimitives.ReadInt64LittleEndian(header[13..21]);
        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[29..33]);
        if (frameSize is < 1 or > FrameSizeLimit || plaintextSize < 0 || plaintextSize != expectedPlaintextSize ||
            frameCount != GetFrameCount(plaintextSize, frameSize))
            throw new InvalidDataException("The encrypted media header does not match its manifest.");
        long payloadSize;
        try { payloadSize = checked(HeaderLength + plaintextSize + (long)frameCount * TagLength); }
        catch (OverflowException ex) { throw new InvalidDataException("The encrypted media payload length is invalid.", ex); }
        if (payloadSize != expectedPayloadSize)
            throw new InvalidDataException("The encrypted media payload length does not match its manifest.");
        return new(plaintextSize, frameSize, frameCount, payloadSize);
    }

    public static long GetFrameOffset(EncryptedFrameLayout layout, uint frameIndex)
    {
        if (frameIndex >= layout.FrameCount) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return checked(HeaderLength + (long)frameIndex * (layout.FrameSize + TagLength));
    }

    public static int GetFramePlaintextLength(EncryptedFrameLayout layout, uint frameIndex)
    {
        if (frameIndex >= layout.FrameCount) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        var start = (long)frameIndex * layout.FrameSize;
        return (int)Math.Min(layout.FrameSize, layout.PlaintextSize - start);
    }

    /// <summary>Authenticates one complete AES-GCM frame before returning its plaintext.</summary>
    public static byte[] DecryptFrame(ReadOnlySpan<byte> header, uint frameIndex,
        ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> fileKey)
    {
        ValidateKey(fileKey);
        if (header.Length != HeaderLength || !header[..Magic.Length].SequenceEqual(Magic) || header[8] != FormatVersion)
            throw new InvalidDataException("The encrypted media header is invalid or unsupported.");
        var frameSize = BinaryPrimitives.ReadInt32LittleEndian(header[9..13]);
        var plaintextSize = BinaryPrimitives.ReadInt64LittleEndian(header[13..21]);
        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[29..33]);
        if (frameSize is < 1 or > FrameSizeLimit || plaintextSize < 0 || frameCount != GetFrameCount(plaintextSize, frameSize) || frameIndex >= frameCount)
            throw new InvalidDataException("The encrypted media frame index or header is invalid.");
        var frameLength = (int)Math.Min(frameSize, plaintextSize - (long)frameIndex * frameSize);
        if (ciphertextAndTag.Length != frameLength + TagLength)
            throw new InvalidDataException("The encrypted media frame is truncated or malformed.");

        var nonce = new byte[NonceLength];
        var aad = CreateFrameAad(header);
        var plaintext = new byte[frameLength];
        header.Slice(21, NoncePrefixLength).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), frameIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(HeaderLength, 4), frameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(HeaderLength + 4, 4), frameLength);
        try
        {
            using var aes = new AesGcm(fileKey, TagLength);
            aes.Decrypt(nonce, ciphertextAndTag[..frameLength], ciphertextAndTag[frameLength..], plaintext, aad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    public static byte[] CreateFileKey() => RandomNumberGenerator.GetBytes(KeyLength);

    public static PassphraseKeyEnvelope WrapFileKey(ReadOnlySpan<byte> fileKey, string passphrase)
    {
        ValidateKey(fileKey);
        ValidatePassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, PassphraseIterations, HashAlgorithmName.SHA256, KeyLength);
        var ciphertext = new byte[KeyLength];
        var tag = new byte[TagLength];
        try
        {
            using var aes = new AesGcm(derivedKey, TagLength);
            aes.Encrypt(nonce, fileKey, ciphertext, tag, EnvelopeAad(PassphraseIterations));
            return new PassphraseKeyEnvelope(FormatVersion, KdfName, PassphraseIterations, CipherName,
                Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally { CryptographicOperations.ZeroMemory(derivedKey); }
    }

    public static byte[] UnwrapFileKey(PassphraseKeyEnvelope envelope, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidatePassphrase(passphrase);
        if (envelope.Version != FormatVersion || envelope.Kdf != KdfName || envelope.Cipher != CipherName ||
            envelope.Iterations != PassphraseIterations)
            throw new InvalidDataException("The passphrase key envelope uses an unsupported format.");
        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        if (salt.Length != SaltLength || nonce.Length != NonceLength || ciphertext.Length != KeyLength || tag.Length != TagLength)
            throw new InvalidDataException("The passphrase key envelope has invalid field lengths.");
        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, envelope.Iterations, HashAlgorithmName.SHA256, KeyLength);
        var fileKey = new byte[KeyLength];
        try
        {
            using var aes = new AesGcm(derivedKey, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, fileKey, EnvelopeAad(envelope.Iterations));
            return fileKey;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(fileKey);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(derivedKey); }
    }

    public static async Task EncryptAsync(
        Stream plaintext, Stream ciphertext, ReadOnlyMemory<byte> fileKey,
        int frameSize = 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ValidateKey(fileKey.Span);
        if (!plaintext.CanRead || !plaintext.CanSeek || !ciphertext.CanWrite || !ciphertext.CanSeek)
            throw new ArgumentException("Encryption requires a readable, seekable source and seekable, writable destination.");
        var initialPosition = ciphertext.Position;
        var initialLength = ciphertext.Length;
        if (frameSize is < 1 or > FrameSizeLimit) throw new ArgumentOutOfRangeException(nameof(frameSize));
        var length = plaintext.Length - plaintext.Position;
        if (length < 0) throw new InvalidDataException("The source stream position exceeds its length.");
        var frameCount = GetFrameCount(length, frameSize);
        if (frameCount > uint.MaxValue) throw new InvalidDataException("The source exceeds the encrypted format limit.");

        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        header[8] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9, 4), frameSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(13, 8), length);
        RandomNumberGenerator.Fill(header.AsSpan(21, NoncePrefixLength));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(29, 4), (uint)frameCount);
        var plainBuffer = new byte[frameSize];
        var encryptedBuffer = new byte[frameSize];
        var tag = new byte[TagLength];
        var nonce = new byte[NonceLength];
        var aad = CreateFrameAad(header);
        header.AsSpan(21, NoncePrefixLength).CopyTo(nonce);
        using var aes = new AesGcm(fileKey.Span, TagLength);
        try
        {
            await ciphertext.WriteAsync(header, cancellationToken);
            long remaining = length;
            for (uint index = 0; index < frameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameLength = (int)Math.Min(frameSize, remaining);
                await ReadExactlyAsync(plaintext, plainBuffer.AsMemory(0, frameLength), cancellationToken);
                BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), index);
                BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(HeaderLength, 4), index);
                BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(HeaderLength + 4, 4), frameLength);
                aes.Encrypt(nonce, plainBuffer.AsSpan(0, frameLength), encryptedBuffer.AsSpan(0, frameLength), tag,
                    aad);
                await ciphertext.WriteAsync(encryptedBuffer.AsMemory(0, frameLength), cancellationToken);
                await ciphertext.WriteAsync(tag, cancellationToken);
                CryptographicOperations.ZeroMemory(plainBuffer.AsSpan(0, frameLength));
                remaining -= frameLength;
            }
            if (remaining != 0 || plaintext.ReadByte() != -1)
                throw new InvalidDataException("The source stream changed while encryption was running.");
        }
        catch (Exception ex)
        {
            try
            {
                ciphertext.SetLength(initialLength);
                ciphertext.Position = initialPosition;
            }
            catch (Exception rollbackError)
            {
                throw new IOException("Encryption failed and partial ciphertext could not be rolled back.", new AggregateException(ex, rollbackError));
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBuffer);
            CryptographicOperations.ZeroMemory(encryptedBuffer);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    public static async Task DecryptAsync(
        Stream ciphertext, Stream plaintext, ReadOnlyMemory<byte> fileKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);
        ValidateKey(fileKey.Span);
        if (!ciphertext.CanRead || !plaintext.CanWrite || !plaintext.CanSeek)
            throw new ArgumentException("Decryption requires a readable source and seekable, writable destination.");
        var initialPosition = plaintext.Position;
        var initialLength = plaintext.Length;
        var header = new byte[HeaderLength];
        await ReadExactlyAsync(ciphertext, header, cancellationToken);
        var frameSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(9, 4));
        var length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(13, 8));
        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(29, 4));
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || header[8] != FormatVersion ||
            frameSize is < 1 or > FrameSizeLimit || length < 0 ||
            frameCount != GetFrameCount(length, frameSize))
            throw new InvalidDataException("The encrypted file header is invalid or unsupported.");

        var plainBuffer = new byte[frameSize];
        var encryptedBuffer = new byte[frameSize];
        var tag = new byte[TagLength];
        var nonce = new byte[NonceLength];
        var aad = CreateFrameAad(header);
        header.AsSpan(21, NoncePrefixLength).CopyTo(nonce);
        using var aes = new AesGcm(fileKey.Span, TagLength);
        try
        {
            long remaining = length;
            for (uint index = 0; index < frameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameLength = (int)Math.Min(frameSize, remaining);
                await ReadExactlyAsync(ciphertext, encryptedBuffer.AsMemory(0, frameLength), cancellationToken);
                await ReadExactlyAsync(ciphertext, tag, cancellationToken);
                BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), index);
                BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(HeaderLength, 4), index);
                BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(HeaderLength + 4, 4), frameLength);
                aes.Decrypt(nonce, encryptedBuffer.AsSpan(0, frameLength), tag, plainBuffer.AsSpan(0, frameLength),
                    aad);
                await plaintext.WriteAsync(plainBuffer.AsMemory(0, frameLength), cancellationToken);
                CryptographicOperations.ZeroMemory(plainBuffer.AsSpan(0, frameLength));
                remaining -= frameLength;
            }
            if (remaining != 0 || ciphertext.ReadByte() != -1)
                throw new InvalidDataException("The encrypted file has trailing or inconsistent data.");
        }
        catch (Exception ex)
        {
            try
            {
                plaintext.SetLength(initialLength);
                plaintext.Position = initialPosition;
            }
            catch (Exception rollbackError)
            {
                throw new IOException("Decryption failed and partial plaintext could not be rolled back.", new AggregateException(ex, rollbackError));
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBuffer);
            CryptographicOperations.ZeroMemory(encryptedBuffer);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static byte[] EnvelopeAad(int iterations) => Encoding.ASCII.GetBytes($"TeleSelfCloud.FileKey|{FormatVersion}|{KdfName}|{iterations}|{CipherName}");

    private static byte[] CreateFrameAad(ReadOnlySpan<byte> header)
    {
        var aad = new byte[header.Length + 8];
        header.CopyTo(aad);
        return aad;
    }

    private static long GetFrameCount(long length, int frameSize) => length == 0 ? 1 : 1 + ((length - 1) / frameSize);

    private static async Task ReadExactlyAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await source.ReadAsync(destination[read..], cancellationToken);
            if (count == 0) throw new EndOfStreamException("The encrypted file is truncated.");
            read += count;
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength) throw new ArgumentException("AES-256 file keys must be exactly 32 bytes.", nameof(key));
    }

    private static void ValidatePassphrase(string passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        if (passphrase.Length < 12) throw new ArgumentException("Recovery passphrases must contain at least 12 characters.", nameof(passphrase));
    }
}
