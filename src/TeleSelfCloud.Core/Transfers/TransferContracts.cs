
using System.Text.Json.Serialization;

namespace TeleSelfCloud.Core.Transfers;

public sealed record UploadCapability(string AccountId, long? MaxFileBytes, DateTimeOffset ObservedAt, string Source);

public sealed record PartCopySource(string AccountId, string OwnerFileId, int Index, string RemoteId);
public sealed record PartRecord(int Index, long Offset, long Length, string Sha256, string? RemoteId, bool Confirmed, string? StagingPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartCopySource? CopySource = null);

/// <summary>Optional plaintext dedup plan. Saved draft intent must be honored independently of future opt-in changes.</summary>
public interface IUploadDeduplication
{
    Task<IReadOnlyList<PartCopySource>?> PlanAsync(FileManifest staged, CancellationToken token);
    Task<string> CopyAsync(PartRecord stagedPart, PartCopySource source, string targetFileId, CancellationToken token);
}

/// <summary>Optional verified candidate whose existing part layout can be adopted after local restaging.</summary>
public interface IUploadLayoutDeduplication : IUploadDeduplication
{
    Task<FileManifest?> FindVerifiedLayoutAsync(FileManifest staged, long maxPartBytes, CancellationToken token);
}

public sealed record EncryptedUploadCopyPlan(long PartSizeBytes, IReadOnlyList<PartRecord> Parts, EncryptedPayloadDescriptor Encryption);
public interface IEncryptedUploadDeduplication : IUploadDeduplication
{
    Task<EncryptedUploadCopyPlan?> PlanEncryptedAsync(FileManifest staged, long maxPartBytes, string newRecoveryPassphrase, CancellationToken token);
}

public sealed record PassphraseKeyEnvelope(
    int Version, string Kdf, int Iterations, string Cipher, string Salt, string Nonce, string Ciphertext, string Tag);

public sealed record EncryptedPayloadDescriptor(
    int Version, long PayloadSize, string PayloadSha256, PassphraseKeyEnvelope RecoveryKey, string? StagingPath = null);

public sealed record FileManifest(
    int SchemaVersion,
    string FileId,
    string FileName,
    long LogicalSize,
    string TotalSha256,
    long PartSizeBytes,
    IReadOnlyList<PartRecord> Parts,
    bool Committed,
    string? AccountId = null,
    string FolderPath = "",
    long Revision = 0,
    DateTimeOffset? UpdatedAtUtc = null,
    bool IsInTrash = false,
    DateTimeOffset? FileModifiedAtUtc = null,
    bool IsFavorite = false,
    bool IsArchived = false,
    bool IsHidden = false,
    EncryptedPayloadDescriptor? Encryption = null)
{
    public long TransferSize => Encryption?.PayloadSize ?? LogicalSize;
}

public interface IUploadCapabilityProvider
{
    Task<UploadCapability?> GetCurrentAsync(CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
}

public interface IPartTransport
{
    Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken cancellationToken);
    Task<Stream> DownloadPartAsync(string remoteId, CancellationToken cancellationToken);
}

/// <summary>Finds a previously accepted upload whose local checkpoint may have been lost.</summary>
public interface IAcceptedPartRecovery
{
    Task<string?> FindAcceptedPartAsync(string path, string fileId, int index, CancellationToken cancellationToken);
}

public interface IManifestStore
{
    Task SaveAsync(FileManifest manifest, CancellationToken cancellationToken);
    Task<FileManifest?> LoadAsync(string fileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken);
    Task DeleteManyAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken);
}

