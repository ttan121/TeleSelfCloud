using System.Threading;
using System.Threading.Tasks;

namespace TeleSelfCloud.Core.Transfers;

public interface IMediaStreamServer
{
    string StartServer(FileManifest manifest);
    string StartServer(string fileName, Func<CancellationToken, Task<IMediaByteSource>> sourceFactory);
    string StartServer(string fileName, IMediaByteSource source);
    void StopServer();
}

/// <summary>Random-access media bytes. Returned arrays are owned by the caller and may be cleared after use.</summary>
public interface IMediaByteSource : IAsyncDisposable
{
    Task<long> GetSizeAsync(CancellationToken cancellationToken);
    Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken);
}
