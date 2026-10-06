using System.Text.Json;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class SqliteManifestStore(string databasePath, string? databaseKey = null) : IManifestStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private bool _schemaReady;

    public async Task SaveAsync(FileManifest manifest, CancellationToken cancellationToken)
    {
        ManifestValidator.ValidateStructure(manifest);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO Manifests (FileId, SchemaVersion, FileName, LogicalSize, AccountId, Json, UpdatedUtc)
                VALUES ($id, $version, $name, $size, $account, $json, $updated)
                ON CONFLICT(FileId) DO UPDATE SET
                    SchemaVersion = excluded.SchemaVersion,
                    FileName = excluded.FileName,
                    LogicalSize = excluded.LogicalSize,
                    AccountId = excluded.AccountId,
                    Json = excluded.Json,
                    UpdatedUtc = excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$id", manifest.FileId);
            command.Parameters.AddWithValue("$version", manifest.SchemaVersion);
            command.Parameters.AddWithValue("$name", manifest.FileName);
            command.Parameters.AddWithValue("$size", manifest.LogicalSize);
            command.Parameters.AddWithValue("$account", (object?)manifest.AccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(manifest, _jsonOptions));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<FileManifest?> LoadAsync(string fileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Json FROM Manifests WHERE FileId = $id;";
            command.Parameters.AddWithValue("$id", fileId);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (json is null) return null;
            var manifest = JsonSerializer.Deserialize<FileManifest>(json, _jsonOptions)
                ?? throw new InvalidDataException("Stored manifest is empty.");
            ManifestValidator.ValidateStructure(manifest);
            return manifest;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Json FROM Manifests ORDER BY UpdatedUtc DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<FileManifest>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var manifest = JsonSerializer.Deserialize<FileManifest>(reader.GetString(0), _jsonOptions)
                    ?? throw new InvalidDataException("Stored manifest is empty.");
                ManifestValidator.ValidateStructure(manifest);
                results.Add(manifest);
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteManyAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "DELETE FROM Manifests WHERE FileId = $id;";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ImportLegacyAsync(IManifestStore legacyStore, CancellationToken cancellationToken)
    {
        foreach (var manifest in await legacyStore.ListAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await LoadAsync(manifest.FileId, cancellationToken) is null)
                await SaveAsync(manifest, cancellationToken);
        }
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) => SqliteDatabase.OpenAsync(databasePath, databaseKey, cancellationToken);

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_schemaReady) return;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS Manifests (
                FileId TEXT PRIMARY KEY NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                FileName TEXT NOT NULL,
                LogicalSize INTEGER NOT NULL,
                AccountId TEXT NULL,
                Json TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Manifests_FileName ON Manifests(FileName);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _schemaReady = true;
    }
}
