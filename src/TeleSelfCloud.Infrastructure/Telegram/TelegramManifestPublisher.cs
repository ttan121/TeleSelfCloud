using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed class TelegramManifestPublisher(TelegramFileTransport transport, string temporaryDirectory, VaultMetadataKey? metadataKey = null) : IRemoteManifestPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishCommittedAsync(FileManifest manifest, CancellationToken cancellationToken)
    {
        ManifestValidator.ValidateStructure(manifest);
        if (manifest.FileId.Contains('|') || manifest.FileId.Length > 512)
            throw new InvalidDataException("Manifest identity cannot be represented in a storage caption.");
        if (!manifest.Committed || manifest.Parts.Any(part => !part.Confirmed || string.IsNullOrWhiteSpace(part.RemoteId)))
            throw new InvalidDataException("Only a complete, committed manifest can be published to Telegram.");

        Directory.CreateDirectory(temporaryDirectory);
        var path = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.manifest.json");
        try
        {
            var portableManifest = manifest with
            {
                Parts = manifest.Parts.Select(part => part with { StagingPath = null, CopySource = null }).ToArray(),
                Encryption = manifest.Encryption is null ? null : manifest.Encryption with { StagingPath = null }
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(portableManifest, JsonOptions);
            if (metadataKey is not null)
            {
                if (manifest.AccountId != metadataKey.AccountId || transport.ChatId != metadataKey.ChatId || manifest.Parts.Any(p => TelegramRemoteMessageId.Parse(p.RemoteId!).ChatId != metadataKey.ChatId))
                    throw new InvalidDataException("The metadata key belongs to another account or vault.");
                var plaintext = bytes;
                try { bytes = await metadataKey.ProtectForReplayAsync(Path.Combine(temporaryDirectory, "protected-outbox"), "manifest", manifest.FileId, plaintext, cancellationToken); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            if (metadataKey is null) await transport.UploadManifestAsync(path, manifest.FileId, hash, cancellationToken);
            else await transport.UploadStorageDocumentAsync(path, $"TSC-MANIFEST|2|{manifest.FileId}|{hash}", cancellationToken);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
