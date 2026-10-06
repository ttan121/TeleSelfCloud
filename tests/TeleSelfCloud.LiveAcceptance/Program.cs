using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

if (!args.Contains("--run-live", StringComparer.Ordinal))
{
    Console.Error.WriteLine("Live Telegram upload tests require the explicit --run-live switch.");
    return 2;
}

var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeleSelfCloud", "P0");
var restartPrepare = args.Contains("--restart-prepare", StringComparer.Ordinal);
var restartResume = args.Contains("--restart-resume", StringComparer.Ordinal);
var deleteRestartFixture = args.Contains("--delete-restart-fixture", StringComparer.Ordinal);
var recreateMissingStorage = args.Contains("--recreate-missing-storage", StringComparer.Ordinal);
var inspectStorageCandidates = args.Contains("--inspect-storage-candidates-only", StringComparer.Ordinal);
if (new[] { restartPrepare, restartResume, deleteRestartFixture, recreateMissingStorage, inspectStorageCandidates }.Count(enabled => enabled) > 1)
    throw new ArgumentException("Choose only one live acceptance mode.");
string? requestedRunId = null;
if (restartResume || deleteRestartFixture)
{
    var option = restartResume ? "--restart-resume" : "--delete-restart-fixture";
    var runIdIndex = Array.IndexOf(args, option) + 1;
    if (runIdIndex >= args.Length || string.IsNullOrWhiteSpace(args[runIdIndex]) ||
        !System.Text.RegularExpressions.Regex.IsMatch(args[runIdIndex], @"^\d{8}-\d{6}-\d{3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        throw new ArgumentException($"{option} requires a valid run ID.");
    requestedRunId = args[runIdIndex];
}
var credentialPath = Path.Combine(appData, "telegram-app-credentials.bin");
var legacyChannelPath = Path.Combine(appData, "telegram-account", "storage-channel.json");
var accountOptionIndex = Array.IndexOf(args, "--account-id");
string? requestedAccountId = null;
var sessionDirectory = TelegramSessionProfileStore.ResolveForRestore(appData);
if (accountOptionIndex >= 0)
{
    if (accountOptionIndex + 1 >= args.Length || !long.TryParse(args[accountOptionIndex + 1], out var requestedNumericAccountId) || requestedNumericAccountId <= 0)
        throw new ArgumentException("--account-id requires a positive numeric Telegram account ID.");
    requestedAccountId = requestedNumericAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    sessionDirectory = TelegramSessionProfileStore.ResolveForRestore(appData, requestedAccountId);
}
if (recreateMissingStorage && requestedAccountId is null)
    throw new ArgumentException("--recreate-missing-storage requires --account-id so recovery stays on the explicitly selected test account.");
var nativePath = Path.Combine(AppContext.BaseDirectory, "tdjson.dll");
if (!File.Exists(nativePath)) throw new FileNotFoundException("TDLib native library was not found beside the acceptance runner.", nativePath);

var protectedBytes = await File.ReadAllBytesAsync(credentialPath);
byte[] clearBytes;
TelegramAppCredentials credentials;
try
{
    clearBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
    try
    {
        credentials = JsonSerializer.Deserialize<TelegramAppCredentials>(clearBytes)
            ?? throw new InvalidDataException("Saved Telegram API credentials could not be decoded.");
    }
    finally { CryptographicOperations.ZeroMemory(clearBytes); }
}
finally { CryptographicOperations.ZeroMemory(protectedBytes); }

var runId = requestedRunId ?? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
var reportPath = Path.Combine(Path.GetTempPath(), $"TeleSelfCloud-P0-Acceptance-{runId}.txt");
void Report(string message)
{
    File.AppendAllText(reportPath, message + Environment.NewLine);
    Console.WriteLine(message);
}
var tempRoot = Path.Combine(Path.GetTempPath(), restartPrepare || restartResume
    ? "TeleSelfCloud-P0-RestartAcceptance"
    : "TeleSelfCloud-P0-Acceptance", runId);
var downloadDirectory = Path.Combine(tempRoot, "downloads");
var stagingDirectory = Path.Combine(tempRoot, "staging");
var store = new SqliteManifestStore(Path.Combine(tempRoot, "acceptance.db"));
Directory.CreateDirectory(tempRoot);
var preserveTempRoot = restartPrepare;
TelegramAuthSession? session = null;
string? accountIdToRedact = null;
long? chatIdToRedact = null;
try
{
    session = new TelegramAuthSession(nativePath, sessionDirectory, credentials);
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    session.StatusChanged += (_, state) =>
    {
        Report($"TDLib: {state}");
        if (state == "authorizationStateReady") ready.TrySetResult();
        else if (state is "authorizationStateWaitPhoneNumber" or "authorizationStateWaitCode" or "authorizationStateWaitPassword" or "authorizationStateWaitEmailAddress" or "authorizationStateWaitEmailCode")
            ready.TrySetException(new InvalidOperationException($"The saved Telegram session requires local sign-in input ({state}); no credentials will be requested in this runner."));
    };
    session.Start();
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(90));
    var currentUser = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMe" }, CancellationToken.None);
    var activeAccountId = currentUser["id"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? throw new InvalidDataException("TDLib did not return the active Telegram account ID.");
    if (requestedAccountId is not null && !string.Equals(activeAccountId, requestedAccountId, StringComparison.Ordinal))
        throw new InvalidOperationException($"Requested account {requestedAccountId}, but TDLib restored account {activeAccountId}.");
    var channelPath = TelegramAccountProfileStore.GetStorageChannelSettingsPath(appData, activeAccountId, legacyChannelPath);
    if (inspectStorageCandidates)
    {
        var inspectionSettings = Path.Combine(tempRoot, "inspection-storage-channel.json");
        var candidate = await new TelegramStorageChannelService(session, inspectionSettings).FindExistingAsync(CancellationToken.None);
        Report(candidate is null
            ? "P1 STORAGE INSPECTION: no valid TeleSelfCloud channel found in the account's main chat list. No channel was created."
            : "P1 STORAGE INSPECTION: found and validated one owner-only private TeleSelfCloud channel. No channel was created or saved to the account profile.");
        return candidate is null ? 2 : 0;
    }
    if (recreateMissingStorage)
    {
        TelegramStorageChannelInfo? savedChannel = null;
        if (File.Exists(channelPath))
        {
            savedChannel = JsonSerializer.Deserialize<TelegramStorageChannelInfo>(await File.ReadAllTextAsync(channelPath))
                ?? throw new InvalidDataException("Saved storage channel configuration is empty.");
            if (!string.Equals(activeAccountId, savedChannel.AccountId, StringComparison.Ordinal))
                throw new InvalidOperationException("Saved storage channel belongs to a different account; no replacement was created.");
            try
            {
                await session.ExecuteAsync(new JsonObject { ["@type"] = "getChat", ["chat_id"] = savedChannel.ChatId }, CancellationToken.None);
                throw new InvalidOperationException("The saved storage channel is still accessible; replacement mode will not create or replace it.");
            }
            catch (TelegramRequestException ex) when (string.Equals(ex.Message, "Chat not found", StringComparison.OrdinalIgnoreCase))
            {
                // Explicit test-account recovery path: only replace a saved channel TDLib confirms is missing.
            }
        }

        var temporarySettingsPath = Path.Combine(tempRoot, "replacement-storage-channel.json");
        var storageService = new TelegramStorageChannelService(session, temporarySettingsPath);
        var replacement = await storageService.FindExistingAsync(CancellationToken.None)
            ?? await storageService.GetOrCreateAsync(createIfMissing: true, CancellationToken.None);
        if (savedChannel is not null && replacement.ChatId == savedChannel.ChatId)
            throw new InvalidDataException("Replacement storage resolved to the missing channel ID.");

        string? backupPath = null;
        if (File.Exists(channelPath))
        {
            backupPath = channelPath + $".missing-{runId}.bak";
            if (File.Exists(backupPath)) throw new IOException("The storage configuration backup path already exists.");
            File.Copy(channelPath, backupPath);
        }
        var stagedConfigPath = channelPath + ".tmp";
        File.Copy(temporarySettingsPath, stagedConfigPath, overwrite: true);
        File.Move(stagedConfigPath, channelPath, overwrite: true);
        var persistedChannel = JsonSerializer.Deserialize<TelegramStorageChannelInfo>(await File.ReadAllTextAsync(channelPath))
            ?? throw new InvalidDataException("The replacement storage configuration could not be reopened.");
        if (persistedChannel.ChatId != replacement.ChatId || persistedChannel.AccountId != activeAccountId)
            throw new InvalidDataException("The persisted replacement storage configuration did not match the verified channel.");
        accountIdToRedact = activeAccountId;
        chatIdToRedact = replacement.ChatId;
        Report($"P1 STORAGE RECOVERY: PASS; verified private channel for account {activeAccountId}; old configuration backup: {(backupPath is null ? "none" : Path.GetFileName(backupPath))}. Existing manifest/file data was not migrated or deleted.");
        return 0;
    }
    if (!File.Exists(channelPath)) throw new FileNotFoundException("No existing private storage channel is configured. The acceptance runner will not create one.");

    if (args.Contains("--account-only", StringComparer.Ordinal))
    {
        var isPremium = currentUser["is_premium"]?.GetValue<bool>() ?? false;
        Report($"Active account {activeAccountId}; tier from TDLib getMe: {(isPremium ? "Premium" : "Standard or not reported as Premium")}. Uploads use the conservative {TelegramUploadCapabilityProvider.ConservativePerFileLimitBytes:N0}-byte per-document ceiling regardless of tier.");
        return 0;
    }

    if (args.Contains("--inspect-history-only", StringComparer.Ordinal))
    {
        var savedChannel = JsonSerializer.Deserialize<TelegramStorageChannelInfo>(await File.ReadAllTextAsync(channelPath))
            ?? throw new InvalidDataException("Saved storage channel configuration is empty.");
        if (!string.Equals(activeAccountId, savedChannel.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Saved storage channel belongs to a different account.");
        var knownChats = await session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "getChats",
            ["chat_list"] = new JsonObject { ["@type"] = "chatListMain" },
            ["offset_order"] = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["offset_chat_id"] = 0,
            ["limit"] = 100
        }, CancellationToken.None);
        var knownChatIds = knownChats["chat_ids"] as JsonArray;
        Report($"Channel cache diagnostic: {knownChatIds?.Count ?? 0} main-list chats; saved channel {(knownChatIds?.Any(id => long.TryParse(id?.ToString(), out var chatId) && chatId == savedChannel.ChatId) == true ? "is" : "is not")} in returned IDs.");
        var archivedChats = await session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "getChats",
            ["chat_list"] = new JsonObject { ["@type"] = "chatListArchive" },
            ["offset_order"] = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["offset_chat_id"] = 0,
            ["limit"] = 100
        }, CancellationToken.None);
        var archivedChatIds = archivedChats["chat_ids"] as JsonArray;
        Report($"Archive diagnostic: {archivedChatIds?.Count ?? 0} archived chats; saved channel {(archivedChatIds?.Any(id => long.TryParse(id?.ToString(), out var chatId) && chatId == savedChannel.ChatId) == true ? "is" : "is not")} in returned IDs.");
        await session.ExecuteAsync(new JsonObject { ["@type"] = "getChat", ["chat_id"] = savedChannel.ChatId }, CancellationToken.None);
        var history = await session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "getChatHistory",
            ["chat_id"] = savedChannel.ChatId,
            ["from_message_id"] = 0,
            ["offset"] = 0,
            ["limit"] = 100,
            ["only_local"] = false
        }, CancellationToken.None);
        if (history["messages"] is JsonArray messages)
        {
            Report($"History diagnostic: Telegram returned {messages.Count} message(s).");
            foreach (var message in messages.OfType<JsonObject>())
            {
                var content = message["content"] as JsonObject;
                var document = content?["document"] as JsonObject;
                var caption = content?["caption"] as JsonObject;
                var captionText = caption?["text"]?.GetValue<string>();
                var captionKind = captionText?.StartsWith("TSC-MANIFEST|1|", StringComparison.Ordinal) == true ? "manifest" :
                    captionText?.StartsWith("TSC-PART|1|", StringComparison.Ordinal) == true ? "part" :
                    string.IsNullOrEmpty(captionText) ? "empty-or-missing" : "other";
                Report($"History shape: contentType={content?["@type"]?.GetValue<string>() ?? "missing"}; hasDocument={document is not null}; captionNodeType={content?["caption"]?.GetType().Name ?? "missing"}; hasCaptionText={!string.IsNullOrEmpty(captionText)}; captionKind={captionKind}.");
            }
        }
        return 0;
    }

    var channelService = new TelegramStorageChannelService(session, channelPath);
    var channel = await channelService.FindExistingAsync(CancellationToken.None)
        ?? throw new InvalidOperationException("No TeleSelfCloud private storage channel was found; the runner will not create one.");
    accountIdToRedact = channel.AccountId;
    chatIdToRedact = channel.ChatId;
    Report("PASS: Existing storage channel belongs to the current account and passed private/owner-only checks.");

    var transport = new TelegramFileTransport(session, channel.ChatId, downloadDirectory);
    transport.TransferStatusChanged += (_, status) => Report($"Transfer: {status}");
    var capability = new TelegramUploadCapabilityProvider(session);
    var remoteCatalog = new TelegramRemoteManifestCatalog(session, transport, store, channel.ChatId);
    remoteCatalog.ScanStatusChanged += (_, status) => Report(status);
    if (deleteRestartFixture)
    {
        var fixtureName = $"TSC-P0-ACCEPTANCE-restart-{runId}.bin";
        var matches = (await remoteCatalog.ImportRecentAsync(CancellationToken.None))
            .Where(manifest => manifest.Committed && manifest.FileName == fixtureName && manifest.AccountId == channel.AccountId)
            .ToArray();
        var fixture = matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException($"Expected exactly one committed acceptance fixture named {fixtureName}; found {matches.Length}. No remote delete was sent.");
        if (!fixture.IsInTrash)
        {
            fixture = FileManifestMetadata.SetTrashed(fixture, true);
            await new TelegramManifestPublisher(transport, Path.Combine(tempRoot, "remote-manifests"))
                .PublishCommittedAsync(fixture, CancellationToken.None);
            await store.SaveAsync(fixture, CancellationToken.None);
        }
        var queueStore = new SqliteTransferQueueStore(Path.Combine(tempRoot, "acceptance.db"));
        var remoteDeleter = new TelegramRemoteFileDeleter(session, transport, channel.ChatId, channel.AccountId);
        var results = await new PermanentFileDeletion(store, queueStore, remoteDeleter)
            .ExecuteAsync(new[] { fixture.FileId }, channel.AccountId, CancellationToken.None);
        var result = results.Single();
        if (!result.Succeeded)
            throw new InvalidOperationException($"Permanent deletion failed (remote deletion confirmed: {result.RemoteDeletionCompleted}): {result.Error}");
        Report($"P2 LIVE DELETE ACCEPTANCE: PASS. Deleted only the confirmed acceptance fixture {fixtureName}; other remote data was not selected.");
        return 0;
    }
    if (args.Contains("--scan-only", StringComparer.Ordinal))
    {
        var manifests = await remoteCatalog.ImportRecentAsync(CancellationToken.None);
        Report($"P0 REMOTE SCAN: PASS; imported {manifests.Count} manifest(s).");
        if (args.Contains("--inspect-history", StringComparer.Ordinal))
        {
            var history = await session.ExecuteAsync(new JsonObject
            {
                ["@type"] = "getChatHistory",
                ["chat_id"] = channel.ChatId,
                ["from_message_id"] = 0,
                ["offset"] = 0,
                ["limit"] = 100,
                ["only_local"] = false
            }, CancellationToken.None);
            if (history["messages"] is JsonArray historyMessages)
            {
                foreach (var message in historyMessages.OfType<JsonObject>())
                {
                    var content = message["content"] as JsonObject;
                    var document = content?["document"] as JsonObject;
                    var caption = content?["caption"] as JsonObject;
                    var captionText = caption?["text"]?.GetValue<string>();
                    var captionKind = captionText?.StartsWith("TSC-MANIFEST|1|", StringComparison.Ordinal) == true ? "manifest" :
                        captionText?.StartsWith("TSC-PART|1|", StringComparison.Ordinal) == true ? "part" :
                        string.IsNullOrEmpty(captionText) ? "empty-or-missing" : "other";
                    Report($"History shape: contentType={content?["@type"]?.GetValue<string>() ?? "missing"}; hasDocument={document is not null}; captionNode={content?["caption"]?.GetType().Name ?? "missing"}; hasCaptionText={!string.IsNullOrEmpty(captionText)}; captionKind={captionKind}.");
                }
            }
        }
        return 0;
    }
    var random = RandomNumberGenerator.Create();

    async Task<FileManifest> UploadGeneratedAsync(string suffix, int bytes, bool forceMultipart, bool interruptAfterFirstPart = false)
    {
        var source = Path.Combine(tempRoot, $"TSC-P0-ACCEPTANCE-{suffix}-{runId}.bin");
        await using (var output = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            var buffer = new byte[64 * 1024];
            var remaining = bytes;
            while (remaining > 0)
            {
                var take = Math.Min(buffer.Length, remaining);
                random.GetBytes(buffer.AsSpan(0, take));
                await output.WriteAsync(buffer.AsMemory(0, take));
                remaining -= take;
            }
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        IPartTransport uploadTransport = transport;
        if (interruptAfterFirstPart) uploadTransport = new CancelAfterFirstConfirmedTransport(transport, cancellation);
        var uploadPipeline = new UploadPipeline(new FileTransferCoordinator(), capability, uploadTransport, store,
            new TelegramManifestPublisher(transport, Path.Combine(tempRoot, "remote-manifests")));
        try
        {
            return await uploadPipeline.UploadAsync(source, stagingDirectory, 1024 * 1024, cancellation.Token, forceMultipart);
        }
        catch (OperationCanceledException) when (interruptAfterFirstPart)
        {
            var staged = (await store.ListAsync(CancellationToken.None)).SingleOrDefault(m => m.FileName == Path.GetFileName(source))
                ?? throw new InvalidDataException("Interrupted upload did not persist a local checkpoint.");
            if (staged.Committed || staged.Parts.Count < 2 || !staged.Parts[0].Confirmed)
                throw new InvalidDataException("Interruption did not leave the expected confirmed-part checkpoint.");
            Report($"PASS: Deliberately interrupted after {staged.Parts.Count(p => p.Confirmed)} of {staged.Parts.Count} confirmed parts.");
            if (restartPrepare) return staged;
            var resumed = await uploadPipeline.ResumeAsync(staged.FileId, CancellationToken.None);
            if (!resumed.Committed || resumed.Parts.Any(p => !p.Confirmed))
                throw new InvalidDataException("Resume did not commit all parts.");
            Report("PASS: Resume completed and committed the interrupted upload.");
            return resumed;
        }
        finally { cancellation.Dispose(); }
    }

    async Task VerifyRemoteRoundTripAsync(FileManifest uploaded, string label)
    {
        if (!uploaded.Committed || uploaded.Parts.Any(part => !part.Confirmed))
            throw new InvalidDataException($"{label} upload did not commit every part.");

        var imported = await remoteCatalog.ImportRecentAsync(CancellationToken.None);
        var remoteManifest = imported.SingleOrDefault(item => item.FileId == uploaded.FileId)
            ?? throw new InvalidDataException($"Remote scan did not discover the {label} manifest.");
        var destination = Path.Combine(tempRoot, $"restored-{label}.bin");
        var restored = await new FileTransferCoordinator(transport).ReassembleAsync(remoteManifest, destination, CancellationToken.None);
        var original = Directory.GetFiles(tempRoot, $"TSC-P0-ACCEPTANCE-{label}-{runId}.bin").Single();
        var sourceHash = await HashFileAsync(original);
        var restoredHash = await HashFileAsync(destination);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, restoredHash) ||
            !string.Equals(Convert.ToHexString(sourceHash), restored.TotalSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} round-trip SHA-256 did not match.");
        Report($"PASS: {label} remote manifest discovery and restore; parts={restored.Parts.Count}; bytes={restored.LogicalSize}; SHA-256={Convert.ToHexString(sourceHash)}.");
    }

    if (restartPrepare)
    {
        var partial = await UploadGeneratedAsync("restart", 3 * 1024 * 1024 + 29, true, true);
        if (partial.Committed || partial.Parts.Count(part => part.Confirmed) != 1)
            throw new InvalidDataException("Restart preparation did not leave exactly one confirmed part.");
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "restart-file-id.txt"), partial.FileId);
        Report($"PROCESS-RESTART PREPARED: run ID {runId}; the process will now exit with one confirmed part checkpointed. Start a new runner process with --run-live --restart-resume {runId}.");
        return 0;
    }

    if (restartResume)
    {
        var restartStatePath = Path.Combine(tempRoot, "restart-file-id.txt");
        var fileId = File.Exists(restartStatePath)
            ? await File.ReadAllTextAsync(restartStatePath)
            : (await store.ListAsync(CancellationToken.None))
                .SingleOrDefault(manifest => string.Equals(
                    manifest.FileName, $"TSC-P0-ACCEPTANCE-restart-{runId}.bin", StringComparison.Ordinal))?.FileId
                ?? throw new FileNotFoundException("The restart acceptance ID and its persisted manifest are both missing.");
        var partial = await store.LoadAsync(fileId, CancellationToken.None)
            ?? throw new FileNotFoundException("The checkpointed manifest was not present after restarting the runner process.");
        if (partial.Committed || partial.Parts.Count(part => part.Confirmed) != 1)
            throw new InvalidDataException("The persisted restart checkpoint is not in the expected state.");

        var resumePipeline = new UploadPipeline(new FileTransferCoordinator(), capability, transport, store,
            new TelegramManifestPublisher(transport, Path.Combine(tempRoot, "remote-manifests")));
        var completedAfterRestart = await resumePipeline.ResumeAsync(fileId, CancellationToken.None);
        if (!completedAfterRestart.Committed || completedAfterRestart.Parts.Any(part => !part.Confirmed))
            throw new InvalidDataException("The restarted process did not complete and commit every part.");
        Report("PASS: A new runner process reopened TDLib and SQLite, retained the confirmed checkpoint, and completed the upload.");

        var rebuiltStore = new SqliteManifestStore(Path.Combine(tempRoot, "catalog-rebuilt-after-db-loss.db"));
        var rebuiltCatalog = new TelegramRemoteManifestCatalog(session, transport, rebuiltStore, channel.ChatId);
        var rebuilt = await rebuiltCatalog.ImportRecentAsync(CancellationToken.None);
        var rebuiltManifest = rebuilt.SingleOrDefault(item => item.FileId == fileId)
            ?? throw new InvalidDataException("A clean local catalog did not rediscover the restarted upload from Telegram.");
        var restoredPath = Path.Combine(tempRoot, "restart-restored.bin");
        var restored = await new FileTransferCoordinator(transport).ReassembleAsync(rebuiltManifest, restoredPath, CancellationToken.None);
        var restoredHash = await HashFileAsync(restoredPath);
        if (!string.Equals(Convert.ToHexString(restoredHash), rebuiltManifest.TotalSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The restarted upload failed SHA-256 validation after a clean catalog rebuild.");
        Report($"PASS: Clean catalog rebuild discovered the committed remote manifest and restored {restored.LogicalSize} bytes with matching SHA-256.");
        Report("P0 PROCESS RESTART AND CATALOG REBUILD ACCEPTANCE: PASS. Generated remote test file remains marked TSC-P0-ACCEPTANCE; no existing data was deleted.");
        return 0;
    }

    var single = await UploadGeneratedAsync("single", 384 * 1024, false);
    if (single.Parts.Count != 1) throw new InvalidDataException("Single-part acceptance file was unexpectedly split.");
    await VerifyRemoteRoundTripAsync(single, "single");

    var multipart = await UploadGeneratedAsync("multipart", 3 * 1024 * 1024 + 173, true);
    if (multipart.Parts.Count < 2) throw new InvalidDataException("Forced multipart acceptance file was not split.");
    await VerifyRemoteRoundTripAsync(multipart, "multipart");

    var interrupted = await UploadGeneratedAsync("resume", 3 * 1024 * 1024 + 311, true, true);
    if (interrupted.Parts.Count < 2) throw new InvalidDataException("Resume acceptance file was not split.");
    await VerifyRemoteRoundTripAsync(interrupted, "resume");

    Report("P0 LIVE ACCEPTANCE: PASS. Three generated test files remain in the private Telegram storage channel, marked TSC-P0-ACCEPTANCE; no existing data was deleted.");
    return 0;
}
catch (Exception ex)
{
    preserveTempRoot = restartResume || deleteRestartFixture;
    var safeMessage = ex.Message
        .Replace(credentials.ApiHash, "[hidden API hash]", StringComparison.Ordinal)
        .Replace(credentials.ApiId.ToString(System.Globalization.CultureInfo.InvariantCulture), "[hidden API ID]", StringComparison.Ordinal);
    if (!string.IsNullOrWhiteSpace(accountIdToRedact)) safeMessage = safeMessage.Replace(accountIdToRedact, "[hidden account ID]", StringComparison.Ordinal);
    if (chatIdToRedact is { } chatId) safeMessage = safeMessage.Replace(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture), "[hidden chat ID]", StringComparison.Ordinal);
    Report($"P0 LIVE ACCEPTANCE: FAIL ({ex.GetType().Name}): {safeMessage}");
    return 1;
}
finally
{
    if (session is not null) await session.DisposeAsync();
    if (!preserveTempRoot)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(tempRoot, recursive: true); }
        catch (IOException) { Console.Error.WriteLine($"Local test artifacts remain at {tempRoot}"); }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine($"Local test artifacts remain at {tempRoot}"); }
    }
}

static async Task<byte[]> HashFileAsync(string path)
{
    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
    return await SHA256.HashDataAsync(input);
}

sealed class CancelAfterFirstConfirmedTransport(IPartTransport inner, CancellationTokenSource cancellation) : IPartTransport
{
    private int _uploaded;

    public async Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken cancellationToken)
    {
        var remoteId = await inner.UploadPartAsync(path, fileId, index, cancellationToken);
        if (Interlocked.Exchange(ref _uploaded, 1) == 0) cancellation.Cancel();
        return remoteId;
    }

    public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken cancellationToken) => inner.DownloadPartAsync(remoteId, cancellationToken);
}
