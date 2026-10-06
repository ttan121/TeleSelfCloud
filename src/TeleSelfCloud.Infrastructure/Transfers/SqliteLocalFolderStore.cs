using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class SqliteLocalFolderStore(string databasePath, string? databaseKey = null) : ILocalFolderStore
{
	private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

	private bool _schemaReady;

	public async Task<LocalFolder> CreateAsync(string accountId, string path, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId, "accountId");
		string normalized = FileManifestMetadata.NormalizeFolderPath(path);
		if (normalized.Length == 0)
		{
			throw new ArgumentException("Folder path cannot be empty.", "path");
		}
		await _gate.WaitAsync(cancellationToken);
		try
		{
			LocalFolder result;
			await using (SqliteConnection connection = await OpenAsync(cancellationToken))
			{
				await EnsureSchemaAsync(connection, cancellationToken);
				LocalFolder localFolder;
				await using (DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
				{
					DateTimeOffset created = DateTimeOffset.UtcNow;
					string[] segments = normalized.Split('/');
					for (int i = 0; i < segments.Length; i++)
					{
						string folderPath = string.Join('/', segments.Take(i + 1));
						await using SqliteCommand command = connection.CreateCommand();
						command.Transaction = (SqliteTransaction)transaction;
						command.CommandText = "INSERT OR IGNORE INTO LocalFolders (AccountId, Path, CreatedUtc) VALUES ($account, $path, $created);";
						command.Parameters.AddWithValue("$account", accountId);
						command.Parameters.AddWithValue("$path", folderPath);
						command.Parameters.AddWithValue("$created", created.ToString("O"));
						await command.ExecuteNonQueryAsync(cancellationToken);
						await DeleteTombstoneAsync(connection, (SqliteTransaction)transaction, accountId, folderPath, cancellationToken);
					}
					await transaction.CommitAsync(cancellationToken);
					localFolder = new LocalFolder(accountId, normalized, created);
				}
				result = localFolder;
			}
			return result;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<IReadOnlyList<LocalFolder>> ListAsync(string accountId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId, "accountId");
		await _gate.WaitAsync(cancellationToken);
		try
		{
			IReadOnlyList<LocalFolder> result;
			await using (SqliteConnection connection = await OpenAsync(cancellationToken))
			{
				await EnsureSchemaAsync(connection, cancellationToken);
				IReadOnlyList<LocalFolder> readOnlyList2;
				await using (SqliteCommand command = connection.CreateCommand())
				{
					command.CommandText = "SELECT Path, CreatedUtc FROM LocalFolders WHERE AccountId = $account ORDER BY Path COLLATE NOCASE;";
					command.Parameters.AddWithValue("$account", accountId);
					IReadOnlyList<LocalFolder> readOnlyList;
					await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
					{
						List<LocalFolder> folders = new List<LocalFolder>();
						while (await reader.ReadAsync(cancellationToken))
						{
							folders.Add(new LocalFolder(accountId, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1))));
						}
						readOnlyList = folders;
					}
					readOnlyList2 = readOnlyList;
				}
				result = readOnlyList2;
			}
			return result;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<IReadOnlyList<FolderTombstone>> ListTombstonesAsync(string accountId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId, "accountId");
		await _gate.WaitAsync(cancellationToken);
		try
		{
			IReadOnlyList<FolderTombstone> result;
			await using (SqliteConnection connection = await OpenAsync(cancellationToken))
			{
				await EnsureSchemaAsync(connection, cancellationToken);
				IReadOnlyList<FolderTombstone> readOnlyList2;
				await using (SqliteCommand command = connection.CreateCommand())
				{
					command.CommandText = "SELECT Path, DeletedUtc FROM LocalFolderTombstones WHERE AccountId = $account ORDER BY Path COLLATE NOCASE;";
					command.Parameters.AddWithValue("$account", accountId);
					IReadOnlyList<FolderTombstone> readOnlyList;
					await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
					{
						List<FolderTombstone> tombstones = new List<FolderTombstone>();
						while (await reader.ReadAsync(cancellationToken))
						{
							tombstones.Add(new FolderTombstone(accountId, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1))));
						}
						readOnlyList = tombstones;
					}
					readOnlyList2 = readOnlyList;
				}
				result = readOnlyList2;
			}
			return result;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task RenameAsync(string accountId, string path, string newPath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId, "accountId");
		string source = FileManifestMetadata.NormalizeFolderPath(path);
		string destination = FileManifestMetadata.NormalizeFolderPath(newPath);
		if (source.Length == 0 || destination.Length == 0 || destination.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("Folder rename paths are invalid or recursive.");
		}
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await EnsureSchemaAsync(connection, cancellationToken);
			await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
			List<LocalFolder> source2 = await LoadFoldersAsync(connection, (SqliteTransaction)transaction, accountId, cancellationToken);
			LocalFolder[] source3 = source2.Where((LocalFolder folder) => IsPathOrDescendant(folder.Path, source)).ToArray();
			if (!source3.Any((LocalFolder folder) => string.Equals(folder.Path, source, StringComparison.OrdinalIgnoreCase)))
			{
				throw new KeyNotFoundException("The folder does not exist in local storage.");
			}
			(LocalFolder Old, string NewPath)[] renamed = source3.Select((LocalFolder folder) => (Old: folder, NewPath: destination + folder.Path.Substring(source.Length))).ToArray();
			HashSet<string> oldPaths = source3.Select((LocalFolder folder) => folder.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
			if (source2.Any((LocalFolder folder) => IsPathOrDescendant(folder.Path, destination) && !oldPaths.Contains(folder.Path)))
			{
				throw new InvalidOperationException("A folder already exists at the destination.");
			}
			DateTimeOffset deletedAt = DateTimeOffset.UtcNow;
			(LocalFolder Old, string NewPath)[] array = renamed;
			for (int num = 0; num < array.Length; num++)
			{
				(LocalFolder Old, string NewPath) item = array[num];
				await DeleteFolderAsync(connection, (SqliteTransaction)transaction, accountId, item.Old.Path, cancellationToken);
				await UpsertTombstoneAsync(connection, (SqliteTransaction)transaction, new FolderTombstone(accountId, item.Old.Path, deletedAt), cancellationToken);
			}
			foreach (var item in renamed.OrderBy(((LocalFolder Old, string NewPath) tuple) => tuple.NewPath.Length))
			{
				string[] segments = item.NewPath.Split('/');
				for (int num = 0; num < segments.Length; num++)
				{
					string folderPath = string.Join('/', segments.Take(num + 1));
					DateTimeOffset createdAtUtc = (string.Equals(folderPath, item.NewPath, StringComparison.OrdinalIgnoreCase) ? item.Old.CreatedAtUtc : deletedAt);
					await UpsertFolderAsync(connection, (SqliteTransaction)transaction, new LocalFolder(accountId, folderPath, createdAtUtc), cancellationToken);
					await DeleteTombstoneAsync(connection, (SqliteTransaction)transaction, accountId, folderPath, cancellationToken);
				}
			}
			await transaction.CommitAsync(cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task DeleteAsync(string accountId, string path, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId, "accountId");
		string normalized = FileManifestMetadata.NormalizeFolderPath(path);
		if (normalized.Length == 0)
		{
			throw new ArgumentException("The storage root cannot be deleted.", "path");
		}
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await EnsureSchemaAsync(connection, cancellationToken);
			await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
			LocalFolder[] array = (await LoadFoldersAsync(connection, (SqliteTransaction)transaction, accountId, cancellationToken)).Where((LocalFolder localFolder) => IsPathOrDescendant(localFolder.Path, normalized)).ToArray();
			if (array.Length == 0)
			{
				throw new KeyNotFoundException("The folder does not exist in local storage.");
			}
			DateTimeOffset deletedAt = DateTimeOffset.UtcNow;
			LocalFolder[] array2 = array;
			foreach (LocalFolder folder in array2)
			{
				await UpsertTombstoneAsync(connection, (SqliteTransaction)transaction, new FolderTombstone(accountId, folder.Path, deletedAt), cancellationToken);
				await DeleteFolderAsync(connection, (SqliteTransaction)transaction, accountId, folder.Path, cancellationToken);
			}
			await transaction.CommitAsync(cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task MergeRemoteAsync(string accountId, IEnumerable<LocalFolder> folders, IEnumerable<FolderTombstone> tombstones, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId, "accountId");
		ArgumentNullException.ThrowIfNull(folders, "folders");
		ArgumentNullException.ThrowIfNull(tombstones, "tombstones");
		LocalFolder[] incoming = folders.ToArray();
		FolderTombstone[] deleted = tombstones.ToArray();
		if (incoming.Length + deleted.Length > 100000)
		{
			throw new InvalidDataException("Remote folder state exceeds the safe entry limit.");
		}
		LocalFolder[] array = incoming;
		foreach (LocalFolder localFolder in array)
		{
			if (!string.Equals(localFolder.AccountId, accountId, StringComparison.Ordinal))
			{
				throw new InvalidDataException("Remote folder state belongs to a different Telegram account.");
			}
			string text = FileManifestMetadata.NormalizeFolderPath(localFolder.Path);
			if (text.Length == 0 || !string.Equals(text, localFolder.Path, StringComparison.Ordinal))
			{
				throw new InvalidDataException("Remote folder state contains an invalid path.");
			}
		}
		FolderTombstone[] array2 = deleted;
		foreach (FolderTombstone folderTombstone in array2)
		{
			if (!string.Equals(folderTombstone.AccountId, accountId, StringComparison.Ordinal) || FileManifestMetadata.NormalizeFolderPath(folderTombstone.Path) != folderTombstone.Path || folderTombstone.DeletedAtUtc == default(DateTimeOffset))
			{
				throw new InvalidDataException("Remote folder state contains an invalid tombstone.");
			}
		}
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await EnsureSchemaAsync(connection, cancellationToken);
			await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
			FolderTombstone[] array3 = deleted;
			foreach (FolderTombstone tombstone in array3)
			{
				await UpsertTombstoneAsync(connection, (SqliteTransaction)transaction, tombstone, cancellationToken);
				await DeleteFolderSubtreeAsync(connection, (SqliteTransaction)transaction, accountId, tombstone.Path, cancellationToken);
			}
			foreach (LocalFolder folder in incoming.OrderBy((LocalFolder item) => item.Path.Length))
			{
				string[] segments = folder.Path.Split('/');
				for (int j = 0; j < segments.Length; j++)
				{
					string folderPath = string.Join('/', segments.Take(j + 1));
					await UpsertFolderAsync(connection, (SqliteTransaction)transaction, new LocalFolder(accountId, folderPath, folder.CreatedAtUtc), cancellationToken);
					await DeleteTombstoneAsync(connection, (SqliteTransaction)transaction, accountId, folderPath, cancellationToken);
				}
			}
			await transaction.CommitAsync(cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) => SqliteDatabase.OpenAsync(databasePath, databaseKey, cancellationToken);

	private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		if (_schemaReady)
		{
			return;
		}
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "CREATE TABLE IF NOT EXISTS LocalFolders (AccountId TEXT NOT NULL, Path TEXT NOT NULL, CreatedUtc TEXT NOT NULL, PRIMARY KEY (AccountId, Path)); CREATE TABLE IF NOT EXISTS LocalFolderTombstones (AccountId TEXT NOT NULL, Path TEXT NOT NULL, DeletedUtc TEXT NOT NULL, PRIMARY KEY (AccountId, Path));";
		await command.ExecuteNonQueryAsync(cancellationToken);
		_schemaReady = true;
	}

	private static bool IsPathOrDescendant(string candidate, string path)
	{
		if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
		{
			return candidate.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static async Task<List<LocalFolder>> LoadFoldersAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, CancellationToken cancellationToken)
	{
		List<LocalFolder> result;
		await using (SqliteCommand command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "SELECT Path, CreatedUtc FROM LocalFolders WHERE AccountId = $account;";
			command.Parameters.AddWithValue("$account", accountId);
			List<LocalFolder> list;
			await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
			{
				List<LocalFolder> folders = new List<LocalFolder>();
				while (await reader.ReadAsync(cancellationToken))
				{
					folders.Add(new LocalFolder(accountId, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1))));
				}
				list = folders;
			}
			result = list;
		}
		return result;
	}

	private static async Task UpsertFolderAsync(SqliteConnection connection, SqliteTransaction transaction, LocalFolder folder, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "INSERT INTO LocalFolders (AccountId, Path, CreatedUtc) VALUES ($account, $path, $created) ON CONFLICT(AccountId, Path) DO NOTHING;";
		command.Parameters.AddWithValue("$account", folder.AccountId);
		command.Parameters.AddWithValue("$path", folder.Path);
		command.Parameters.AddWithValue("$created", folder.CreatedAtUtc.ToUniversalTime().ToString("O"));
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task DeleteFolderAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, string path, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM LocalFolders WHERE AccountId = $account AND Path = $path;";
		command.Parameters.AddWithValue("$account", accountId);
		command.Parameters.AddWithValue("$path", path);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task DeleteFolderSubtreeAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, string path, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM LocalFolders WHERE AccountId = $account AND (Path = $path OR substr(Path, 1, length($path) + 1) = $path || '/');";
		command.Parameters.AddWithValue("$account", accountId);
		command.Parameters.AddWithValue("$path", path);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task UpsertTombstoneAsync(SqliteConnection connection, SqliteTransaction transaction, FolderTombstone tombstone, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "INSERT INTO LocalFolderTombstones (AccountId, Path, DeletedUtc) VALUES ($account, $path, $deleted) ON CONFLICT(AccountId, Path) DO UPDATE SET DeletedUtc = excluded.DeletedUtc;";
		command.Parameters.AddWithValue("$account", tombstone.AccountId);
		command.Parameters.AddWithValue("$path", tombstone.Path);
		command.Parameters.AddWithValue("$deleted", tombstone.DeletedAtUtc.ToUniversalTime().ToString("O"));
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task DeleteTombstoneAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, string path, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM LocalFolderTombstones WHERE AccountId = $account AND Path = $path;";
		command.Parameters.AddWithValue("$account", accountId);
		command.Parameters.AddWithValue("$path", path);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}
}

