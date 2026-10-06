using System.Globalization;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Persists successful remote history high-watermarks per Telegram account and storage channel.</summary>
public sealed class SqliteRemoteSyncCheckpointStore(string databasePath, string? databaseKey = null) : IRemoteSyncCheckpointStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RemoteSyncCheckpoint?> LoadAsync(string accountId, long chatId, CancellationToken cancellationToken)
    {
        ValidateKey(accountId, chatId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT HighestMessageId, LastSuccessfulSyncUtc FROM RemoteSyncCheckpoints WHERE AccountId = $account AND ChatId = $chat;";
            command.Parameters.AddWithValue("$account", accountId);
            command.Parameters.AddWithValue("$chat", chatId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new RemoteSyncCheckpoint(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string accountId, long chatId, RemoteSyncCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ValidateKey(accountId, chatId);
        if (checkpoint.HighestMessageId < 0) throw new ArgumentOutOfRangeException(nameof(checkpoint));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO RemoteSyncCheckpoints (AccountId, ChatId, HighestMessageId, LastSuccessfulSyncUtc)
                VALUES ($account, $chat, $message, $updated)
                ON CONFLICT(AccountId, ChatId) DO UPDATE SET
                    HighestMessageId = excluded.HighestMessageId,
                    LastSuccessfulSyncUtc = excluded.LastSuccessfulSyncUtc;
                """;
            command.Parameters.AddWithValue("$account", accountId);
            command.Parameters.AddWithValue("$chat", chatId);
            command.Parameters.AddWithValue("$message", checkpoint.HighestMessageId);
            command.Parameters.AddWithValue("$updated", checkpoint.LastSuccessfulSyncUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<(long ChatId, RemoteSyncCheckpoint Checkpoint)>> ListAccountAsync(
        string accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("A Telegram account ID is required.", nameof(accountId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT ChatId, HighestMessageId, LastSuccessfulSyncUtc FROM RemoteSyncCheckpoints WHERE AccountId = $account;";
            command.Parameters.AddWithValue("$account", accountId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<(long, RemoteSyncCheckpoint)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add((reader.GetInt64(0), new RemoteSyncCheckpoint(
                    reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture))));
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("A Telegram account ID is required.", nameof(accountId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM RemoteSyncCheckpoints WHERE AccountId = $account;";
            command.Parameters.AddWithValue("$account", accountId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) => SqliteDatabase.OpenAsync(databasePath, databaseKey, cancellationToken);

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS RemoteSyncCheckpoints (
                AccountId TEXT NOT NULL,
                ChatId INTEGER NOT NULL,
                HighestMessageId INTEGER NOT NULL,
                LastSuccessfulSyncUtc TEXT NOT NULL,
                PRIMARY KEY (AccountId, ChatId)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateKey(string accountId, long chatId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("A Telegram account ID is required.", nameof(accountId));
        if (chatId == 0) throw new ArgumentOutOfRangeException(nameof(chatId));
    }
}
