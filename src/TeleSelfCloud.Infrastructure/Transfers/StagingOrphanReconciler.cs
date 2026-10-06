using System.Globalization;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed record StagingReconciliationResult(bool CatalogReadable, int DeletedDirectories, int DeletedFiles, long DeletedBytes);

/// <summary>Reclaims only old, unreferenced files in generated upload-preparation locations.</summary>
public static class StagingOrphanReconciler
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public static async Task<StagingReconciliationResult> ReconcileAsync(
        string profileRoot, string stagingRoot, IManifestStore manifestStore, LocalProfileLease lease,
        DateTimeOffset nowUtc, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(manifestStore);
        ArgumentNullException.ThrowIfNull(lease);
        lease.RequireWithin(profileRoot);
        lease.RequireWithin(stagingRoot);
        var root = Path.GetFullPath(stagingRoot);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(root) || !Directory.Exists(root))
            return new StagingReconciliationResult(true, 0, 0, 0);

        IReadOnlyList<FileManifest> manifests;
        try { manifests = await manifestStore.ListAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch { return new StagingReconciliationResult(false, 0, 0, 0); }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var manifest in manifests)
            {
                token.ThrowIfCancellationRequested();
                foreach (var path in manifest.Parts.Select(part => part.StagingPath).Append(manifest.Encryption?.StagingPath))
                    if (!string.IsNullOrWhiteSpace(path)) referenced.Add(Path.GetFullPath(path));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new StagingReconciliationResult(false, 0, 0, 0);
        }

        var result = new MutableResult();
        ReconcileGeneratedPartDirectories(root, referenced, nowUtc, token, result);
        ReconcileEncryptedPreparationFiles(root, referenced, nowUtc, token, result);
        return new StagingReconciliationResult(true, result.Directories, result.Files, result.Bytes);
    }

    private sealed class MutableResult
    {
        public int Directories;
        public int Files;
        public long Bytes;
    }

    private static void ReconcileGeneratedPartDirectories(string root, HashSet<string> referenced,
        DateTimeOffset nowUtc, CancellationToken token, MutableResult result)
    {
        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                LocalFileSystemPathGuard.ContainsReparsePoint(directory)) continue;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var candidates = new List<string>(entries.Length);
            var eligible = true;
            foreach (var entry in entries)
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    var name = Path.GetFileName(entry);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                        !IsGeneratedPartName(name) || referenced.Contains(Path.GetFullPath(entry)) ||
                        IsRecent(File.GetLastWriteTimeUtc(entry), nowUtc))
                    { eligible = false; break; }
                    candidates.Add(entry);
                }
                catch (IOException) { eligible = false; break; }
                catch (UnauthorizedAccessException) { eligible = false; break; }
            }
            if (!eligible || IsRecent(Directory.GetLastWriteTimeUtc(directory), nowUtc)) continue;

            // Recheck the direct directory immediately before deleting; never recurse into unknown data.
            if (LocalFileSystemPathGuard.ContainsReparsePoint(directory)) continue;
            var removed = true;
            foreach (var file in candidates)
            {
                if (referenced.Contains(Path.GetFullPath(file)) || LocalFileSystemPathGuard.ContainsReparsePoint(file))
                { removed = false; break; }
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    result.Files++;
                    result.Bytes += length;
                }
                catch (IOException) { removed = false; break; }
                catch (UnauthorizedAccessException) { removed = false; break; }
            }
            if (!removed) continue;
            try { Directory.Delete(directory, recursive: false); result.Directories++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ReconcileEncryptedPreparationFiles(string root, HashSet<string> referenced,
        DateTimeOffset nowUtc, CancellationToken token, MutableResult result)
    {
        var directory = Path.Combine(root, "encrypted-preparation");
        if (LocalFileSystemPathGuard.ContainsReparsePoint(directory) || !Directory.Exists(directory)) return;
        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(Path.GetExtension(file), ".bin", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(name, "N", out _) || referenced.Contains(Path.GetFullPath(file)) ||
                LocalFileSystemPathGuard.ContainsReparsePoint(file) || IsRecent(File.GetLastWriteTimeUtc(file), nowUtc)) continue;
            try
            {
                var length = new FileInfo(file).Length;
                File.Delete(file);
                result.Files++;
                result.Bytes += length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsGeneratedPartName(string name) => name.Length == 17 &&
        name.StartsWith("part-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(name.AsSpan(5, 8), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool IsRecent(DateTime modifiedUtc, DateTimeOffset nowUtc) =>
        modifiedUtc > nowUtc.Subtract(Retention).UtcDateTime;
}
