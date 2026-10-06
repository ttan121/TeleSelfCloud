using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Read-only encrypted reuse proof: authenticated logical bytes and recovery key, never plaintext files.</summary>
public sealed class EncryptedContentDedupVerifier(ITelegramRequestClient session, IPartTransport transport, string accountId, long chatId, string temporaryRoot)
{
    public async Task<bool> VerifyAsync(FileManifest candidate, string sourceSha256, long sourceSize, string recoveryPassphrase, CancellationToken token)
        => await VerifyCoreAsync(candidate, sourceSha256, sourceSize, recoveryPassphrase, null, token) is not null;

    public Task<FileManifest?> VerifyAndStageAsync(FileManifest candidate, string sourceSha256, long sourceSize,
        string sourceRecoveryPassphrase, string newRecoveryPassphrase, CancellationToken token) =>
        VerifyCoreAsync(candidate, sourceSha256, sourceSize, sourceRecoveryPassphrase, newRecoveryPassphrase, token);

    private async Task<FileManifest?> VerifyCoreAsync(FileManifest candidate, string sourceSha256, long sourceSize,
        string recoveryPassphrase, string? newRecoveryPassphrase, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(sourceSha256);
        ManifestValidator.ValidateStructure(candidate);
        candidate = candidate with { Parts = candidate.Parts.ToArray() }; ManifestValidator.ValidateStructure(candidate);
        if (transport is TelegramFileTransport native && (!ReferenceEquals(native.RequestClient, session) || native.ChatId != chatId))
            throw new InvalidDataException("Encrypted duplicate verification requires the same Telegram session and vault transport.");
        if (string.IsNullOrWhiteSpace(accountId) || chatId == 0 || candidate.AccountId != accountId)
            throw new InvalidDataException("The encrypted duplicate belongs to another vault.");
        if (sourceSize < 0 || sourceSha256.Length != 64 || !sourceSha256.All(Uri.IsHexDigit)) throw new ArgumentException("The source content identity is invalid.");
        if (!candidate.Committed || candidate.IsInTrash || candidate.Encryption is not { } encryption || candidate.LogicalSize != sourceSize ||
            !string.Equals(candidate.TotalSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var part in candidate.Parts)
            if (part.RemoteId is null || TelegramRemoteMessageId.Parse(part.RemoteId).ChatId != chatId)
                throw new InvalidDataException("An encrypted duplicate part points outside the active vault.");
        token.ThrowIfCancellationRequested();
        var key = AesGcmFileCipher.UnwrapFileKey(encryption.RecoveryKey, recoveryPassphrase);
        try
        {
            // Ciphertext only. CreateNew + DeleteOnClose gives exclusive, attempt-owned lifetime on every exit.
            Directory.CreateDirectory(temporaryRoot);
            var path = Path.Combine(temporaryRoot, "dedup-proof-" + Guid.NewGuid().ToString("N") + ".cipher");
            await using var ciphertext = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            if (!await new RemoteContentDedupVerifier(session, transport, accountId, chatId).VerifyPayloadAsync(candidate, encryption.PayloadSha256, encryption.PayloadSize, ciphertext, token)) return null;
            await ciphertext.FlushAsync(token); ciphertext.Position = 0;
            using var plaintext = new LogicalHashSink(sourceSize);
            await AesGcmFileCipher.DecryptAsync(ciphertext, plaintext, key, token);
            token.ThrowIfCancellationRequested();
            if (plaintext.Length != sourceSize || !CryptographicOperations.FixedTimeEquals(plaintext.Finish(), Convert.FromHexString(sourceSha256))) return null;
            if (newRecoveryPassphrase is null) return candidate;
            var envelope = AesGcmFileCipher.WrapFileKey(key, newRecoveryPassphrase);
            return await RetainVerifiedPartsAsync(candidate, ciphertext, envelope, token);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task<FileManifest> RetainVerifiedPartsAsync(FileManifest candidate, FileStream ciphertext, PassphraseKeyEnvelope envelope, CancellationToken token)
    {
        var directory = Path.Combine(temporaryRoot, "dedup-cipher-" + Guid.NewGuid().ToString("N"));
        var created = new List<string>(); var parts = new List<PartRecord>(); var buffer = new byte[65536];
        try
        {
            Directory.CreateDirectory(directory); ciphertext.Position = 0;
            foreach (var part in candidate.Parts)
            {
                token.ThrowIfCancellationRequested(); var path = Path.Combine(directory, $"part-{part.Index:D8}.bin");
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true); created.Add(path);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long remaining = part.Length;
                while (remaining > 0)
                {
                    var read = await ciphertext.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                    if (read == 0) throw new EndOfStreamException("Verified ciphertext ended while staging.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token); remaining -= read;
                }
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(part.Sha256)))
                    throw new InvalidDataException("Verified ciphertext changed while staging.");
                await output.FlushAsync(token); output.Flush(flushToDisk: true); parts.Add(part with { StagingPath = path });
            }
            token.ThrowIfCancellationRequested();
            return candidate with { Parts = parts, Encryption = candidate.Encryption! with { RecoveryKey = envelope, StagingPath = null } };
        }
        catch
        {
            foreach (var path in created) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            try { Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    // The cipher supports rollback to the starting output position. This sink discards bytes after hashing;
    // rollback resets the digest and length, so failed authentication cannot leave a usable partial proof.
    private sealed class LogicalHashSink(long limit) : Stream
    {
        private IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long length; private bool disposed;
        public byte[] Finish() { ObjectDisposedException.ThrowIf(disposed, this); return hash.GetHashAndReset(); }
        public override bool CanRead => false;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => !disposed;
        public override long Length => length;
        public override long Position { get => length; set { if (value != length) throw new NotSupportedException(); } }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (buffer.Length > limit - length) throw new InvalidDataException("Authenticated plaintext exceeds the source size.");
            hash.AppendData(buffer); length += buffer.Length;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override void SetLength(long value)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (value != 0) throw new NotSupportedException();
            hash.Dispose(); hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); length = 0;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (!disposed) { hash.Dispose(); disposed = true; } base.Dispose(disposing); }
    }
}
