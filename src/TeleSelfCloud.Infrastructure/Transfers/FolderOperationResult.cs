namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed record FolderOperationResult(string Path, int UpdatedFiles, bool FolderStatePublished, string? SyncError, bool KeptCurrentState = false);
