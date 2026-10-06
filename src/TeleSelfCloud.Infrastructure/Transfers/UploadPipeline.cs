using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class UploadPipeline(
    ITransferCoordinator transferCoordinator,
    IUploadCapabilityProvider capabilityProvider,
    IPartTransport transport,
    IManifestStore manifestStore,
    IRemoteManifestPublisher remoteManifestPublisher,
    IUploadDeduplication? deduplication = null,
    LocalStagingContentStore? stagingContentStore = null) : IUploadPipeline
{
    public async Task<FileManifest> UploadAsync(
        string sourcePath,
        string stagingRoot,
        long partSizeBytes,
        CancellationToken cancellationToken,
        bool forceChunking = false,
        Func<TransferProgress, Task>? progressCallback = null,
        string? recoveryPassphrase = null)
    {
        var (manifest, capability) = await PrepareForUploadAsync(
            sourcePath, stagingRoot, partSizeBytes, cancellationToken, forceChunking, recoveryPassphrase);
        return await UploadPendingAsync(manifest, capability, recoverAmbiguousSend: false, cancellationToken, progressCallback);
    }

    public async Task<FileManifest> StageForUploadAsync(
        string sourcePath,
        string stagingRoot,
        long partSizeBytes,
        CancellationToken cancellationToken,
        bool forceChunking = false,
        string? recoveryPassphrase = null)
    {
        var (manifest, _) = await PrepareForUploadAsync(
            sourcePath, stagingRoot, partSizeBytes, cancellationToken, forceChunking, recoveryPassphrase);
        return manifest;
    }

    private async Task<(FileManifest Manifest, UploadCapability Capability)> PrepareForUploadAsync(
        string sourcePath,
        string stagingRoot,
        long partSizeBytes,
        CancellationToken cancellationToken,
        bool forceChunking,
        string? recoveryPassphrase)
    {
        if (partSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(partSizeBytes));
        cancellationToken.ThrowIfCancellationRequested();

        await capabilityProvider.RefreshAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var capability = await capabilityProvider.GetCurrentAsync(cancellationToken);
        if (capability is null || string.IsNullOrWhiteSpace(capability.AccountId) || capability.MaxFileBytes is null or <= 0)
            throw new InvalidOperationException("The active account upload limit is unknown. Upload is blocked; no Premium or unlimited capability is assumed.");
        FileManifest manifest;
        if (recoveryPassphrase is null)
        {
            var sourceSize = new FileInfo(sourcePath).Length;
            var effectivePartSize = !forceChunking && sourceSize <= capability.MaxFileBytes.Value
                ? Math.Max(sourceSize, 1)
                : Math.Min(partSizeBytes, capability.MaxFileBytes.Value);
            manifest = await transferCoordinator.PrepareAsync(sourcePath, stagingRoot, effectivePartSize, cancellationToken);
        }
        else
        {
            manifest = await PrepareEncryptedAsync(sourcePath, stagingRoot, partSizeBytes, capability, forceChunking,
                recoveryPassphrase, cancellationToken);
        }
        manifest = manifest with { AccountId = capability.AccountId };
        try
        {
            if (stagingContentStore is not null)
            {
                foreach (var part in manifest.Parts.Where(part => !string.IsNullOrWhiteSpace(part.StagingPath)))
                    await stagingContentStore.ProtectInPlaceAsync(part.StagingPath!, StagingFileIdentity.Part(manifest.FileId, part.Index),
                        part.Length, part.Sha256, cancellationToken);
                if (manifest.Encryption is { StagingPath: { } payloadPath } descriptor)
                    await stagingContentStore.ProtectInPlaceAsync(payloadPath, StagingFileIdentity.EncryptedPayload(manifest.FileId),
                        descriptor.PayloadSize, descriptor.PayloadSha256, cancellationToken);
            }
            await manifestStore.SaveAsync(manifest, CancellationToken.None);
        }
        catch
        {
            // A store may fail before commit or after a durable commit. Only discard newly prepared
            // bytes when a read-back confirms that no manifest owns them; read failures preserve data.
            try
            {
                if (await manifestStore.LoadAsync(manifest.FileId, CancellationToken.None) is null)
                    CleanupUnpersistedPreparation(manifest, stagingRoot);
            }
            catch { /* An uncertain catalog state must retain staging for recovery. */ }
            throw;
        }
        FileManifest? supersededEncryptedDraft = null;
        if (manifest.Encryption is not null && recoveryPassphrase is not null && deduplication is IEncryptedUploadDeduplication encryptedDedup)
        {
            var plan = await encryptedDedup.PlanEncryptedAsync(manifest, capability.MaxFileBytes.Value, recoveryPassphrase, cancellationToken);
            if (plan is not null)
            {
                if (plan.Parts.Any(p => p.Length > capability.MaxFileBytes.Value || p.CopySource is null || p.StagingPath is null))
                    throw new InvalidDataException("Encrypted duplicate plan requires bounded staged copy parts.");
                supersededEncryptedDraft = manifest;
                var adopted = manifest with { PartSizeBytes = plan.PartSizeBytes, Parts = plan.Parts, Encryption = plan.Encryption };
                ManifestValidator.ValidateStructure(adopted); cancellationToken.ThrowIfCancellationRequested();
                await manifestStore.SaveAsync(adopted, CancellationToken.None);
                manifest = adopted;
                CleanupSupersededEncryptedStaging(supersededEncryptedDraft, manifest, stagingRoot);
            }
        }
        if (deduplication is not null && manifest.Encryption is null)
        {
            if (deduplication is IUploadLayoutDeduplication layouts)
            {
                var candidate = await layouts.FindVerifiedLayoutAsync(manifest, capability.MaxFileBytes.Value, cancellationToken);
                if (candidate is not null)
                {
                    manifest = await DedupLayoutStaging.AdoptAsync(manifest, candidate, capability.MaxFileBytes.Value, cancellationToken,
                        stagingContentStore);
                    cancellationToken.ThrowIfCancellationRequested();
                    await manifestStore.SaveAsync(manifest, CancellationToken.None);
                }
            }
            else
            {
                var plan = await deduplication.PlanAsync(manifest, cancellationToken);
                if (plan is not null)
                {
                    if (plan.Count != manifest.Parts.Count) throw new InvalidDataException("The duplicate-copy plan does not match staged parts.");
                    manifest = manifest with { Parts = manifest.Parts.Select((part, index) => part with { CopySource = plan[index] }).ToArray() };
                    ManifestValidator.ValidateStructure(manifest);
                    await manifestStore.SaveAsync(manifest, CancellationToken.None);
                }
            }
        }
        return (manifest, capability);
    }

    private static void CleanupSupersededEncryptedStaging(FileManifest previous, FileManifest current, string stagingRoot)
    {
        var root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var retained = current.Parts.Select(p => p.StagingPath).Append(current.Encryption?.StagingPath)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var superseded = previous.Parts.Select(p => p.StagingPath).Append(previous.Encryption?.StagingPath)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in superseded)
        {
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || retained.Contains(path)) continue;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CleanupUnpersistedPreparation(FileManifest manifest, string stagingRoot)
    {
        if (!Guid.TryParseExact(manifest.FileId, "N", out _)) return;
        var root = Path.GetFullPath(stagingRoot);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(root)) return;
        var jobDirectory = Path.Combine(root, manifest.FileId);
        if (!LocalFileSystemPathGuard.ContainsReparsePoint(jobDirectory))
        {
            foreach (var part in manifest.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.StagingPath)) continue;
                var expected = Path.Combine(jobDirectory, $"part-{part.Index:D8}.bin");
                if (!string.Equals(Path.GetFullPath(part.StagingPath), expected, StringComparison.OrdinalIgnoreCase) ||
                    LocalFileSystemPathGuard.ContainsReparsePoint(expected)) continue;
                try { File.Delete(expected); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(jobDirectory, recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (manifest.Encryption?.StagingPath is { } encryptedPath)
        {
            var encryptedRoot = Path.Combine(root, "encrypted-preparation");
            if (LocalFileSystemPathGuard.ContainsReparsePoint(encryptedRoot)) return;
            // Encryption preparation uses a separate random UUID. Accept only that exact local shape.
            var fullEncryptedPath = Path.GetFullPath(encryptedPath);
            if (!string.Equals(Path.GetDirectoryName(fullEncryptedPath), encryptedRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(fullEncryptedPath), ".bin", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(fullEncryptedPath), "N", out _) ||
                LocalFileSystemPathGuard.ContainsReparsePoint(fullEncryptedPath)) return;
            try { File.Delete(fullEncryptedPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<FileManifest> ResumeAsync(
        string fileId,
        CancellationToken cancellationToken,
        Func<TransferProgress, Task>? progressCallback = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await capabilityProvider.RefreshAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var capability = await capabilityProvider.GetCurrentAsync(cancellationToken);
        if (capability is null || string.IsNullOrWhiteSpace(capability.AccountId) || capability.MaxFileBytes is null or <= 0)
            throw new InvalidOperationException("The active account upload limit is unknown. Resume is blocked until the capability is verified.");

        var manifest = await manifestStore.LoadAsync(fileId, cancellationToken)
            ?? throw new FileNotFoundException("No local transfer manifest exists for this file.", fileId);
        if (!string.Equals(manifest.AccountId, capability.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("This transfer belongs to a different Telegram account. Switch back to its owning account to resume.");
        return await UploadPendingAsync(manifest, capability, recoverAmbiguousSend: true, cancellationToken, progressCallback);
    }

    /// <summary>Explicit user fallback; caller must pause queue/own profile before changing a saved transfer.</summary>
    public async Task<FileManifest> SwitchPendingCopiesToUploadAsync(string fileId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); await capabilityProvider.RefreshAsync(token);
        var capability = await capabilityProvider.GetCurrentAsync(token);
        if (capability is null || string.IsNullOrWhiteSpace(capability.AccountId) || capability.MaxFileBytes is null or <= 0)
            throw new InvalidOperationException("The active account upload limit is unknown. Copy fallback is blocked.");
        var manifest = await manifestStore.LoadAsync(fileId, token) ?? throw new FileNotFoundException("The saved transfer is missing.");
        ManifestValidator.ValidateStructure(manifest);
        if (manifest.AccountId != capability.AccountId) throw new InvalidOperationException("The saved transfer belongs to another account.");
        if (manifest.Committed || !manifest.Parts.Any(p => p.CopySource is not null)) return manifest;
        if (transport is not IAcceptedPartRecovery recovery)
            throw new InvalidOperationException("Accepted-part history verification is required before changing copy intent.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var original = JsonSerializer.SerializeToUtf8Bytes(manifest, options);
        var parts = manifest.Parts.ToArray();
        foreach (var part in parts.Where(p => p.CopySource is not null))
        {
            token.ThrowIfCancellationRequested();
            if (part.Length > capability.MaxFileBytes.Value || part.StagingPath is null)
                throw new InvalidDataException("The staged copy part cannot be safely uploaded under the current account limit.");
            await using var staged = await MaterializePartAsync(manifest, part, token);
            if (staged.Stream.Length != part.Length || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(staged.Stream, token), Convert.FromHexString(part.Sha256)))
                throw new InvalidDataException("The staged copy part failed integrity validation. Its intent was kept.");
            var accepted = await recovery.FindAcceptedPartAsync(staged.Path, manifest.FileId, part.Index, token);
            parts[part.Index] = part with { CopySource = null, RemoteId = accepted, Confirmed = accepted is not null };
        }
        token.ThrowIfCancellationRequested();
        var current = await manifestStore.LoadAsync(fileId, token);
        if (current is null || !original.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(current, options)))
            throw new InvalidDataException("The saved transfer changed during fallback verification. Its latest state was kept.");
        var next = manifest with { Parts = parts, UpdatedAtUtc = parts.All(p => p.Confirmed) ? manifest.UpdatedAtUtc ?? DateTimeOffset.UtcNow : manifest.UpdatedAtUtc };
        ManifestValidator.ValidateStructure(next); await manifestStore.SaveAsync(next, CancellationToken.None); return next;
    }

    private async Task<FileManifest> UploadPendingAsync(
        FileManifest manifest,
        UploadCapability capability,
        bool recoverAmbiguousSend,
        CancellationToken cancellationToken,
        Func<TransferProgress, Task>? progressCallback)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManifestValidator.ValidateStructure(manifest);
        if (manifest.Parts.Any(p => p.CopySource is not null) && deduplication is null)
            throw new InvalidOperationException("This saved transfer requires its document-copy recovery service before resuming.");
        if (manifest.Committed)
        {
            manifest = await ClearCommittedEncryptedCacheAsync(manifest);
            await ReportProgressAsync(manifest, progressCallback);
            return manifest;
        }
        await ReportProgressAsync(manifest, progressCallback);
        var maxFileBytes = capability.MaxFileBytes!.Value;
        if (manifest.Parts.Any(p => p.Length > maxFileBytes))
            throw new InvalidOperationException("A staged part exceeds the active account's verified upload limit.");

        var tryRecoveryForFirstPendingPart = recoverAmbiguousSend;
        foreach (var part in manifest.Parts.OrderBy(p => p.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.Confirmed) continue;
            if (part.StagingPath is null || !File.Exists(part.StagingPath))
                throw new FileNotFoundException($"Staged data for part {part.Index} is missing.");
            // Hold a read lease through recovery/send/checkpoint so verified bytes cannot change meanwhile.
            await using var localPart = await MaterializePartAsync(manifest, part, cancellationToken);
            if (localPart.Stream.Length != part.Length)
                throw new InvalidDataException($"Staged part {part.Index} failed integrity validation.");
            var hash = await SHA256.HashDataAsync(localPart.Stream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(part.Sha256), hash))
                throw new InvalidDataException($"Staged part {part.Index} failed integrity validation.");

            string remoteId;
            if (tryRecoveryForFirstPendingPart)
            {
                tryRecoveryForFirstPendingPart = false;
                var recoveredId = transport is IAcceptedPartRecovery recovery
                    ? await recovery.FindAcceptedPartAsync(localPart.Path, manifest.FileId, part.Index, cancellationToken)
                    : null;
                remoteId = recoveredId ?? await SendPartAsync(manifest, part, manifest.FileId, cancellationToken);
            }
            else
            {
                remoteId = await SendPartAsync(manifest, part, manifest.FileId, cancellationToken);
            }

            var updatedParts = manifest.Parts.Select(p => p.Index == part.Index
                ? p with { RemoteId = remoteId, Confirmed = true, CopySource = null }
                : p).ToArray();
            // Persist the final metadata timestamp with the last accepted-part checkpoint.
            // Publication retries must replay the same metadata, not invent a newer update.
            manifest = manifest with { Parts = updatedParts,
                UpdatedAtUtc = updatedParts.All(p => p.Confirmed) ? DateTimeOffset.UtcNow : manifest.UpdatedAtUtc };
            await manifestStore.SaveAsync(manifest, CancellationToken.None);
            await ReportProgressAsync(manifest, progressCallback);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (manifest.UpdatedAtUtc is null)
        {
            // Legacy all-confirmed drafts may have no timestamp. Save one before publication.
            manifest = manifest with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            await manifestStore.SaveAsync(manifest, CancellationToken.None);
        }
        var committed = manifest with { Committed = true };
        await remoteManifestPublisher.PublishCommittedAsync(committed, cancellationToken);
        manifest = committed;
        await manifestStore.SaveAsync(manifest, CancellationToken.None);
        manifest = await ClearCommittedEncryptedCacheAsync(manifest);
        return manifest;
    }

    private async Task<string> SendPartAsync(FileManifest manifest, PartRecord part, string targetFileId, CancellationToken token)
    {
        if (part.CopySource is { } source) return await deduplication!.CopyAsync(part, source, targetFileId, token);
        await using var staged = await MaterializePartAsync(manifest, part, token);
        return await transport.UploadPartAsync(staged.Path, targetFileId, part.Index, token);
    }

    private Task<LocalStagingContentStore.MaterializedFile> MaterializePartAsync(
        FileManifest manifest, PartRecord part, CancellationToken token)
    {
        var path = part.StagingPath ?? throw new InvalidDataException("The upload part has no local staging path.");
        var identity = StagingFileIdentity.Part(manifest.FileId, part.Index);
        return stagingContentStore is null
            ? Task.FromResult(LocalStagingContentStore.MaterializedFile.Open(path))
            : stagingContentStore.MaterializeAsync(path, identity, token);
    }

    private static Task ReportProgressAsync(FileManifest manifest, Func<TransferProgress, Task>? callback)
    {
        if (callback is null) return Task.CompletedTask;
        var confirmed = manifest.Parts.Where(part => part.Confirmed).ToArray();
        return callback(new TransferProgress(
            manifest.FileId,
            confirmed.Sum(part => part.Length),
            manifest.TransferSize,
            confirmed.Length,
            manifest.Parts.Count));
    }

    private async Task<FileManifest> PrepareEncryptedAsync(
        string sourcePath, string stagingRoot, long partSizeBytes, UploadCapability capability,
        bool forceChunking, string passphrase, CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists) throw new FileNotFoundException("Source file was not found.", sourcePath);
        var sourceSize = sourceInfo.Length;
        byte[] fileKey = AesGcmFileCipher.CreateFileKey();
        var keyEnvelope = AesGcmFileCipher.WrapFileKey(fileKey, passphrase);
        var preparationDirectory = Path.Combine(stagingRoot, "encrypted-preparation");
        Directory.CreateDirectory(preparationDirectory);
        var encryptedPath = Path.Combine(preparationDirectory, Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            string sourceHash;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            {
                sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
                source.Position = 0;
                await using var encrypted = new FileStream(encryptedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true);
                await AesGcmFileCipher.EncryptAsync(source, encrypted, fileKey, cancellationToken: cancellationToken);
                await encrypted.FlushAsync(cancellationToken);
            }

            var encryptedInfo = new FileInfo(encryptedPath);
            long effectivePartSize = !forceChunking && encryptedInfo.Length <= capability.MaxFileBytes!.Value
                ? Math.Max(encryptedInfo.Length, 1)
                : Math.Min(partSizeBytes, capability.MaxFileBytes!.Value);
            var staged = await transferCoordinator.PrepareAsync(encryptedPath, stagingRoot, effectivePartSize, cancellationToken);
            return staged with
            {
                FileName = Path.GetFileName(sourcePath),
                LogicalSize = sourceSize,
                TotalSha256 = sourceHash,
                FileModifiedAtUtc = new DateTimeOffset(sourceInfo.LastWriteTimeUtc, TimeSpan.Zero),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Encryption = new EncryptedPayloadDescriptor(1, staged.LogicalSize, staged.TotalSha256, keyEnvelope, encryptedPath)
            };
        }
        catch
        {
            foreach (var file in Directory.Exists(preparationDirectory)
                         ? Directory.EnumerateFiles(preparationDirectory, Path.GetFileName(encryptedPath) + "*")
                         : Array.Empty<string>())
                try { File.Delete(file); } catch (IOException) { }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(fileKey); }
    }

    private async Task<FileManifest> ClearCommittedEncryptedCacheAsync(FileManifest manifest)
    {
        var stagingPath = manifest.Encryption?.StagingPath;
        if (!manifest.Committed || string.IsNullOrWhiteSpace(stagingPath)) return manifest;
        try
        {
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
            var cleared = manifest with { Encryption = manifest.Encryption! with { StagingPath = null } };
            await manifestStore.SaveAsync(cleared, CancellationToken.None);
            return cleared;
        }
        catch (IOException) { return manifest; }
        catch (UnauthorizedAccessException) { return manifest; }
    }
}
