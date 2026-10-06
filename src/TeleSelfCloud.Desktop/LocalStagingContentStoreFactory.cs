using System.IO;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

internal static class LocalStagingContentStoreFactory
{
    public static LocalStagingContentStore Create(string profileRoot, string catalogRoot, LocalProfileLease lease)
    {
        var fullCatalogRoot = Path.GetFullPath(catalogRoot);
        return new LocalStagingContentStore(profileRoot, lease, identity =>
        {
            if (!LocalDatabaseProtection.IsConfigured(fullCatalogRoot)) return null;
            var status = LocalDatabaseProtection.Status(fullCatalogRoot);
            if (status.Stage != "Ready" || status.ProtectionId is null)
                throw new InvalidOperationException("The selected catalog's local protection is not ready; staging access is blocked.");
            return new LocalStagingCipher(LocalDatabaseProtection.DatabaseKey(fullCatalogRoot), status.ProtectionId, identity);
        }, allowLegacyPlaintext: true);
    }
}
