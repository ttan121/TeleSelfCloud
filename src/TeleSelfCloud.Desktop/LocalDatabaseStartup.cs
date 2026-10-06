using System.IO;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public sealed record LocalDatabaseStartupProgress(string Root, int CatalogIndex, int CatalogCount, string Stage)
{
    public bool CanCancel => Stage is not ("Switching" or "Switched" or "ProtectingOriginals" or "Ready");
}

/// <summary>Enumerate application catalog locations only, never TDLib/cache/staging trees.</summary>
public static class LocalDatabaseStartup
{
    public static IReadOnlyList<string> CatalogRoots(string sharedRoot)
    {
        var root = Path.GetFullPath(sharedRoot); RejectLink(root);
        var roots = new List<string> { root };
        foreach (var account in Children(Path.Combine(root, "accounts"), 256))
        {
            roots.Add(account);
            roots.AddRange(Children(Path.Combine(account, "vaults"), 256));
        }
        return roots;
    }
    private static string[] Children(string directory, int limit)
    {
        RejectLink(directory); if (!Directory.Exists(directory)) return [];
        var children = Directory.EnumerateDirectories(directory).Take(limit + 1).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (children.Length > limit) throw new InvalidDataException("The catalog inventory exceeds its limit. All profiles were kept.");
        foreach (var child in children) RejectLink(child);
        return children;
    }
    private static void RejectLink(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The catalog inventory contains a filesystem link. All profiles were kept.");
    }
    public static async Task PrepareAsync(string sharedRoot, LocalProfileLease lease, Func<string, Task<bool>> recover, CancellationToken token, IProgress<LocalDatabaseStartupProgress>? progress = null)
    {
        lease.RequireWithin(sharedRoot);
        token.ThrowIfCancellationRequested();
        try { progress?.Report(new(Path.GetFullPath(sharedRoot), 0, 0, "Inventory")); } catch (Exception) { }
        token.ThrowIfCancellationRequested();
        var configured = await Task.Run(() => CatalogRoots(sharedRoot).Where(root => LocalDatabaseProtection.IsConfigured(root) || LocalCacheProtection.IsConfigured(root) || LocalVaultRegistryProtection.IsConfigured(root)).ToArray(), token);
        for (var i = 0; i < configured.Length; i++)
        {
            var root = configured[i]; var index = i + 1;
            void Report(string stage) { try { progress?.Report(new(root, index, configured.Length, stage)); } catch (Exception) { } }
            token.ThrowIfCancellationRequested();
            Report("Checking"); token.ThrowIfCancellationRequested();
            // Only unavailable wrapping keys invoke recovery. Bad journal/schema/storage
            // fails migration and must not be misrepresented as a passphrase problem.
            if (!LocalDatabaseProtection.KeyAvailable(root))
            {
                Report("Recovery");
                if (!await recover(root)) throw new OperationCanceledException("Local database key recovery was canceled.");
                if (!LocalDatabaseProtection.KeyAvailable(root)) throw new InvalidDataException("The existing database key must be recovered before opening stores.");
            }
            await Task.Run(() => LocalDatabaseProtection.MigrateOfflineAsync(root, lease, token, progress: Report), token);
            await Task.Run(() => LocalAccountStorageSettingsProtection.PrepareOffline(root, lease));
            // A late stop finishes the current switch/cleanup, then exits before stores open.
            token.ThrowIfCancellationRequested();
            await Task.Run(() => LocalCacheProtection.PrepareAsync(root, lease, token, Report), token);
            token.ThrowIfCancellationRequested();
            await Task.Run(() => LocalVaultRegistryProtection.PrepareAsync(root, lease, token, Report), token);
        }
    }
}
