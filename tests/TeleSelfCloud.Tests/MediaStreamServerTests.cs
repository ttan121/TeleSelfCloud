using System.Net;
using System.Diagnostics;
using System.Reflection;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class MediaStreamServerTests
{
    [Fact]
    public async Task ServesGetHeadAndSingleByteRangesWithCorrectHeaders()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("0123456789");
        var workflow = new FakeWorkflow(bytes);
        using var server = CreateServer(workflow);
        using var client = new HttpClient();
        var url = server.StartServer(Manifest("clip.mp4"));

        using var full = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("video/mp4", full.Content.Headers.ContentType?.MediaType);
        Assert.True(full.Headers.CacheControl?.NoStore);
        Assert.Equal(bytes, await full.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", full.Headers.AcceptRanges.Single());

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using var ranged = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal("bytes 2-5/10", ranged.Content.Headers.ContentRange?.ToString());
        Assert.Equal("2345", await ranged.Content.ReadAsStringAsync());

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        headRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(7, null);
        using var head = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.PartialContent, head.StatusCode);
        Assert.Equal("bytes 7-9/10", head.Content.Headers.ContentRange?.ToString());
        Assert.Equal(3, head.Content.Headers.ContentLength);
        Assert.Equal(1, workflow.RestoreCount);

        using var suffixRequest = new HttpRequestMessage(HttpMethod.Get, url);
        suffixRequest.Headers.TryAddWithoutValidation("Range", "bytes=-2");
        using var suffix = await client.SendAsync(suffixRequest);
        Assert.Equal(HttpStatusCode.PartialContent, suffix.StatusCode);
        Assert.Equal("89", await suffix.Content.ReadAsStringAsync());

        using var unsatisfiableRequest = new HttpRequestMessage(HttpMethod.Get, url);
        unsatisfiableRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, null);
        using var unsatisfiable = await client.SendAsync(unsatisfiableRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        Assert.Equal("bytes */10", unsatisfiable.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task RejectsInvalidRangesAndUnsupportedMethods()
    {
        var workflow = new FakeWorkflow("0123456789"u8.ToArray());
        using var server = CreateServer(workflow);
        using var client = new HttpClient();
        var url = server.StartServer(Manifest("clip.webm"));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Range", "bytes=1-2,4-5");
        using var invalid = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        Assert.Equal("bytes */10", invalid.Content.Headers.ContentRange?.ToString());

        using var post = await client.PostAsync(url, new StringContent("ignored"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal("GET, HEAD", string.Join(", ", post.Content.Headers.GetValues("Allow")));
    }

    [Fact]
    public async Task RequiresTokenAndStopsServingWhenPreviewCloses()
    {
        var workflow = new FakeWorkflow("clip"u8.ToArray());
        var scratchRoot = NewScratchRoot();
        var server = new MediaStreamServer(workflow, scratchRoot);
        using var client = new HttpClient();
        var url = server.StartServer(Manifest("clip.mkv"));
        var uri = new Uri(url);
        var invalidUrl = new UriBuilder(uri) { Query = "token=wrong" }.Uri;
        var tempRoot = scratchRoot;
        var filesBefore = Directory.Exists(tempRoot)
            ? Directory.GetFiles(tempRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var denied = await client.GetAsync(invalidUrl);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, workflow.RestoreCount);

        using var accepted = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("clip", await accepted.Content.ReadAsStringAsync());
        var ownedFiles = Directory.GetFiles(tempRoot).Where(path => !filesBefore.Contains(path)).ToArray();
        Assert.Single(ownedFiles);

        server.StopServer();
        Assert.All(ownedFiles, path => Assert.False(File.Exists(path)));
        try
        {
            using var stopped = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, stopped.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Kestrel may already have completed its asynchronous shutdown.
        }
        server.Dispose();
    }

    [Fact]
    public async Task AbruptProcessTerminationDeletesFileBackedPreviewTemp()
    {
        var marker = "crash-preview-" + Guid.NewGuid().ToString("N");
        var configuration = typeof(MediaStreamServerTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TeleSelfCloud.ProfileLeaseProbe/bin", configuration, "net10.0/TeleSelfCloud.ProfileLeaseProbe.dll"));
        Assert.True(File.Exists(probe), "Build the preview process probe alongside the test project.");

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add("--media-preview");
        start.ArgumentList.Add(marker);
        var scratchRoot = NewScratchRoot();
        start.ArgumentList.Add(scratchRoot);
        using var child = Process.Start(start)!;
        try
        {
            var url = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.StartsWith("http://127.0.0.1:", url);
            using var client = new HttpClient();
            Assert.Equal(marker, await client.GetStringAsync(url));

            var tempRoot = scratchRoot;
            var ownedFile = Assert.Single(Directory.GetFiles(tempRoot), path => ReadPreviewFile(path) == marker);
            Assert.True(File.Exists(ownedFile));

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(File.Exists(ownedFile), "DeleteOnClose should remove plaintext preview data when Windows closes handles for a terminated process.");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentProbesRestoreThePreviewOnlyOnce()
    {
        var workflow = new FakeWorkflow("0123456789"u8.ToArray(), delay: TimeSpan.FromMilliseconds(100));
        using var server = CreateServer(workflow);
        using var client = new HttpClient();
        var url = server.StartServer(Manifest("clip.ogg"));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetByteArrayAsync(url)));
        Assert.All(responses, bytes => Assert.Equal("0123456789"u8.ToArray(), bytes));
        Assert.Equal(1, workflow.RestoreCount);
    }

    [Fact]
    public async Task EmptyFileCanBeProbedButCannotSatisfyByteRanges()
    {
        var workflow = new FakeWorkflow([]);
        using var server = CreateServer(workflow);
        using var client = new HttpClient();
        var url = server.StartServer(Manifest("empty.mp4"));

        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var ranged = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, ranged.StatusCode);
        Assert.Equal("bytes */0", ranged.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task LazyByteSourceReceivesOnlyRequestedOffsetAndIsDisposedWithPreview()
    {
        var workflow = new FakeWorkflow("must-not-restore"u8.ToArray());
        var source = new FakeByteSource("0123456789"u8.ToArray());
        using var server = CreateServer(workflow);
        using var client = new HttpClient();
        var url = server.StartServer("clip.mp4", _ => Task.FromResult<IMediaByteSource>(source));

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(source.Reads);
        Assert.Equal(0, workflow.RestoreCount);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(3, 6);
        using var response = await client.SendAsync(request);
        Assert.Equal("3456", await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { (3L, 4) }, source.Reads);
        Assert.Equal(0, workflow.RestoreCount);

        server.StopServer();
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ClientDisconnectCancelsAnOutstandingLazyMediaRead()
    {
        var source = new BlockingByteSource(10);
        using var server = CreateServer(new FakeWorkflow("unused"u8.ToArray()));
        using var client = new HttpClient();
        using var requestCancellation = new CancellationTokenSource();
        var url = server.StartServer("clip.mp4", _ => Task.FromResult<IMediaByteSource>(source));

        var request = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        requestCancellation.Cancel();

        await source.ReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        server.StopServer();
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static string NewScratchRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.StreamTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static MediaStreamServer CreateServer(ILocalFileWorkflow workflow) => new(workflow, NewScratchRoot());

    private static FileManifest Manifest(string fileName) => new(
        SchemaVersion: 1, FileId: "preview-file", FileName: fileName, LogicalSize: 10,
        TotalSha256: new string('0', 64), PartSizeBytes: 10,
        Parts: [new PartRecord(0, 0, 10, new string('0', 64), "-100/1", true)], Committed: true);

    private static string ReadPreviewFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private sealed class FakeWorkflow(byte[] content, TimeSpan? delay = null) : ILocalFileWorkflow
    {
        private int _restoreCount;
        public int RestoreCount => Volatile.Read(ref _restoreCount);

        public Task<FileManifest> PrepareAsync(string sourcePath, string stagingRoot, long partSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task RestoreAsync(string fileId, string destinationPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _restoreCount);
            if (delay is { } pause) await Task.Delay(pause, cancellationToken);
            await File.WriteAllBytesAsync(destinationPath, content, cancellationToken);
        }
    }

    private sealed class FakeByteSource(byte[] content) : IMediaByteSource
    {
        public List<(long Offset, int Count)> Reads { get; } = [];
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> GetSizeAsync(CancellationToken cancellationToken) => Task.FromResult((long)content.Length);

        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add((offset, count));
            var length = (int)Math.Min(count, content.Length - offset);
            return Task.FromResult(content.AsSpan((int)offset, length).ToArray());
        }

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingByteSource(long size) : IMediaByteSource
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> GetSizeAsync(CancellationToken cancellationToken) => Task.FromResult(size);

        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            catch (OperationCanceledException)
            {
                ReadCancelled.TrySetResult();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
