using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace TeleSelfCloud.Infrastructure.Transfers;

public static class SqliteDatabase
{
    public static async Task<SqliteConnection> OpenAsync(string databasePath, string? key, CancellationToken token)
    {
        if (key is not null)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(key); }
            catch (FormatException) { throw new InvalidDataException("The local database key has invalid format."); }
            try { if (bytes.Length != 32 || Convert.ToBase64String(bytes) != key) throw new InvalidDataException("The local database key has invalid format."); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = key is null ? SqliteCacheMode.Shared : SqliteCacheMode.Private,
            Pooling = key is null, DefaultTimeout = 5
        };
        if (key is not null) options.Password = key;
        var connection = new SqliteConnection(options.ToString());
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
