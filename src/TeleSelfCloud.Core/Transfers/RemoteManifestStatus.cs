namespace TeleSelfCloud.Core.Transfers;

public static class RemoteManifestStatus
{
    public static bool IsUnknown(FileManifest manifest, IReadOnlySet<string> observedFileIds) =>
        manifest.Committed && !observedFileIds.Contains(manifest.FileId);
}
