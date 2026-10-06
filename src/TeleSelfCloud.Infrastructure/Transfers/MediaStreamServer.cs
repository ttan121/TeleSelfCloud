using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Serves a single lazy preview over loopback with request-scoped cancellation.</summary>
public sealed class MediaStreamServer : IMediaStreamServer, IDisposable
{
    private const int MaximumConcurrentRequests = 8;
    private readonly object _gate = new();
    private readonly ILocalFileWorkflow _workflow;
    private readonly string _scratchDirectory;
    private ActiveStream? _active;

    public MediaStreamServer(ILocalFileWorkflow workflow, string scratchDirectory)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        _scratchDirectory = Path.GetFullPath(scratchDirectory);
    }

    public string StartServer(FileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return StartServerCore(manifest.FileName, manifest, null, null);
    }

    public string StartServer(string fileName, Func<CancellationToken, Task<IMediaByteSource>> sourceFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        return StartServerCore(fileName, null, sourceFactory, null);
    }

    public string StartServer(string fileName, IMediaByteSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);
        return StartServerCore(fileName, null, null, source);
    }

    private string StartServerCore(string fileName, FileManifest? manifest,
        Func<CancellationToken, Task<IMediaByteSource>>? sourceFactory, IMediaByteSource? source)
    {
        ActiveStream stream;
        string address;
        lock (_gate)
        {
            var previous = _active;
            _active = null;
            previous?.Stop();

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var application = builder.Build();
            stream = new ActiveStream(application, _workflow, _scratchDirectory, fileName, manifest, sourceFactory, source, token);
            application.Run(stream.HandleRequestAsync);
            try
            {
                application.Start();
                address = application.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                    .Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                    ?? throw new InvalidOperationException("Kestrel did not report its loopback address.");
                var uri = new Uri(address);
                if (uri.Host != IPAddress.Loopback.ToString() || uri.Port is < 1 or > 65535)
                    throw new InvalidOperationException("The media preview server did not bind to a valid loopback endpoint.");
                _active = stream;
            }
            catch
            {
                stream.Stop();
                throw;
            }
        }

        var urlFileName = Uri.EscapeDataString(fileName).Replace("+", "%20");
        return $"{address.TrimEnd('/')}/{urlFileName}?token={stream.AccessToken}";
    }

    public void StopServer()
    {
        ActiveStream? previous;
        lock (_gate)
        {
            previous = _active;
            _active = null;
        }
        previous?.Stop();
    }

    public void Dispose() => StopServer();

    private sealed class ActiveStream(WebApplication application, ILocalFileWorkflow workflow, string scratchDirectory, string fileName, FileManifest? manifest,
        Func<CancellationToken, Task<IMediaByteSource>>? sourceFactory, IMediaByteSource? initialSource, string accessToken)
    {
        private readonly object _lifetimeGate = new();
        private readonly SemaphoreSlim _sourceGate = new(1, 1);
        private readonly SemaphoreSlim _restoreGate = new(1, 1);
        private readonly SemaphoreSlim _requestSlots = new(MaximumConcurrentRequests);
        private string? _filePath;
        private FileStream? _temporaryFileLease;
        private IMediaByteSource? _source = initialSource;
        private int _activeRequests;
        private bool _stopped;
        private bool _stopCancellationIssued;
        private bool _cleaned;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly string _scratchDirectory = scratchDirectory;

        public string AccessToken { get; } = accessToken;
        private CancellationToken Token => _cancellation.Token;

        public async Task HandleRequestAsync(HttpContext context)
        {
            if (!TryEnterRequest())
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                context.Response.Headers["Retry-After"] = "1";
                return;
            }

            if (!_requestSlots.Wait(0))
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                context.Response.Headers["Retry-After"] = "1";
                ExitRequest();
                return;
            }

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token, context.RequestAborted);
            var cancellationToken = requestCancellation.Token;
            try
            {
                var request = context.Request;
                var response = context.Response;
                response.Headers["Cache-Control"] = "no-store, max-age=0";
                response.Headers["Pragma"] = "no-cache";
                if (!FixedTimeTokenEquals(request.Query["token"].ToString(), AccessToken))
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return;
                }

                var isHead = HttpMethods.IsHead(request.Method);
                if (!isHead && !HttpMethods.IsGet(request.Method))
                {
                    response.Headers["Allow"] = "GET, HEAD";
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    return;
                }

                var source = await GetOrCreateSourceAsync(cancellationToken).ConfigureAwait(false);
                string? filePath = null;
                long size;
                if (source is not null)
                {
                    size = await source.GetSizeAsync(cancellationToken).ConfigureAwait(false);
                    if (size < 0) throw new InvalidDataException("The media source returned a negative size.");
                }
                else
                {
                    filePath = await GetOrRestoreAsync(cancellationToken).ConfigureAwait(false);
                    var file = new FileInfo(filePath);
                    if (!file.Exists)
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }
                    size = file.Length;
                }

                if (!MediaByteRange.TryParse(request.Headers["Range"].ToString(), size, out var range, out var hasRange))
                {
                    response.Headers["Content-Range"] = $"bytes */{size}";
                    response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                    response.ContentLength = 0;
                    return;
                }

                var start = hasRange ? range.Start : 0;
                var end = hasRange ? range.End : size - 1;
                var length = size == 0 ? 0 : end - start + 1;
                response.Headers["Accept-Ranges"] = "bytes";
                response.ContentType = GetContentType(fileName);
                response.ContentLength = length;
                response.StatusCode = hasRange ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
                if (hasRange) response.Headers["Content-Range"] = $"bytes {start}-{end}/{size}";
                if (isHead || length == 0) return;

                await using var input = source is null
                    ? new FileStream(filePath!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess)
                    : null;
                if (input is not null) input.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[64 * 1024];
                var remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    if (source is null)
                    {
                        var read = await input!.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException("The restored preview changed while it was being served.");
                        await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        remaining -= read;
                    }
                    else
                    {
                        var bytes = await source.ReadAsync(start + (length - remaining), requested, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            if (bytes.Length == 0 || bytes.Length > requested)
                                throw new EndOfStreamException("The media source returned an invalid range length.");
                            await response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                            remaining -= bytes.Length;
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) when (context.RequestAborted.IsCancellationRequested || Token.IsCancellationRequested) { }
            finally
            {
                if (context.RequestAborted.IsCancellationRequested) context.Abort();
                _requestSlots.Release();
                ExitRequest();
            }
        }

        private bool TryEnterRequest()
        {
            lock (_lifetimeGate)
            {
                if (_stopped) return false;
                _activeRequests++;
                return true;
            }
        }

        private void ExitRequest()
        {
            lock (_lifetimeGate)
            {
                _activeRequests--;
                CleanupIfDone();
            }
        }

        private async Task<IMediaByteSource?> GetOrCreateSourceAsync(CancellationToken cancellationToken)
        {
            if (sourceFactory is null) return _source;
            await _sourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_source is null)
                    _source = await sourceFactory(cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The media source factory returned null.");
                return _source;
            }
            finally { _sourceGate.Release(); }
        }

        private async Task<string> GetOrRestoreAsync(CancellationToken cancellationToken)
        {
            await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? path = null;
            FileStream? temporaryFileLease = null;
            try
            {
                if (_filePath is not null && File.Exists(_filePath)) return _filePath;
                Directory.CreateDirectory(_scratchDirectory);
                path = Path.Combine(_scratchDirectory, "stream-" + Guid.NewGuid().ToString("N") + ".media");
                var capturedManifest = manifest ?? throw new InvalidOperationException("A restore manifest is required for file-backed preview.");
                await workflow.RestoreAsync(capturedManifest.FileId, path, cancellationToken).ConfigureAwait(false);
                temporaryFileLease = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
                lock (_lifetimeGate)
                {
                    if (cancellationToken.IsCancellationRequested || _stopped)
                        throw new OperationCanceledException(cancellationToken);
                    _filePath = path;
                    _temporaryFileLease = temporaryFileLease;
                    temporaryFileLease = null;
                }
                return path;
            }
            catch
            {
                if (path is not null && _filePath is null) SafeDelete(path);
                throw;
            }
            finally
            {
                temporaryFileLease?.Dispose();
                _restoreGate.Release();
            }
        }

        public void Stop()
        {
            lock (_lifetimeGate)
            {
                if (_stopped) return;
                _stopped = true;
            }

            // Cancellation callbacks can run synchronously. Never invoke them under
            // the lifetime lock, since a callback may finish a request and re-enter it.
            _cancellation.Cancel();
            lock (_lifetimeGate)
            {
                _stopCancellationIssued = true;
                CleanupIfDone();
            }
            _ = StopApplicationAsync();
        }

        private async Task StopApplicationAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await application.StopAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                try { await application.DisposeAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
            }
        }

        private void CleanupIfDone()
        {
            if (!_stopped || !_stopCancellationIssued || _activeRequests != 0 || _cleaned) return;
            _cleaned = true;
            _temporaryFileLease?.Dispose();
            _temporaryFileLease = null;
            if (_filePath is not null) SafeDelete(_filePath);
            if (_source is not null) _ = DisposeSourceAsync(_source);
            _restoreGate.Dispose();
            _sourceGate.Dispose();
            _requestSlots.Dispose();
        }

        private static async Task DisposeSourceAsync(IMediaByteSource source)
        {
            try { await source.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        private static void SafeDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool FixedTimeTokenEquals(string supplied, string expected)
    {
        if (supplied.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(expected));
    }

    private static string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".aac" => "audio/aac",
        ".wav" => "audio/wav",
        ".ogg" or ".opus" => "audio/ogg",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream"
    };

}

internal readonly record struct MediaByteRange(long Start, long End)
{
    public static bool TryParse(string? header, long size, out MediaByteRange range, out bool hasRange)
    {
        range = default;
        hasRange = false;
        if (string.IsNullOrWhiteSpace(header)) return true;
        header = header.Trim();
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',')) return false;
        if (size <= 0) return false;
        var spec = header[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0 || spec.IndexOf('-', dash + 1) >= 0) return false;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            if (!long.TryParse(last, out var suffix) || suffix <= 0) return false;
            range = new(Math.Max(0, size - suffix), size - 1);
        }
        else
        {
            if (!long.TryParse(first, out var start) || start < 0 || start >= size) return false;
            if (last.Length == 0) range = new(start, size - 1);
            else if (!long.TryParse(last, out var requestedEnd) || requestedEnd < start) return false;
            else range = new(start, Math.Min(requestedEnd, size - 1));
        }
        hasRange = true;
        return true;
    }
}
