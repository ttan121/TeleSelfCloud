using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Separate process/provider: never opens an installed profile or changes the application's provider.
if (args.Length != 1) throw new ArgumentException("Provide an empty output directory for the isolated cipher probe.");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Probe output is not empty.");
Directory.CreateDirectory(root);
const string secret = "Synthetic-private-finance-79035";
var keyBytes = RandomNumberGenerator.GetBytes(32);
var key = Convert.ToBase64String(keyBytes); CryptographicOperations.ZeroMemory(keyBytes);
var path = Path.Combine(root, "encrypted.db");
var checks = new Dictionary<string, bool>();
string? sqlite = null, cipherVersion = null, cipherName = null;
using (var connection = Open(path, key))
{
    sqlite = Scalar(connection, "SELECT sqlite_version()");
    cipherVersion = Scalar(connection, "SELECT sqlite3mc_version()");
    cipherName = Scalar(connection, "PRAGMA cipher");
    Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE Records(Id INTEGER PRIMARY KEY, Name TEXT, Payload BLOB); CREATE VIRTUAL TABLE Search USING fts5(Name);");
    using var insert = connection.CreateCommand();
    insert.CommandText = "INSERT INTO Records VALUES(1,$name,$data); INSERT INTO Search VALUES($name);";
    insert.Parameters.AddWithValue("$name", secret); insert.Parameters.AddWithValue("$data", RandomNumberGenerator.GetBytes(64 * 1024)); insert.ExecuteNonQuery();
    checks["EncryptedDatabaseHasNoPlainName"] = !Contains(ReadShared(path), secret);
    checks["EncryptedWalHasNoPlainName"] = !Contains(ReadShared(path + "-wal"), secret);
    checks["Fts5Works"] = Scalar(connection, "SELECT COUNT(*) FROM Search WHERE Search MATCH 'Synthetic'") == "1";
    checks["IntegrityCheck"] = Scalar(connection, "PRAGMA integrity_check") == "ok";
    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
}
checks["NoPlainSQLiteHeader"] = !File.ReadAllBytes(path).AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8);
using (var reopen = Open(path, key)) checks["CorrectKeyReopen"] = Scalar(reopen, "SELECT Name FROM Records") == secret;
checks["MissingKeyRejected"] = Rejects(path, null);
checks["WrongKeyRejected"] = Rejects(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
var corrupted = Path.Combine(root, "tampered.db"); var original = File.ReadAllBytes(path);
var changed = original.ToArray(); changed[512] ^= 1; File.WriteAllBytes(corrupted, changed);
checks["PageTamperingRejected"] = Rejects(corrupted, key);
var truncated = Path.Combine(root, "truncated.db"); File.WriteAllBytes(truncated, original[..^33]);
checks["TruncationRejected"] = Rejects(truncated, key);
var legacy = Path.Combine(root, "legacy.db");
using (var plain = Open(legacy, null)) Execute(plain, "CREATE TABLE Legacy(Id INTEGER); INSERT INTO Legacy VALUES(42);");
using (var plain = Open(legacy, null)) checks["PlainDatabaseCompatibility"] = Scalar(plain, "SELECT Id FROM Legacy") == "42";
var migrated = Path.Combine(root, "migrated.db");
using (var plain = Open(legacy, null))
using (var copy = Open(migrated, null))
{
    plain.BackupDatabase(copy);
    var rekeyBytes = Encoding.UTF8.GetBytes(key);
    try
    {
        var rc = SQLitePCL.raw.sqlite3_rekey(copy.Handle, rekeyBytes);
        if (rc != SQLitePCL.raw.SQLITE_OK) throw new InvalidOperationException($"Native copy rekey failed with code {rc}.");
    }
    finally { CryptographicOperations.ZeroMemory(rekeyBytes); }
}
using (var encrypted = Open(migrated, key)) checks["PlainCopyRekeyPreservesRows"] = Scalar(encrypted, "SELECT Id FROM Legacy") == "42" && Scalar(encrypted, "PRAGMA integrity_check") == "ok";
checks["PlainCopyRekeyPreservesRows"] &= !File.ReadAllBytes(migrated).AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) && Rejects(migrated, null);
var report = new { Version = 1, Sqlite = sqlite, CipherLibrary = cipherVersion, Cipher = cipherName, Package = "SQLite3MC.PCLRaw.bundle/2.4.0", Checks = checks, AllPassed = checks.Values.All(v => v), Scope = "Native isolated database only; no app/profile migration or UI acceptance" };
File.WriteAllText(Path.Combine(root, "probe-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
if (!report.AllPassed) throw new InvalidOperationException("Native cipher probe failed. Evidence was kept.");

static SqliteConnection Open(string file, string? password)
{
    var options = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 5 };
    if (password is not null) options.Password = password;
    var connection = new SqliteConnection(options.ToString());
    try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
}
static string? Scalar(SqliteConnection c, string sql) { using var command = c.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()?.ToString(); }
static void Execute(SqliteConnection c, string sql) { using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
static bool Contains(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
static byte[] ReadShared(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
}
static bool Rejects(string path, string? key)
{
    try { using var c = Open(path, key); return Scalar(c, "PRAGMA integrity_check") != "ok"; }
    catch (SqliteException) { return true; }
}
