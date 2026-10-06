using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public static class EncryptedPayloadRestorer
{
    public static async Task RestoreAsync(
        FileManifest manifest, string encryptedPayloadPath, string destinationPath,
        string recoveryPassphrase, CancellationToken cancellationToken)
    {
        ManifestValidator.ValidateStructure(manifest);
        var descriptor = manifest.Encryption
            ?? throw new InvalidOperationException("The selected manifest does not contain an encrypted payload.");
        var payloadPath = Path.GetFullPath(encryptedPayloadPath);
        var destination = Path.GetFullPath(destinationPath);
        var payloadInfo = new FileInfo(payloadPath);
        if (!payloadInfo.Exists || payloadInfo.Length != descriptor.PayloadSize)
            throw new InvalidDataException("The encrypted payload size does not match its manifest.");
        await using (var payload = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
        {
            var payloadHash = await SHA256.HashDataAsync(payload, cancellationToken);
            if (!ManifestValidator.HashMatches(descriptor.PayloadSha256, payloadHash))
                throw new InvalidDataException("The encrypted payload failed its manifest SHA-256 check.");
        }

        byte[] key;
        try { key = AesGcmFileCipher.UnwrapFileKey(descriptor.RecoveryKey, recoveryPassphrase); }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The recovery passphrase is incorrect or the key envelope is damaged.", ex);
        }

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".tsc-decrypt-" + Guid.NewGuid().ToString("N") + ".partial");
        var ownsTemporary = false;
        try
        {
            await using (var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true))
            {
                ownsTemporary = true;
                await AesGcmFileCipher.DecryptAsync(input, output, key, cancellationToken);
                await output.FlushAsync(cancellationToken);
                if (output.Length != manifest.LogicalSize)
                    throw new InvalidDataException("Decrypted file size does not match its manifest.");
                output.Position = 0;
                var actualHash = await SHA256.HashDataAsync(output, cancellationToken);
                if (!ManifestValidator.HashMatches(manifest.TotalSha256, actualHash))
                    throw new InvalidDataException("Decrypted file failed its manifest SHA-256 check.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        catch
        {
            try { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
