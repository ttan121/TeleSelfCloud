using System.Buffers.Binary;
using System.Security.Cryptography;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalStagingCipherTests
{
    private const string Key = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
    private const string ProtectionId = "5f4f2ae5d8b54a9eb145f230fa4b3df8";
    private const string Identity = "file:fixture:part:0";

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(1048576)]
    [InlineData(1048593)]
    public async Task RoundTripsEmptyAndMultiFramePayloadsWithBoundedFormat(long length)
    {
        var bytes = Enumerable.Range(0, checked((int)length)).Select(index => (byte)(index * 31)).ToArray();

        var protectedBytes = await EncryptAsync(bytes);

        Assert.True(LocalStagingCipher.HasProtectedHeader(protectedBytes));
        var expectedFrames = Math.Max(1, (length + LocalStagingCipher.FrameSize - 1) / LocalStagingCipher.FrameSize);
        Assert.Equal(LocalStagingCipher.HeaderSize + length + expectedFrames * LocalStagingCipher.FrameOverhead, protectedBytes.LongLength);
        Assert.Equal(bytes, await DecryptAsync(protectedBytes));
    }

    [Fact]
    public async Task WrongKeyProtectionIdOrIdentityFailsAuthentication()
    {
        var protectedBytes = await EncryptAsync([1, 2, 3, 4]);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(protectedBytes, Key, ProtectionId, "other-part"));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(protectedBytes, Key, Guid.NewGuid().ToString("N"), Identity));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(protectedBytes, Convert.ToBase64String(new byte[32]), ProtectionId, Identity));
    }

    [Fact]
    public async Task FrameTagTamperAndFrameReorderingAreRejected()
    {
        var protectedBytes = await EncryptAsync(new byte[LocalStagingCipher.FrameSize * 2]);
        var tampered = protectedBytes.ToArray();
        tampered[^1] ^= 0x80;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(tampered));

        var firstLength = LocalStagingCipher.FrameSize;
        var firstSize = 4 + 12 + firstLength + 16;
        var secondSize = protectedBytes.Length - LocalStagingCipher.HeaderSize - firstSize;
        var reordered = protectedBytes[..LocalStagingCipher.HeaderSize]
            .Concat(protectedBytes[(LocalStagingCipher.HeaderSize + firstSize)..])
            .Concat(protectedBytes[LocalStagingCipher.HeaderSize..(LocalStagingCipher.HeaderSize + firstSize)]).ToArray();
        Assert.Equal(secondSize + firstSize + LocalStagingCipher.HeaderSize, reordered.Length);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(reordered));
    }

    [Fact]
    public async Task TruncationTrailingDataInvalidFrameLengthAndHeaderAreRejected()
    {
        var protectedBytes = await EncryptAsync([9, 8, 7]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => DecryptAsync(protectedBytes[..^1]));
        await Assert.ThrowsAsync<InvalidDataException>(() => DecryptAsync([.. protectedBytes, (byte)0]));

        var badFrameLength = protectedBytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(badFrameLength.AsSpan(LocalStagingCipher.HeaderSize, 4), 99);
        await Assert.ThrowsAsync<InvalidDataException>(() => DecryptAsync(badFrameLength));

        var badHeader = protectedBytes.ToArray();
        badHeader[0] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => DecryptAsync(badHeader));
    }

    [Fact]
    public async Task RejectsMalformedProtectionContextAndOversizeLengthBeforeWriting()
    {
        Assert.Throws<InvalidDataException>(() => new LocalStagingCipher("invalid", ProtectionId, Identity));
        Assert.Throws<InvalidDataException>(() => new LocalStagingCipher(Key, "not-a-guid", Identity));
        using var cipher = new LocalStagingCipher(Key, ProtectionId, Identity);
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => cipher.EncryptAsync(input, output, LocalStagingCipher.MaximumPlaintextBytes + 1));
        Assert.Empty(output.ToArray());
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext)
    {
        using var cipher = new LocalStagingCipher(Key, ProtectionId, Identity);
        using var input = new MemoryStream(plaintext, writable: false);
        using var output = new MemoryStream();
        await cipher.EncryptAsync(input, output, plaintext.Length);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] protectedBytes, string key = Key, string protectionId = ProtectionId, string identity = Identity)
    {
        using var cipher = new LocalStagingCipher(key, protectionId, identity);
        using var input = new MemoryStream(protectedBytes, writable: false);
        using var output = new MemoryStream();
        await cipher.DecryptAsync(input, output);
        return output.ToArray();
    }
}
