namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Exclusive process ownership of a local profile, held until application exit.</summary>
public sealed class LocalProfileLease : IDisposable
{
    private FileStream? stream;
    public string Root { get; }
    private LocalProfileLease(FileStream stream, string root) { this.stream = stream; Root = root; }
    public void RequireWithin(string directory)
    {
        if (stream is null) throw new ObjectDisposedException(nameof(LocalProfileLease));
        var path = Path.GetFullPath(directory);
        RejectReparsePoints(path);
        RejectReparsePoints(Root);
        if (!path.Equals(Root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The database migration requires ownership of its local profile.");
    }

    public static LocalProfileLease? TryAcquire(string profileRoot)
    {
        var root = Path.GetFullPath(profileRoot);
        RejectReparsePoints(root);
        Directory.CreateDirectory(root);
        RejectReparsePoints(root);
        var lockPath = Path.Combine(root, ".profile.lock");
        RejectReparsePoints(lockPath);
        if (Directory.Exists(lockPath))
            throw new InvalidDataException("The local profile lock path cannot be a directory.");
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                RejectReparsePoints(lockPath);
                return new LocalProfileLease(stream, root);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }

    private static void RejectReparsePoints(string path)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path))
            throw new InvalidDataException("The local profile path contains a filesystem link; profile ownership was not acquired.");
    }

    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}
