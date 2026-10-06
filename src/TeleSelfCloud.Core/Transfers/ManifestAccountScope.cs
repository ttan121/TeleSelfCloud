namespace TeleSelfCloud.Core.Transfers;

/// <summary>Limits remote manifests to their owner while keeping unbound local drafts usable.</summary>
public static class ManifestAccountScope
{
    public static bool IsVisible(FileManifest manifest, string? activeAccountId)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.IsNullOrWhiteSpace(manifest.AccountId))
            return !manifest.Committed;

        return !string.IsNullOrWhiteSpace(activeAccountId) &&
            string.Equals(manifest.AccountId, activeAccountId, StringComparison.Ordinal);
    }
}
