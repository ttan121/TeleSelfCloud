using System.IO;
using System.Security.Cryptography;
using System.Text;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

/// <summary>Owns plaintext preview copies under the exclusively leased application profile.</summary>
internal static class LocalPreviewWorkspace
{
    private const string OwnerFileName = ".tsc-preview-owner";
    private static readonly byte[] OwnerBytes = "TeleSelfCloud/preview/v1"u8.ToArray();

    public static string Create(string profileRoot, LocalProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var root = GetPreviewRoot(profileRoot);
        RequireLease(root, profileRoot, lease);
        Directory.CreateDirectory(root);
        RejectLinks(root);

        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            RejectLinks(directory);
            using var marker = new FileStream(Path.Combine(directory, OwnerFileName), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            marker.Write(OwnerBytes);
            marker.Flush(flushToDisk: true);
            return directory;
        }
        catch
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    RejectLinksRecursively(directory);
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void CleanupOrphans(string profileRoot, LocalProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var root = GetPreviewRoot(profileRoot);
        if (!string.Equals(Path.GetFullPath(profileRoot), lease.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The preview workspace requires the matching application profile lease.");
        lease.RequireWithin(profileRoot);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(root)) return;
        if (!Directory.Exists(root)) return;

        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(root).ToArray(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (var directory in directories)
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            if (LocalFileSystemPathGuard.ContainsReparsePoint(directory)) continue;
            try { DeleteOwnedDirectory(directory); }
            catch (InvalidDataException) { /* Preserve linked or otherwise unsafe workspaces. */ }
            catch (IOException) { /* An open preview is preserved; startup can retry later. */ }
            catch (UnauthorizedAccessException) { /* Keep inaccessible recovery data intact. */ }
        }
    }

    public static void Delete(string profileRoot, string directory, LocalProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var root = GetPreviewRoot(profileRoot);
        RequireLease(root, profileRoot, lease);
        var target = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) return;
        try { DeleteOwnedDirectory(target); }
        catch (InvalidDataException) { /* Preserve linked or otherwise unsafe workspaces. */ }
        catch (IOException) { /* An external reader may still have the preview open. */ }
        catch (UnauthorizedAccessException) { /* Preserve data when cleanup is not permitted. */ }
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        RejectLinks(directory);
        if (!IsOwned(directory)) return;
        RejectLinksRecursively(directory);
        Directory.Delete(directory, recursive: true);
    }

    private static bool IsOwned(string directory)
    {
        var markerPath = Path.Combine(directory, OwnerFileName);
        if (!File.Exists(markerPath)) return false;
        var attributes = File.GetAttributes(markerPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        using var stream = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != OwnerBytes.Length) return false;
        Span<byte> actual = stackalloc byte[OwnerBytes.Length];
        stream.ReadExactly(actual);
        return CryptographicOperations.FixedTimeEquals(actual, OwnerBytes);
    }

    private static void RejectLinksRecursively(string root)
    {
        RejectLinks(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A preview workspace contains a filesystem link; it was preserved.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void RequireLease(string previewRoot, string profileRoot, LocalProfileLease lease)
    {
        var fullProfile = Path.GetFullPath(profileRoot);
        if (!string.Equals(fullProfile, lease.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The preview workspace requires the matching application profile lease.");
        lease.RequireWithin(previewRoot);
    }

    private static string GetPreviewRoot(string profileRoot) =>
        Path.GetFullPath(Path.Combine(profileRoot, "transient", "preview"));

    private static void RejectLinks(string path)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path))
            throw new InvalidDataException("The preview workspace contains a filesystem link.");
    }
}
