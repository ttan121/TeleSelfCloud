using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Tests;

public sealed class TelegramDatabaseKeyStoreTests
{
    [Fact]
    public void NewSessionKeyIsDpapiProtectedAndStableAcrossRestart()
    {
        var directory = NewDirectory();
        try
        {
            var first = TelegramDatabaseKeyStore.LoadOrCreate(directory);
            var second = TelegramDatabaseKeyStore.LoadOrCreate(directory);

            Assert.True(first.IsProtected);
            Assert.False(first.IsLegacyPlaintext);
            Assert.Equal(32, first.Value.Length);
            Assert.Equal(first, second);
            Assert.NotEqual(first.Value, File.ReadAllText(Path.Combine(directory, "tdlib-database-key.dpapi")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ExistingUnmarkedSessionRemainsUnchangedAsLegacyPlaintext()
    {
        var directory = NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "td.binlog"), "existing session fixture");

            var result = TelegramDatabaseKeyStore.LoadOrCreate(directory);

            Assert.False(result.IsProtected);
            Assert.True(result.IsLegacyPlaintext);
            Assert.Empty(result.Value);
            Assert.False(File.Exists(Path.Combine(directory, "tdlib-database-key.dpapi")));
            Assert.Equal("existing session fixture", File.ReadAllText(Path.Combine(directory, "td.binlog")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CorruptProtectedKeyFailsClosedInsteadOfReplacingIt()
    {
        var directory = NewDirectory();
        var keyPath = Path.Combine(directory, "tdlib-database-key.dpapi");
        try
        {
            File.WriteAllBytes(keyPath, [1, 2, 3, 4]);

            Assert.Throws<CryptographicException>(() => TelegramDatabaseKeyStore.LoadOrCreate(directory));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(keyPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void OversizedProtectedKeyIsRejectedAndPreserved()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "tdlib-database-key.dpapi");
        var bytes = new byte[16 * 1024 + 1];
        RandomNumberGenerator.Fill(bytes);
        try
        {
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => TelegramDatabaseKeyStore.LoadOrCreate(directory));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ValidFlushedTemporaryKeyIsPromotedInsteadOfCreatingAReplacement()
    {
        var sourceDirectory = NewDirectory();
        var targetDirectory = NewDirectory();
        try
        {
            var original = TelegramDatabaseKeyStore.LoadOrCreate(sourceDirectory);
            var protectedBytes = File.ReadAllBytes(Path.Combine(sourceDirectory, "tdlib-database-key.dpapi"));
            var temporaryPath = Path.Combine(targetDirectory, "tdlib-database-key.dpapi.tmp");
            File.WriteAllBytes(temporaryPath, protectedBytes);

            var recovered = TelegramDatabaseKeyStore.LoadOrCreate(targetDirectory);

            Assert.Equal(original.Value, recovered.Value);
            Assert.True(recovered.IsProtected);
            Assert.False(recovered.IsLegacyPlaintext);
            Assert.Equal(protectedBytes, File.ReadAllBytes(Path.Combine(targetDirectory, "tdlib-database-key.dpapi")));
            Assert.False(File.Exists(temporaryPath));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(File.ReadAllBytes(Path.Combine(sourceDirectory, "tdlib-database-key.dpapi")));
            Directory.Delete(sourceDirectory, recursive: true);
            Directory.Delete(targetDirectory, recursive: true);
        }
    }

    [Fact]
    public void DirectoryAtProtectedKeyPathDoesNotSelectLegacyPlaintextMode()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "tdlib-database-key.dpapi"));

            Assert.Throws<InvalidDataException>(() => TelegramDatabaseKeyStore.LoadOrCreate(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TelegramCredentialsRoundTripWithoutWritingCleartext()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "credentials.bin");
        const string apiHash = "0123456789abcdef0123456789abcdef";
        try
        {
            var store = new TelegramCredentialStore(path);
            store.Save(new TelegramAppCredentials(12345, apiHash));

            var stored = File.ReadAllBytes(path);
            Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(apiHash), stored);
            Assert.Equal(new TelegramAppCredentials(12345, apiHash), store.Load());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void OversizedCredentialFileIsRejectedAndPreserved()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "credentials.bin");
        var bytes = new byte[16 * 1024 + 1];
        RandomNumberGenerator.Fill(bytes);
        try
        {
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => new TelegramCredentialStore(path).Load());
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InvalidReplacementCredentialsDoNotModifyExistingCredentialFile()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "credentials.bin");
        try
        {
            var store = new TelegramCredentialStore(path);
            store.Save(new TelegramAppCredentials(12345, "existing-hash"));
            var original = File.ReadAllBytes(path);

            Assert.Throws<ArgumentException>(() => store.Save(new TelegramAppCredentials(67890, new string('x', 257))));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new TelegramAppCredentials(12345, "existing-hash"), store.Load());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TdlibReceivesDatabaseEncryptionKeyAtInitializationAndUnlock()
    {
        const string key = "0123456789abcdefghijklmnopqrstuv";
        var parameters = TelegramAuthSession.CreateTdlibParametersRequest(
            Path.Combine(Path.GetTempPath(), "tdlib-db"), new TelegramAppCredentials(123, "hash"), key);
        var unlock = TelegramAuthSession.CreateDatabaseEncryptionKeyRequest(key);

        Assert.Equal(key, parameters["database_encryption_key"]?.GetValue<string>());
        Assert.Equal(key, unlock["encryption_key"]?.GetValue<string>());
        Assert.Equal("setTdlibParameters", parameters["@type"]?.GetValue<string>());
        Assert.Equal("checkDatabaseEncryptionKey", unlock["@type"]?.GetValue<string>());
    }

    [Fact]
    public async Task NativeTdlibAcceptsDpapiKeyForFreshUnauthenticatedSession()
    {
        var nativePath = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "tdjson.dll");
        Assert.True(File.Exists(nativePath), $"TDLib native library not found: {nativePath}");
        var directory = NewDirectory();
        var storedKey = TelegramDatabaseKeyStore.LoadOrCreate(directory);
        Assert.True(storedKey.IsProtected);

        try
        {
            await VerifyUnauthenticatedStartupAsync(nativePath, directory, storedKey.Value);
            // A second native client reopens the same scratch profile, so the key must remain stable.
            await VerifyUnauthenticatedStartupAsync(nativePath, directory,
                TelegramDatabaseKeyStore.LoadOrCreate(directory).Value);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NativeTdlibRejectsDatabaseKeyChangeBeforeAuthentication()
    {
        var nativePath = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "tdjson.dll");
        Assert.True(File.Exists(nativePath), $"TDLib native library not found: {nativePath}");
        var directory = NewDirectory();
        try
        {
            await VerifyKeyChangeRequiresAuthenticationAsync(nativePath, directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task VerifyKeyChangeRequiresAuthenticationAsync(string nativePath, string directory)
    {
        await using var client = new TdJsonClient(nativePath);
        var waitForParameters = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitForPhoneNumber = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UpdateReceived += (_, update) =>
        {
            if (update["@type"]?.GetValue<string>() == "updateAuthorizationState" &&
                update["authorization_state"] is JsonObject authorizationState)
            {
                var authorizationStateType = authorizationState["@type"]?.GetValue<string>();
                if (authorizationStateType == "authorizationStateWaitTdlibParameters") waitForParameters.TrySetResult();
                if (authorizationStateType == "authorizationStateWaitPhoneNumber") waitForPhoneNumber.TrySetResult();
                if (authorizationStateType == "authorizationStateClosed") closed.TrySetResult();
            }
            else if (update["@type"]?.GetValue<string>() == "error")
            {
                var error = new InvalidOperationException($"TDLib setup failed: {update["message"]?.GetValue<string>()}");
                waitForParameters.TrySetException(error);
                waitForPhoneNumber.TrySetException(error);
            }
        };
        client.StartReceiving();
        var credentials = new TelegramAppCredentials(1, "0123456789abcdef0123456789abcdef");
        await waitForParameters.Task.WaitAsync(TimeSpan.FromSeconds(20));
        client.Send(TelegramAuthSession.CreateTdlibParametersRequest(directory, credentials, ""));

        await waitForPhoneNumber.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var error = await Assert.ThrowsAsync<TelegramRequestException>(() => client.ExecuteAsync(new JsonObject
        {
            ["@type"] = "setDatabaseEncryptionKey",
            ["new_encryption_key"] = "0123456789abcdefghijklmnopqrstuv"
        }).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(401, error.ErrorCode);
        client.Send(new JsonObject { ["@type"] = "close" });
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task VerifyUnauthenticatedStartupAsync(string nativePath, string directory, string key)
    {
        await using var session = new TelegramAuthSession(nativePath, directory,
            new TelegramAppCredentials(1, "0123456789abcdef0123456789abcdef"), key);
        var state = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AuthorizationStateChanged += (_, update) =>
        {
            var value = update["@type"]?.GetValue<string>();
            if (value == "authorizationStateWaitPhoneNumber") state.TrySetResult(value);
            else if (value == "authorizationStateClosed") state.TrySetException(new InvalidOperationException("TDLib closed before reaching the unauthenticated login state."));
        };
        session.StatusChanged += (_, status) =>
        {
            if (status.StartsWith("TDLib error", StringComparison.Ordinal) || status.StartsWith("TDLib receive failure", StringComparison.Ordinal))
                state.TrySetException(new InvalidOperationException(status));
        };
        session.Start();
        var actual = await state.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("authorizationStateWaitPhoneNumber", actual);
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-TelegramDbKey", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
