using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Provides plaintext reads only through a disposable handle; protected files are fully authenticated first.</summary>
public sealed class LocalStagingContentStore(string profileRoot, LocalProfileLease lease,
    Func<string, LocalStagingCipher?> cipherFactory, bool allowLegacyPlaintext)
{
    private const string WorkspaceName = "staging";
    private const string MarkerName = ".tsc-upload-owner";
    private static ReadOnlySpan<byte> MarkerBytes => "TeleSelfCloud/upload-materialization/v1"u8;
    private string WorkspaceRoot => Path.GetFullPath(Path.Combine(profileRoot, "transient", WorkspaceName));

    public sealed class MaterializedFile : IDisposable, IAsyncDisposable
    {
        private readonly Action? release;
        private int disposed;
        public string Path { get; }
        public Stream Stream { get; }

        internal MaterializedFile(string path, Stream stream, Action? release)
        { Path = path; Stream = stream; this.release = release; }

        internal static MaterializedFile Open(string path, Action? release = null) =>
            new(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true), release);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { Stream.Dispose(); }
            finally { release?.Invoke(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await Stream.DisposeAsync(); }
            finally { release?.Invoke(); }
        }
    }

    public async Task<MaterializedFile> MaterializeAsync(string path, string identity, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(lease);
        lease.RequireWithin(profileRoot);
        var sourcePath = Path.GetFullPath(path);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(sourcePath))
            throw new InvalidDataException("The local staging path contains a filesystem link; its data was kept.");

        var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        try
        {
            var prefix = new byte[8];
            var prefixBytes = 0;
            while (prefixBytes < prefix.Length)
            {
                var read = await source.ReadAsync(prefix.AsMemory(prefixBytes), token);
                if (read == 0) break;
                prefixBytes += read;
            }
            source.Position = 0;
            var protectedFile = LocalStagingCipher.HasProtectedHeader(prefix.AsSpan(0, prefixBytes));
            CryptographicOperations.ZeroMemory(prefix);
            if (!protectedFile)
            {
                if (!allowLegacyPlaintext)
                    throw new InvalidDataException("This catalog requires protected staging. The unprotected file was kept.");
                token.ThrowIfCancellationRequested();
                return new MaterializedFile(sourcePath, source, release: null);
            }

            var cipher = cipherFactory(identity)
                ?? throw new InvalidDataException("Protected staging cannot be opened without its catalog key. The file was kept.");
            using (cipher)
            {
                var workspace = CreateWorkspace();
                var temporary = System.IO.Path.Combine(workspace, "payload.tmp");
                var payload = System.IO.Path.Combine(workspace, "payload.bin");
                try
                {
                    if (LocalFileSystemPathGuard.ContainsReparsePoint(workspace))
                        throw new InvalidDataException("The upload materialization workspace contains a filesystem link.");
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                     128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await cipher.DecryptAsync(source, output, token);
                        await output.FlushAsync(token);
                        output.Flush(flushToDisk: true);
                    }
                    token.ThrowIfCancellationRequested();
                    if (LocalFileSystemPathGuard.ContainsReparsePoint(temporary) || LocalFileSystemPathGuard.ContainsReparsePoint(payload))
                        throw new InvalidDataException("The upload materialization path contains a filesystem link.");
                    File.Move(temporary, payload);
                    source.Dispose();
                    return MaterializedFile.Open(payload, () => DeleteWorkspace(workspace));
                }
                catch
                {
                    DeleteWorkspace(workspace);
                    throw;
                }
            }
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    /// <summary>Authenticates and atomically protects a generated staging file when its owning catalog is protected.</summary>
    public async Task ProtectInPlaceAsync(string path, string identity, long expectedLength, string expectedSha256,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        lease.RequireWithin(profileRoot);
        var sourcePath = Path.GetFullPath(path);
        lease.RequireWithin(sourcePath);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(sourcePath))
            throw new InvalidDataException("The local staging path contains a filesystem link; its data was kept.");

        var prefix = new byte[8];
        await using (var probe = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
        {
            var read = await probe.ReadAsync(prefix, token);
            if (LocalStagingCipher.HasProtectedHeader(prefix.AsSpan(0, read)))
            {
                await using var alreadyProtected = await MaterializeAsync(sourcePath, identity, token);
                await VerifyPlaintextAsync(alreadyProtected.Stream, expectedLength, expectedSha256, token);
                CryptographicOperations.ZeroMemory(prefix);
                return;
            }
        }
        CryptographicOperations.ZeroMemory(prefix);

        using var cipher = cipherFactory(identity);
        if (cipher is null) return;

        string? workspace = null;
        try
        {
            workspace = CreateWorkspace();
            var temporary = Path.Combine(workspace, "protected.tmp");
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            {
                await VerifyPlaintextAsync(source, expectedLength, expectedSha256, token);
                source.Position = 0;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await cipher.EncryptAsync(source, output, expectedLength, token);
                    await output.FlushAsync(token);
                    output.Flush(flushToDisk: true);
                }
            }
            token.ThrowIfCancellationRequested();
            if (LocalFileSystemPathGuard.ContainsReparsePoint(sourcePath) || LocalFileSystemPathGuard.ContainsReparsePoint(temporary))
                throw new InvalidDataException("The local staging replacement path contains a filesystem link.");
            File.Move(temporary, sourcePath, overwrite: true);
        }
        finally { if (workspace is not null) DeleteWorkspace(workspace); }
    }

    private static async Task VerifyPlaintextAsync(Stream input, long expectedLength, string expectedSha256, CancellationToken token)
    {
        if (expectedLength < 0 || input.Length != expectedLength)
            throw new InvalidDataException("The local staging size does not match its manifest.");
        var actual = await SHA256.HashDataAsync(input, token);
        if (!ManifestValidator.HashMatches(expectedSha256, actual))
            throw new InvalidDataException("The local staging content does not match its manifest.");
    }

    public static void CleanupOrphans(string profileRoot, LocalProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.RequireWithin(profileRoot);
        var root = Path.GetFullPath(Path.Combine(profileRoot, "transient", WorkspaceName));
        if (LocalFileSystemPathGuard.ContainsReparsePoint(root) || !Directory.Exists(root)) return;
        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (var directory in directories)
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                LocalFileSystemPathGuard.ContainsReparsePoint(directory) || !IsOwned(directory)) continue;
            DeleteWorkspaceAt(root, directory);
        }
    }

    private string CreateWorkspace()
    {
        var root = WorkspaceRoot;
        lease.RequireWithin(root);
        Directory.CreateDirectory(root);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(root))
            throw new InvalidDataException("The upload materialization folder contains a filesystem link.");
        var directory = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (LocalFileSystemPathGuard.ContainsReparsePoint(directory))
                throw new InvalidDataException("The upload materialization workspace contains a filesystem link.");
            using var marker = new FileStream(System.IO.Path.Combine(directory, MarkerName), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            marker.Write(MarkerBytes);
            marker.Flush(flushToDisk: true);
            return directory;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private void DeleteWorkspace(string directory) => DeleteWorkspaceAt(WorkspaceRoot, directory);

    private static void DeleteWorkspaceAt(string root, string directory)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullDirectory = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(fullDirectory), fullRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(fullDirectory), "N", out _) ||
            LocalFileSystemPathGuard.ContainsReparsePoint(fullDirectory) || !IsOwned(fullDirectory)) return;
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(fullDirectory); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            try
            {
                if (name is not (MarkerName or "payload.bin" or "payload.tmp" or "protected.tmp") ||
                    (File.GetAttributes(entry) & FileAttributes.Directory) != 0 ||
                    LocalFileSystemPathGuard.ContainsReparsePoint(entry)) return;
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
        foreach (var entry in entries)
        {
            try { File.Delete(entry); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
        try { Directory.Delete(fullDirectory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsOwned(string directory)
    {
        var markerPath = System.IO.Path.Combine(directory, MarkerName);
        try
        {
            var attributes = File.GetAttributes(markerPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (marker.Length != MarkerBytes.Length) return false;
            Span<byte> actual = stackalloc byte[MarkerBytes.Length];
            marker.ReadExactly(actual);
            return CryptographicOperations.FixedTimeEquals(actual, MarkerBytes);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
