using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

/// <summary>Owns temporary copies handed to an external file application.</summary>
internal static class LocalOpenedFileWorkspace
{
    private const string OwnerFileName = ".tsc-open-owner";
    private static ReadOnlySpan<byte> Magic => "TSCOPE01"u8;
    private static readonly TimeSpan MinimumRetention = TimeSpan.FromDays(30);

    internal sealed record Workspace(string Directory, string FilePath);

    public static Workspace Create(string profileRoot, LocalProfileLease lease, string fileName)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The opened file name is invalid.");

        var root = GetRoot(profileRoot);
        RequireLease(root, profileRoot, lease);
        Directory.CreateDirectory(root);
        RejectLinks(root);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            RejectLinks(directory);
            var markerBytes = CreateMarker(DateTimeOffset.UtcNow);
            try
            {
                using var marker = new FileStream(Path.Combine(directory, OwnerFileName), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                marker.Write(markerBytes);
                marker.Flush(flushToDisk: true);
            }
            finally { CryptographicOperations.ZeroMemory(markerBytes); }
            var payloadDirectory = Path.Combine(directory, "payload");
            Directory.CreateDirectory(payloadDirectory);
            RejectLinks(payloadDirectory);
            return new Workspace(directory, Path.Combine(payloadDirectory, fileName));
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    public static void CleanupOrphans(string profileRoot, LocalProfileLease lease) =>
        CleanupOrphansAt(profileRoot, lease, DateTimeOffset.UtcNow);

    internal static void CleanupOrphansAt(string profileRoot, LocalProfileLease lease, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var root = GetRoot(profileRoot);
        ValidateProfileLease(profileRoot, lease);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(root) || !Directory.Exists(root)) return;

        string[] directories;
        try { directories = Directory.EnumerateDirectories(root).ToArray(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var now = nowUtc.ToUniversalTime();
        foreach (var directory in directories)
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                LocalFileSystemPathGuard.ContainsReparsePoint(directory)) continue;
            try
            {
                if (!TryReadCreationTime(directory, out var createdUtc) || now - createdUtc < MinimumRetention) continue;
                if (!TryGetPayloadLayout(directory, out var payload)) continue;
                if (payload is not null)
                {
                    var lastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(payload), TimeSpan.Zero);
                    if (lastWriteUtc > createdUtc && now - lastWriteUtc < MinimumRetention) continue;
                    using (new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                }
                DeleteOwnedDirectory(directory);
            }
            catch (InvalidDataException) { /* Keep malformed or linked workspaces for manual review. */ }
            catch (IOException) { /* The external application may still have the file open. */ }
            catch (UnauthorizedAccessException) { /* Preserve inaccessible files. */ }
        }
    }

    public static void Delete(string profileRoot, Workspace workspace, LocalProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(lease);
        var root = GetRoot(profileRoot);
        RequireLease(root, profileRoot, lease);
        var directory = Path.GetFullPath(workspace.Directory);
        if (!string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) return;
        try { DeleteOwnedDirectory(directory); }
        catch (InvalidDataException) { /* Preserve unsafe workspaces. */ }
        catch (IOException) { /* Preserve data if another reader holds it. */ }
        catch (UnauthorizedAccessException) { /* Preserve data when cleanup is not permitted. */ }
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        RejectLinksRecursively(directory);
        if (!TryReadCreationTime(directory, out _) || !TryGetPayloadLayout(directory, out _)) return;
        Directory.Delete(directory, recursive: true);
    }

    private static bool TryReadCreationTime(string directory, out DateTimeOffset createdUtc)
    {
        createdUtc = default;
        var path = Path.Combine(directory, OwnerFileName);
        if (!File.Exists(path)) return false;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != 16) return false;
        Span<byte> bytes = stackalloc byte[16];
        stream.ReadExactly(bytes);
        if (!CryptographicOperations.FixedTimeEquals(bytes[..8], Magic)) return false;
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        createdUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    private static bool TryGetSinglePayload(string directory, out string payload)
    {
        payload = string.Empty;
        if (!TryGetPayloadLayout(directory, out var found) || found is null) return false;
        payload = found;
        return true;
    }

    private static bool TryGetPayloadLayout(string directory, out string? payload)
    {
        payload = null;
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        var hasMarker = false;
        string? payloadDirectory = null;
        foreach (var entry in entries)
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            var name = Path.GetFileName(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!string.Equals(name, "payload", StringComparison.Ordinal) || payloadDirectory is not null) return false;
                payloadDirectory = entry;
            }
            else if (string.Equals(name, OwnerFileName, StringComparison.Ordinal)) hasMarker = true;
            else return false;
        }
        if (!hasMarker || payloadDirectory is null) return false;
        var files = Directory.EnumerateFileSystemEntries(payloadDirectory).ToArray();
        if (files.Length > 1) return false;
        if (files.Length == 1)
        {
            var attributes = File.GetAttributes(files[0]);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
            payload = files[0];
        }
        return true;
    }

    private static byte[] CreateMarker(DateTimeOffset createdUtc)
    {
        var bytes = new byte[16];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), createdUtc.ToUniversalTime().UtcTicks);
        return bytes;
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
                    throw new InvalidDataException("An opened-file workspace contains a filesystem link; it was preserved.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void RequireLease(string openedRoot, string profileRoot, LocalProfileLease lease)
    {
        ValidateProfileLease(profileRoot, lease);
        lease.RequireWithin(openedRoot);
    }

    private static void ValidateProfileLease(string profileRoot, LocalProfileLease lease)
    {
        if (!string.Equals(Path.GetFullPath(profileRoot), lease.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The opened-file workspace requires the matching application profile lease.");
        lease.RequireWithin(profileRoot);
    }

    private static string GetRoot(string profileRoot) => Path.GetFullPath(Path.Combine(profileRoot, "transient", "opened"));

    private static void RejectLinks(string path)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path))
            throw new InvalidDataException("The opened-file workspace contains a filesystem link.");
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !LocalFileSystemPathGuard.ContainsReparsePoint(directory))
            {
                RejectLinksRecursively(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
