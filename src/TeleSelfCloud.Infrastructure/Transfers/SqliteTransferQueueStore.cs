using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Stores user-visible upload work beside the transfer manifests in SQLite.</summary>
public sealed class SqliteTransferQueueStore(string databasePath, string? databaseKey = null) : ITransferQueueStore, IAtomicTransferQueueStartStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _schemaReady;

    public async Task EnsureAsync(string fileId, string fileName, long totalBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileId)) throw new ArgumentException("A file ID is required.", nameof(fileId));
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("A file name is required.", nameof(fileName));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            var now = DateTimeOffset.UtcNow.ToString("O");
            command.CommandText = """
                INSERT OR IGNORE INTO TransferQueue (TaskId, FileId, Direction, DestinationPath, FileName, State, AttemptCount, TransferredBytes, TotalBytes, CreatedUtc, UpdatedUtc, LastError)
                VALUES ($id, $id, $upload, NULL, $name, $pending, 0, 0, $total, $now, $now, NULL);
                """;
            command.Parameters.AddWithValue("$id", fileId);
            command.Parameters.AddWithValue("$upload", TransferDirection.Upload.ToString());
            command.Parameters.AddWithValue("$name", fileName);
            command.Parameters.AddWithValue("$pending", TransferQueueState.Pending.ToString());
            command.Parameters.AddWithValue("$total", Math.Max(0, totalBytes));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task EnqueueAsync(string fileId, string fileName, long totalBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileId)) throw new ArgumentException("A file ID is required.", nameof(fileId));
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("A file name is required.", nameof(fileName));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            var now = DateTimeOffset.UtcNow.ToString("O");
            command.CommandText = """
                INSERT INTO TransferQueue (TaskId, FileId, Direction, DestinationPath, FileName, State, AttemptCount, TransferredBytes, TotalBytes, CreatedUtc, UpdatedUtc, LastError)
                VALUES ($id, $id, $upload, NULL, $name, $pending, 0, 0, $total, $now, $now, NULL)
                ON CONFLICT(TaskId) DO UPDATE SET
                    FileName = excluded.FileName,
                    TotalBytes = excluded.TotalBytes;
                """;
            command.Parameters.AddWithValue("$id", fileId);
            command.Parameters.AddWithValue("$upload", TransferDirection.Upload.ToString());
            command.Parameters.AddWithValue("$name", fileName);
            command.Parameters.AddWithValue("$pending", TransferQueueState.Pending.ToString());
            command.Parameters.AddWithValue("$total", Math.Max(0, totalBytes));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<TransferQueueItem> EnqueueDownloadAsync(
        string fileId,
        string fileName,
        string destinationPath,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileId)) throw new ArgumentException("A file ID is required.", nameof(fileId));
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("A file name is required.", nameof(fileName));
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("A download destination is required.", nameof(destinationPath));
        if (totalBytes < 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));

        var item = new TransferQueueItem(
            Guid.NewGuid().ToString("N"), fileId, fileName, TransferDirection.Download,
            Path.GetFullPath(destinationPath), TransferQueueState.Pending, 0, 0, totalBytes,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TransferQueue (TaskId, FileId, Direction, DestinationPath, FileName, State, AttemptCount, TransferredBytes, TotalBytes, CreatedUtc, UpdatedUtc, LastError)
                VALUES ($task, $file, $direction, $destination, $name, $state, 0, 0, $total, $created, $updated, NULL);
                """;
            command.Parameters.AddWithValue("$task", item.TaskId);
            command.Parameters.AddWithValue("$file", item.FileId);
            command.Parameters.AddWithValue("$direction", item.Direction.ToString());
            command.Parameters.AddWithValue("$destination", item.DestinationPath!);
            command.Parameters.AddWithValue("$name", item.FileName);
            command.Parameters.AddWithValue("$state", item.State.ToString());
            command.Parameters.AddWithValue("$total", item.TotalBytes);
            command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return item;
        }
        finally { _gate.Release(); }
    }

    public async Task ImportItemsAsync(IEnumerable<TransferQueueItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var imported = items.ToArray();
        if (imported.Length == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var item in imported)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO TransferQueue
                    (TaskId, FileId, Direction, DestinationPath, FileName, State, AttemptCount, TransferredBytes, TotalBytes, CreatedUtc, UpdatedUtc, LastError)
                    VALUES ($task, $file, $direction, $destination, $name, $state, $attempts, $transferred, $total, $created, $updated, $error);
                    """;
                command.Parameters.AddWithValue("$task", item.TaskId);
                command.Parameters.AddWithValue("$file", item.FileId);
                command.Parameters.AddWithValue("$direction", item.Direction.ToString());
                command.Parameters.AddWithValue("$destination", (object?)item.DestinationPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$name", item.FileName);
                command.Parameters.AddWithValue("$state", item.State.ToString());
                command.Parameters.AddWithValue("$attempts", item.AttemptCount);
                command.Parameters.AddWithValue("$transferred", item.TransferredBytes);
                command.Parameters.AddWithValue("$total", item.TotalBytes);
                command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
                command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$error", (object?)NormalizeError(item.LastError) ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SetStateAsync(
        string taskId,
        TransferQueueState state,
        string? error,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT State FROM TransferQueue WHERE TaskId = $id;";
            command.Parameters.AddWithValue("$id", taskId);
            var currentValue = await command.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new KeyNotFoundException("The transfer is not present in the saved queue.");
            if (!Enum.TryParse<TransferQueueState>(currentValue, out var current))
                throw new InvalidDataException("The saved transfer queue contains an unknown state.");
            if (!CanTransition(current, state))
                throw new InvalidOperationException($"The transfer cannot move from {current} to {state}.");

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE TransferQueue
                SET State = $state,
                    AttemptCount = AttemptCount + CASE WHEN $state = $running THEN 1 ELSE 0 END,
                    TransferredBytes = CASE WHEN $state = $completed AND TotalBytes > 0 THEN TotalBytes ELSE TransferredBytes END,
                    UpdatedUtc = $updated,
                    LastError = $error
                WHERE TaskId = $id;
                """;
            command.Parameters.AddWithValue("$state", state.ToString());
            command.Parameters.AddWithValue("$running", TransferQueueState.Running.ToString());
            command.Parameters.AddWithValue("$completed", TransferQueueState.Completed.ToString());
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$error", (object?)NormalizeError(error) ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", taskId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryStartAsync(string taskId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT State FROM TransferQueue WHERE TaskId = $id;";
            command.Parameters.AddWithValue("$id", taskId);
            var value = await command.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new KeyNotFoundException("The transfer is not present in the saved queue.");
            if (!Enum.TryParse<TransferQueueState>(value, out var current))
                throw new InvalidDataException("The saved transfer queue contains an unknown state.");
            if (current is not (TransferQueueState.Pending or TransferQueueState.Paused))
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE TransferQueue
                SET State = $running, AttemptCount = AttemptCount + 1, UpdatedUtc = $updated, LastError = NULL
                WHERE TaskId = $id AND State = $expected;
                """;
            command.Parameters.AddWithValue("$running", TransferQueueState.Running.ToString());
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", taskId);
            command.Parameters.AddWithValue("$expected", current.ToString());
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return changed == 1;
        }
        finally { _gate.Release(); }
    }

    public Task RequeueForRetryAsync(string taskId, CancellationToken cancellationToken) =>
        SetStateAsync(taskId, TransferQueueState.Pending, null, cancellationToken);

    public async Task UpdateProgressAsync(string taskId, long transferredBytes, long totalBytes, CancellationToken cancellationToken)
    {
        if (transferredBytes < 0 || totalBytes < 0 || transferredBytes > totalBytes)
            throw new ArgumentOutOfRangeException(nameof(transferredBytes), "Transfer progress must be between zero and total bytes.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE TransferQueue SET TransferredBytes = $bytes, TotalBytes = $total, UpdatedUtc = $updated WHERE TaskId = $task;";
            command.Parameters.AddWithValue("$bytes", transferredBytes);
            command.Parameters.AddWithValue("$total", totalBytes);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$task", taskId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new KeyNotFoundException("The transfer is not present in the saved queue.");
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteTasksAsync(IEnumerable<string> taskIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var ids = taskIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
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
                command.CommandText = "DELETE FROM TransferQueue WHERE TaskId = $id;";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteItemsForFilesAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken)
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
                command.CommandText = "DELETE FROM TransferQueue WHERE FileId = $id;";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE TransferQueue
                SET State = $paused,
                    LastError = 'The app closed before this transfer finished. Resume it when ready.',
                    UpdatedUtc = $updated
                WHERE State = $running;
                SELECT changes();
                """;
            command.Parameters.AddWithValue("$running", TransferQueueState.Running.ToString());
            command.Parameters.AddWithValue("$paused", TransferQueueState.Paused.ToString());
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TransferQueueItem>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TaskId, FileId, Direction, DestinationPath, FileName, State, AttemptCount, TransferredBytes, TotalBytes, CreatedUtc, UpdatedUtc, LastError
                FROM TransferQueue
                ORDER BY CreatedUtc DESC;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var items = new List<TransferQueueItem>();
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Enum.TryParse<TransferDirection>(reader.GetString(2), out var direction) ||
                    !Enum.TryParse<TransferQueueState>(reader.GetString(5), out var state))
                    throw new InvalidDataException("The saved transfer queue contains an unknown state.");
                items.Add(new TransferQueueItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(4),
                    direction,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    state,
                    reader.GetInt32(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture),
                    reader.IsDBNull(11) ? null : NormalizeError(reader.GetString(11))));
            }
            return items;
        }
        finally { _gate.Release(); }
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) => SqliteDatabase.OpenAsync(databasePath, databaseKey, cancellationToken);

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_schemaReady) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        var tableExistsCommand = connection.CreateCommand();
        tableExistsCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'TransferQueue';";
        var tableExists = await tableExistsCommand.ExecuteScalarAsync(cancellationToken) is not null;
        await tableExistsCommand.DisposeAsync();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tableExists)
        {
            var columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText = "PRAGMA table_info('TransferQueue');";
            await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.Transaction = (SqliteTransaction)transaction;
        if (!tableExists)
        {
            schemaCommand.CommandText = CreateTableSql("TransferQueue");
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (!columns.Contains("TaskId"))
        {
            schemaCommand.CommandText = CreateTableSql("TransferQueue_v2") + """
                INSERT INTO TransferQueue_v2 (TaskId, FileId, Direction, DestinationPath, FileName, State, AttemptCount, TransferredBytes, TotalBytes, CreatedUtc, UpdatedUtc, LastError)
                SELECT FileId, FileId, 'Upload', NULL, FileName, State, AttemptCount, 0, 0, CreatedUtc, UpdatedUtc, LastError
                FROM TransferQueue;
                DROP TABLE TransferQueue;
                ALTER TABLE TransferQueue_v2 RENAME TO TransferQueue;
                """;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        if (tableExists && !columns.Contains("TaskId"))
        {
            columns.Add("TransferredBytes");
            columns.Add("TotalBytes");
        }
        if (!tableExists)
        {
            columns.Add("TransferredBytes");
            columns.Add("TotalBytes");
        }
        if (!columns.Contains("TransferredBytes"))
        {
            schemaCommand.CommandText = "ALTER TABLE TransferQueue ADD COLUMN TransferredBytes INTEGER NOT NULL DEFAULT 0;";
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!columns.Contains("TotalBytes"))
        {
            schemaCommand.CommandText = "ALTER TABLE TransferQueue ADD COLUMN TotalBytes INTEGER NOT NULL DEFAULT 0;";
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        schemaCommand.CommandText = "CREATE INDEX IF NOT EXISTS IX_TransferQueue_State_CreatedUtc ON TransferQueue(State, CreatedUtc); CREATE INDEX IF NOT EXISTS IX_TransferQueue_FileId_Direction ON TransferQueue(FileId, Direction);";
        await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        await SanitizeLegacyErrorsAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _schemaReady = true;
    }

    private static async Task SanitizeLegacyErrorsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var replacements = new List<(string Id, string? Error)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT TaskId, LastError FROM TransferQueue WHERE LastError IS NOT NULL;";
            await using var reader = await select.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var original = reader.GetString(1); var normalized = NormalizeError(original);
                if (original != normalized) replacements.Add((reader.GetString(0), normalized));
            }
        }
        foreach (var replacement in replacements)
        {
            token.ThrowIfCancellationRequested();
            await using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE TransferQueue SET LastError = $error WHERE TaskId = $id;";
            update.Parameters.AddWithValue("$id", replacement.Id);
            update.Parameters.AddWithValue("$error", (object?)replacement.Error ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(token);
        }
    }

    private static string CreateTableSql(string tableName) => $"""
        CREATE TABLE {tableName} (
            TaskId TEXT PRIMARY KEY NOT NULL,
            FileId TEXT NOT NULL,
            Direction TEXT NOT NULL,
            DestinationPath TEXT NULL,
            FileName TEXT NOT NULL,
            State TEXT NOT NULL,
            AttemptCount INTEGER NOT NULL,
            TransferredBytes INTEGER NOT NULL,
            TotalBytes INTEGER NOT NULL,
            CreatedUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            LastError TEXT NULL
        );
        """;

    private static bool CanTransition(TransferQueueState current, TransferQueueState next) => (current, next) switch
    {
        (TransferQueueState.Pending, TransferQueueState.Running or TransferQueueState.Cancelled or TransferQueueState.Completed) => true,
        (TransferQueueState.Running, TransferQueueState.Paused or TransferQueueState.Failed or TransferQueueState.Completed) => true,
        (TransferQueueState.Paused, TransferQueueState.Running or TransferQueueState.Cancelled or TransferQueueState.Completed) => true,
        (TransferQueueState.Failed, TransferQueueState.Pending or TransferQueueState.Cancelled) => true,
        (TransferQueueState.Cancelled, TransferQueueState.Pending) => true,
        _ => false
    };

    private static string? NormalizeError(string? error)
    {
        return SafeFailure.Normalize(error);
    }
}

