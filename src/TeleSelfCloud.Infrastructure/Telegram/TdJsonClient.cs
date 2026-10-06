using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Thin binding to the official TDLib JSON client C interface.</summary>
public sealed class TdJsonClient : IAsyncDisposable
{
    private readonly IntPtr _library;
    private readonly IntPtr _client;
    private readonly SendDelegate _send;
    private readonly ReceiveDelegate _receive;
    private readonly DestroyDelegate _destroy;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private Task? _receiver;
    private int _started;

    public event EventHandler<JsonObject>? UpdateReceived;
    public event EventHandler<Exception>? ReceiveFaulted;

    public TdJsonClient(string nativeLibraryPath)
    {
        _library = NativeLibrary.Load(Path.GetFullPath(nativeLibraryPath));
        // TDLib debug logs include the full setTdlibParameters request, including api_hash.
        // Suppress native logs before creating a client so credentials are never sent to stdout/stderr.
        var setLogVerbosity = Marshal.GetDelegateForFunctionPointer<SetLogVerbosityDelegate>(
            NativeLibrary.GetExport(_library, "td_set_log_verbosity_level"));
        setLogVerbosity(0);
        _client = Marshal.GetDelegateForFunctionPointer<CreateDelegate>(NativeLibrary.GetExport(_library, "td_json_client_create"))();
        _send = Marshal.GetDelegateForFunctionPointer<SendDelegate>(NativeLibrary.GetExport(_library, "td_json_client_send"));
        _receive = Marshal.GetDelegateForFunctionPointer<ReceiveDelegate>(NativeLibrary.GetExport(_library, "td_json_client_receive"));
        _destroy = Marshal.GetDelegateForFunctionPointer<DestroyDelegate>(NativeLibrary.GetExport(_library, "td_json_client_destroy"));
    }

    public void StartReceiving()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The TDLib receive loop has already been started.");
        _receiver = Task.Run(ReceiveLoopAsync);
    }

    public void Send(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(request);
        _send(_client, payload);
    }

    public async Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("Start TDLib receiving before sending requests.");

        var extra = Guid.NewGuid().ToString("N");
        request["@extra"] = extra;
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(extra, completion)) throw new InvalidOperationException("Could not register TDLib request.");
        try
        {
            Send(request);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(extra, out _);
        }
    }

    private void CompletePendingRequest(JsonObject response)
    {
        var extra = response["@extra"]?.GetValue<string>();
        if (extra is not null && _pending.TryRemove(extra, out var completion))
        {
            if (response["@type"]?.GetValue<string>() == "error")
            {
                var code = response["code"]?.ToString() ?? "?";
                var detail = response["message"]?.GetValue<string>() ?? "Unknown TDLib request error";
                int? errorCode = int.TryParse(code, out var parsedCode) ? parsedCode : null;
                completion.TrySetException(TelegramRequestException.From(errorCode, detail));
            }
            else
            {
                completion.TrySetResult(response);
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var pointer = _receive(_client, 1.0);
                if (pointer == IntPtr.Zero) continue;
                var json = Marshal.PtrToStringUTF8(pointer);
                if (json is null) continue;
                if (JsonNode.Parse(json) is JsonObject update)
                {
                    CompletePendingRequest(update);
                    try { UpdateReceived?.Invoke(this, update); }
                    catch (Exception ex) { ReceiveFaulted?.Invoke(this, ex); }
                }
                await Task.Yield();
            }
            catch (JsonException)
            {
                // TDLib output should be JSON; ignore an isolated malformed response.
            }
            catch (Exception ex)
            {
                if (!_stop.IsCancellationRequested) ReceiveFaulted?.Invoke(this, ex);
                await Task.Delay(250);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var request in _pending.Values)
            request.TrySetException(new ObjectDisposedException(nameof(TdJsonClient)));
        _pending.Clear();
        if (_receiver is not null) await _receiver.ConfigureAwait(false);
        _destroy(_client);
        NativeLibrary.Free(_library);
        _stop.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLogVerbosityDelegate(int verbosityLevel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendDelegate(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string request);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ReceiveDelegate(IntPtr client, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(IntPtr client);
}
