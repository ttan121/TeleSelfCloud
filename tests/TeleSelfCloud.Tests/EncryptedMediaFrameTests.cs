using System.Security.Cryptography;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class EncryptedMediaFrameTests
{
    [Fact]
    public async Task RandomAccessLayoutAuthenticatesEveryFrameAndMapsPlaintextOffsets()
    {
        var logical = "abcdefghij"u8.ToArray();
        var key = AesGcmFileCipher.CreateFileKey();
        try
        {
            using var encrypted = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(new MemoryStream(logical), encrypted, key, frameSize: 4);
            var payload = encrypted.ToArray();
            var layout = AesGcmFileCipher.ParseFrameLayout(payload.AsSpan(0, 33), logical.Length, payload.Length);
            Assert.Equal(3u, layout.FrameCount);
            Assert.Equal(33L, AesGcmFileCipher.GetFrameOffset(layout, 0));
            Assert.Equal(53L, AesGcmFileCipher.GetFrameOffset(layout, 1));
            Assert.Equal(73L, AesGcmFileCipher.GetFrameOffset(layout, 2));

            for (uint index = 0; index < layout.FrameCount; index++)
            {
                var frameLength = AesGcmFileCipher.GetFramePlaintextLength(layout, index);
                var frameOffset = AesGcmFileCipher.GetFrameOffset(layout, index);
                var authenticated = AesGcmFileCipher.DecryptFrame(payload.AsSpan(0, 33), index,
                    payload.AsSpan((int)frameOffset, frameLength + 16), key);
                try { Assert.Equal(logical.AsSpan((int)index * 4, frameLength).ToArray(), authenticated); }
                finally { CryptographicOperations.ZeroMemory(authenticated); }
            }

            var damaged = payload.AsSpan((int)AesGcmFileCipher.GetFrameOffset(layout, 1), 4 + 16).ToArray();
            damaged[^1] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => AesGcmFileCipher.DecryptFrame(payload.AsSpan(0, 33), 1, damaged, key));
            CryptographicOperations.ZeroMemory(damaged);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task EmptyEncryptedFileHasOneAuthenticatedZeroLengthFrame()
    {
        var key = AesGcmFileCipher.CreateFileKey();
        try
        {
            using var encrypted = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(new MemoryStream([]), encrypted, key, frameSize: 8);
            var payload = encrypted.ToArray();
            var layout = AesGcmFileCipher.ParseFrameLayout(payload.AsSpan(0, 33), 0, payload.Length);
            Assert.Equal(1u, layout.FrameCount);
            var frame = AesGcmFileCipher.DecryptFrame(payload.AsSpan(0, 33), 0, payload.AsSpan(33), key);
            Assert.Empty(frame);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