public sealed record LocalFolder(string AccountId, string Path, DateTimeOffset CreatedAtUtc);
public sealed record FolderTombstone(string AccountId, string Path, DateTimeOffset DeletedAtUtc);
public interface ILocalFolderStore
{
    Task<LocalFolder> CreateAsync(string accountId, string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<LocalFolder>> ListAsync(string accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FolderTombstone>> ListTombstonesAsync(string accountId, CancellationToken cancellationToken);
    Task RenameAsync(string accountId, string path, string newPath, CancellationToken cancellationToken);
    Task DeleteAsync(string accountId, string path, CancellationToken cancellationToken);
    Task MergeRemoteAsync(string accountId, IEnumerable<LocalFolder> folders, IEnumerable<FolderTombstone> tombstones, CancellationToken cancellationToken);
}
public enum BulkFileAction { Move, MoveToTrash }

public sealed record BulkFileActionResult(string FileId, bool Succeeded, string? Error);

public interface IFileBulkActions
{
    Task<IReadOnlyList<BulkFileActionResult>> ExecuteAsync(
        IEnumerable<string> fileIds, BulkFileAction action, string? destinationFolder,
        string accountId, CancellationToken cancellationToken);
}

public sealed record PermanentFileDeletionResult(string FileId, bool Succeeded, bool RemoteDeletionCompleted, string? Error);

public interface IPermanentFileDeletion
{
    Task<IReadOnlyList<PermanentFileDeletionResult>> ExecuteAsync(
        IEnumerable<string> fileIds, string accountId, CancellationToken cancellationToken);
}

public enum TransferQueueState
{
    Pending,
    Running,
    Paused,
    Failed,
    Completed,
    Cancelled
}

public enum TransferDirection
{
    Upload,
    Download
}

public sealed record TransferQueueItem(
    string TaskId,
    string FileId,
    string FileName,
    TransferDirection Direction,
    string? DestinationPath,
    TransferQueueState State,
    int AttemptCount,
    long TransferredBytes,
    long TotalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError);

public interface ITransferQueueStore
{
    Task EnsureAsync(string fileId, string fileName, long totalBytes, CancellationToken cancellationToken);
    Task EnqueueAsync(string fileId, string fileName, long totalBytes, CancellationToken cancellationToken);
    Task<TransferQueueItem> EnqueueDownloadAsync(string fileId, string fileName, string destinationPath, long totalBytes, CancellationToken cancellationToken);
    Task SetStateAsync(string taskId, TransferQueueState state, string? error, CancellationToken cancellationToken);
    Task UpdateProgressAsync(string taskId, long transferredBytes, long totalBytes, CancellationToken cancellationToken);
    Task RequeueForRetryAsync(string taskId, CancellationToken cancellationToken);
    Task DeleteTasksAsync(IEnumerable<string> taskIds, CancellationToken cancellationToken);
    Task DeleteItemsForFilesAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken);
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferQueueItem>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Optional atomic queue start used to skip work canceled while waiting for a runner slot.</summary>
public interface IAtomicTransferQueueStartStore
{
    Task<bool> TryStartAsync(string taskId, CancellationToken cancellationToken);
}

public sealed record TransferProgress(string FileId, long TransferredBytes, long TotalBytes, int CompletedParts, int PartCount);

public sealed record RemoteSyncCheckpoint(long HighestMessageId, DateTimeOffset LastSuccessfulSyncUtc);

public interface IRemoteSyncCheckpointStore
{
    Task<RemoteSyncCheckpoint?> LoadAsync(string accountId, long chatId, CancellationToken cancellationToken);
    Task SaveAsync(string accountId, long chatId, RemoteSyncCheckpoint checkpoint, CancellationToken cancellationToken);
}

public interface IRemoteManifestPublisher
{
    Task PublishCommittedAsync(FileManifest manifest, CancellationToken cancellationToken);
}

public interface ITransferCoordinator
{
    Task<FileManifest> PrepareAsync(string sourcePath, string stagingRoot, long partSizeBytes, CancellationToken cancellationToken);
    Task<FileManifest> ReassembleAsync(
        FileManifest manifest,
        string destinationPath,
        CancellationToken cancellationToken,
        Func<TransferProgress, Task>? progressCallback = null);
}

public interface IUploadPipeline
{
    Task<FileManifest> UploadAsync(
        string sourcePath,
        string stagingRoot,
        long partSizeBytes,
        CancellationToken cancellationToken,
        bool forceChunking = false,
        Func<TransferProgress, Task>? progressCallback = null,
        string? recoveryPassphrase = null);
    Task<FileManifest> ResumeAsync(string fileId, CancellationToken cancellationToken, Func<TransferProgress, Task>? progressCallback = null);
}


