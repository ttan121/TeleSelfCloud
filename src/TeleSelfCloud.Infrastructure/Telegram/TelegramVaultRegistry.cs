using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed record TelegramVaultRegistryState(int SchemaVersion, string AccountId, long PrimaryChatId,
    long ActiveChatId, IReadOnlyList<TelegramStorageChannelInfo> Vaults,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool PrimaryVaultUsesIsolatedDirectory = false);

/// <summary>The first vault owns the unchanged legacy account directory; additional vaults have independent stores.</summary>
public sealed class TelegramVaultRegistry(string accountDirectory, string accountId, LocalRecordCipher? recordCipher = null,
    bool ownsCipher = false, bool requireExisting = false) : IDisposable
{
    private int disposed;
    private void CheckOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsCipher) recordCipher?.Dispose(); }
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record Envelope(TelegramVaultRegistryState State, string Sha256);
    private string RegistryPath => Path.Combine(Path.GetFullPath(accountDirectory), "vaults.json");
    private IDisposable Lease()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        Directory.CreateDirectory(accountDirectory);
        try { return new FileStream(RegistryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("The vault registry is busy. Retry after the current operation.", ex); }
    }
    public async Task<TelegramVaultRegistryState?> LoadAsync(CancellationToken token)
    {
        CheckOpen();
        if (!File.Exists(RegistryPath)) { if (requireExisting) throw Invalid(); return null; }
        await using var stream = new FileStream(RegistryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        if (stream.Length > 1024 * 1024 + (recordCipher is null ? 0 : 76)) throw Invalid();
        Envelope? envelope;
        try
        {
            if (recordCipher is null) envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Options, token);
            else
            {
                var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token);
                var plain = recordCipher.Unprotect(bytes);
                try { envelope = JsonSerializer.Deserialize<Envelope>(plain, Options); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        catch (JsonException ex) { throw Invalid(ex); }
        return ValidateEnvelope(envelope);
    }
    internal void ValidatePlaintext(byte[] plain)
    {
        if (plain.Length > 1024 * 1024) throw Invalid();
        try { ValidateEnvelope(JsonSerializer.Deserialize<Envelope>(plain, Options)); }
        catch (JsonException ex) { throw Invalid(ex); }
    }
    private TelegramVaultRegistryState ValidateEnvelope(Envelope? envelope)
    {
        Validate(envelope?.State);
        if (envelope!.Sha256 != Hash(envelope.State)) throw Invalid();
        return envelope.State;
    }
    public async Task<TelegramVaultRegistryState> RegisterAsync(TelegramStorageChannelInfo verifiedChannel, bool select, CancellationToken token)
    {
        CheckOpen();
        ValidateChannel(verifiedChannel);
        using var lease = Lease();
        var previous = await LoadAsync(token);
        var vaults = (previous?.Vaults ?? []).Where(v => v.ChatId != verifiedChannel.ChatId).Append(verifiedChannel).OrderBy(v => v.ChatId).ToArray();
        var next = new TelegramVaultRegistryState(1, accountId, previous?.PrimaryChatId ?? verifiedChannel.ChatId,
            select ? verifiedChannel.ChatId : previous?.ActiveChatId ?? verifiedChannel.ChatId, vaults,
            previous?.PrimaryVaultUsesIsolatedDirectory ?? false);
        await SaveAsync(next, token);
        return next;
    }

    /// <summary>Registers the first vault outside the legacy account root while retaining that root unchanged for recovery.</summary>
    public async Task<TelegramVaultRegistryState> RegisterIsolatedPrimaryAsync(TelegramStorageChannelInfo verifiedChannel, CancellationToken token)
    {
        CheckOpen();
        ValidateChannel(verifiedChannel);
        using var lease = Lease();
        if (await LoadAsync(token) is not null)
            throw new InvalidOperationException("An isolated primary vault can only be created for an account without a vault registry.");
        var next = new TelegramVaultRegistryState(1, accountId, verifiedChannel.ChatId, verifiedChannel.ChatId,
            [verifiedChannel], PrimaryVaultUsesIsolatedDirectory: true);
        await SaveAsync(next, token);
        return next;
    }
    public async Task<TelegramVaultRegistryState> SelectAsync(long chatId, CancellationToken token)
    {
        CheckOpen();
        using var lease = Lease();
        var previous = await LoadAsync(token) ?? throw Invalid();
        if (!previous.Vaults.Any(v => v.ChatId == chatId)) throw new InvalidOperationException("The selected vault is not registered for this account.");
        var next = previous with { ActiveChatId = chatId };
        await SaveAsync(next, token);
        return next;
    }
    public string GetDataDirectory(TelegramVaultRegistryState state, long chatId)
    {
        CheckOpen();
        Validate(state);
        if (!state.Vaults.Any(v => v.ChatId == chatId)) throw new InvalidOperationException("The selected vault is not registered for this account.");
        if (chatId == state.PrimaryChatId && !state.PrimaryVaultUsesIsolatedDirectory)
            return Path.GetFullPath(accountDirectory);
        return Path.Combine(Path.GetFullPath(accountDirectory), "vaults",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
    }

    public string GetIsolatedPrimaryDirectory(long chatId)
    {
        CheckOpen();
        if (chatId == 0) throw new ArgumentOutOfRangeException(nameof(chatId));
        return Path.Combine(Path.GetFullPath(accountDirectory), "vaults",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
    }
    private async Task SaveAsync(TelegramVaultRegistryState state, CancellationToken token)
    {
        Validate(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(state, Hash(state)), Options);
        if (bytes.Length > 1024 * 1024) throw Invalid();
        var plain = bytes;
        var temporary = RegistryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (recordCipher is not null) bytes = recordCipher.Protect(plain);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
            token.ThrowIfCancellationRequested();
            CheckOpen();
            File.Move(temporary, RegistryPath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(TelegramVaultRegistryState state) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state, Options)));
    private void Validate(TelegramVaultRegistryState? state)
    {
        if (state is null || state.SchemaVersion != 1 || state.AccountId != accountId || state.Vaults is null ||
            state.Vaults.Count is < 1 or > 256 || state.Vaults.Select(v => v?.ChatId).Distinct().Count() != state.Vaults.Count ||
            !state.Vaults.Any(v => v?.ChatId == state.PrimaryChatId) || !state.Vaults.Any(v => v?.ChatId == state.ActiveChatId)) throw Invalid();
        foreach (var channel in state.Vaults) ValidateChannel(channel);
    }
    private void ValidateChannel(TelegramStorageChannelInfo? channel)
    {
        if (channel is null || channel.AccountId != accountId || channel.ChatId == 0 ||
            string.IsNullOrWhiteSpace(channel.Title) || channel.Title.Length > 256 ||
            (channel.CreationRequestId is not null && (!Guid.TryParseExact(channel.CreationRequestId, "N", out var id) || id.ToString("N") != channel.CreationRequestId))) throw Invalid();
    }
    private static InvalidDataException Invalid(Exception? inner = null) => new("The vault registry is invalid. Existing profiles were kept; restore a registry backup before switching vaults.", inner);
}
