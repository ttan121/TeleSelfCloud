using System.Text.Json.Nodes;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed record TelegramAppCredentials(int ApiId, string ApiHash);

public sealed class TelegramAuthSession : IAsyncDisposable, ITelegramUpdateSource
{
    private readonly TdJsonClient _client;
    private readonly string _databaseDirectory;
    private readonly TelegramAppCredentials _credentials;
    private readonly string _databaseEncryptionKey;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _initialized;
    private int _started;
    private int _authenticationInputPending;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? AuthenticationInputRejected;
    public event EventHandler<bool>? AuthenticationInputPendingChanged;
    public event EventHandler<JsonObject>? AuthorizationStateChanged;
    public event EventHandler<JsonObject>? AccountReceived;
    public event EventHandler<JsonObject>? UpdateReceived;
    public string CurrentAuthorizationState => _currentState;
    public bool AuthenticationInputPending => Volatile.Read(ref _authenticationInputPending) != 0;

    public TelegramAuthSession(string nativeLibraryPath, string databaseDirectory, TelegramAppCredentials credentials, string databaseEncryptionKey = "")
    {
        if (credentials.ApiId <= 0 || string.IsNullOrWhiteSpace(credentials.ApiHash))
            throw new ArgumentException("Valid Telegram application credentials are required.", nameof(credentials));
        _credentials = credentials;
        _databaseEncryptionKey = databaseEncryptionKey ?? throw new ArgumentNullException(nameof(databaseEncryptionKey));
        _databaseDirectory = Path.GetFullPath(databaseDirectory);
        Directory.CreateDirectory(_databaseDirectory);
        _client = new TdJsonClient(nativeLibraryPath);
        _client.UpdateReceived += OnUpdateReceived;
        _client.ReceiveFaulted += (_, error) => StatusChanged?.Invoke(this, $"TDLib receive failure: {error.Message}");
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The Telegram authorization session has already started.");
        _client.StartReceiving();
    }

    public void SubmitPhoneNumber(string phoneNumber) => SendWhen("authorizationStateWaitPhoneNumber", "setAuthenticationPhoneNumber", new JsonObject
    {
        ["phone_number"] = Required(phoneNumber, nameof(phoneNumber))
    });

    public void SubmitCode(string code) => SendWhen("authorizationStateWaitCode", "checkAuthenticationCode", new JsonObject
    {
        ["code"] = Required(code, nameof(code))
    });

    public void SubmitPassword(string password) => SendWhen("authorizationStateWaitPassword", "checkAuthenticationPassword", new JsonObject
    {
        ["password"] = Required(password, nameof(password))
    });

    public void SubmitEmailAddress(string email) => SendWhen("authorizationStateWaitEmailAddress", "setAuthenticationEmailAddress", new JsonObject
    {
        ["email_address"] = Required(email, nameof(email))
    });

    public void SubmitEmailCode(string code) => SendWhen("authorizationStateWaitEmailCode", "checkAuthenticationEmailCode", new JsonObject
    {
        ["code"] = new JsonObject { ["@type"] = "emailAddressAuthenticationCode", ["code"] = Required(code, nameof(code)) }
    });

    public void RegisterUser(string firstName, string lastName) => SendWhen("authorizationStateWaitRegistration", "registerUser", new JsonObject
    {
        ["first_name"] = Required(firstName, nameof(firstName)),
        ["last_name"] = lastName?.Trim() ?? "",
        ["disable_notification"] = true
    });

    public void LogOut() => _client.Send(new JsonObject { ["@type"] = "logOut" });

    public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        if (_currentState != "authorizationStateReady")
            throw new InvalidOperationException($"TDLib is not ready (current state: {_currentState}).");
        return _client.ExecuteAsync(request, cancellationToken);
    }

    public async Task<JsonObject> WaitForUpdateAsync(Func<JsonObject, bool> predicate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<JsonObject>? handler = null;
        handler = (_, update) =>
        {
            try
            {
                if (predicate(update)) completion.TrySetResult((JsonObject)update.DeepClone());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        };
        _client.UpdateReceived += handler;
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _client.UpdateReceived -= handler;
        }
    }

    private void OnUpdateReceived(object? sender, JsonObject update)
    {
        UpdateReceived?.Invoke(this, update);
        var updateType = update["@type"]?.GetValue<string>();
        if (updateType == "error")
        {
            if (update["@extra"] is not null) return;
            var code = update["code"]?.ToString() ?? "?";
            if (code == "406")
            {
                StatusChanged?.Invoke(this, "TDLib returned error 406. Check the application ID/hash and TDLib configuration; the error details are intentionally hidden.");
                return;
            }
            var message = update["message"]?.GetValue<string>() ?? "Unknown TDLib error";
            StatusChanged?.Invoke(this, $"TDLib error {code}: {message}");
            return;
        }
        if (updateType == "user" && update["id"] is not null)
        {
            AccountReceived?.Invoke(this, update);
            return;
        }
        if (updateType != "updateAuthorizationState" || update["authorization_state"] is not JsonObject state)
            return;

        var type = state["@type"]?.GetValue<string>() ?? "unknown";
        _currentState = type;
        if (type == "authorizationStateClosed") _closed.TrySetResult();
        AuthorizationStateChanged?.Invoke(this, state);
        StatusChanged?.Invoke(this, type);

        if (type == "authorizationStateWaitTdlibParameters" && Interlocked.Exchange(ref _initialized, 1) == 0)
            _client.Send(CreateParametersRequest());
        else if (type == "authorizationStateWaitEncryptionKey")
            _client.Send(CreateDatabaseEncryptionKeyRequest(_databaseEncryptionKey));
        else if (type == "authorizationStateReady")
            _client.Send(new JsonObject { ["@type"] = "getMe" });
    }

    private JsonObject CreateParametersRequest() => CreateTdlibParametersRequest(_databaseDirectory, _credentials, _databaseEncryptionKey);

    internal static JsonObject CreateTdlibParametersRequest(
        string databaseDirectory, TelegramAppCredentials credentials, string databaseEncryptionKey) => new()
    {
        ["@type"] = "setTdlibParameters",
        ["database_directory"] = Path.GetFullPath(databaseDirectory),
        ["files_directory"] = Path.Combine(Path.GetFullPath(databaseDirectory), "files"),
        ["database_encryption_key"] = databaseEncryptionKey,
        ["use_file_database"] = true,
        ["use_chat_info_database"] = false,
        ["use_message_database"] = false,
        ["use_secret_chats"] = false,
        ["api_id"] = credentials.ApiId,
        ["api_hash"] = credentials.ApiHash,
        ["system_language_code"] = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
        ["device_model"] = "Windows PC",
        ["system_version"] = Environment.OSVersion.VersionString,
        ["application_version"] = "0.1.0"
    };

    internal static JsonObject CreateDatabaseEncryptionKeyRequest(string databaseEncryptionKey) => new()
    {
        ["@type"] = "checkDatabaseEncryptionKey",
        ["encryption_key"] = databaseEncryptionKey
    };

    private void SendWhen(string expectedState, string method, JsonObject fields)
    {
        if (_currentState != expectedState)
            throw new InvalidOperationException($"Cannot send {method} while TDLib state is {_currentState}.");
        if (Interlocked.CompareExchange(ref _authenticationInputPending, 1, 0) != 0)
        {
            StatusChanged?.Invoke(this, "A Telegram sign-in request is still being processed. Wait for its response.");
            return;
        }
        fields["@type"] = method;
        AuthenticationInputPendingChanged?.Invoke(this, true);
        _ = SendAuthenticationInputAsync(fields);
    }

    private async Task SendAuthenticationInputAsync(JsonObject request)
    {
        try
        {
            await _client.ExecuteAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AuthenticationInputRejected?.Invoke(this, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _authenticationInputPending, 0);
            AuthenticationInputPendingChanged?.Invoke(this, false);
        }
    }

    private string _currentState = "authorizationStateWaitTdlibParameters";

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _started) != 0 && _currentState != "authorizationStateClosed")
        {
            _client.Send(new JsonObject { ["@type"] = "close" });
            try
            {
                await _closed.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                StatusChanged?.Invoke(this, "TDLib did not report authorizationStateClosed before timeout.");
            }
        }
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
