using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public sealed record LocalDatabaseKeyBackup(int Version, string ProtectionId, string Proof, PassphraseKeyEnvelope RecoveryKey);
public sealed record LocalDatabaseProtectionStatus(bool Configured, string? ProtectionId, string Stage);

/// <summary>Offline one-catalog protection. Caller owns the global profile lease before any stores/session open.</summary>
public static class LocalDatabaseProtection
{
    private const string Db = "manifests.db", PolicyFile = "local-db-policy.json", KeyFile = "local-db-key.dpapi", PlanFile = "local-db-migration.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Policy(int Version, string ProtectionId, string Proof, string Cipher);
    private sealed record Piece(string Name, string Hash);
    private sealed record Plan(int Version, string ProtectionId, string OperationId, string Stage, string LogicalHash, Piece[] Pieces, string? CandidateHash = null);
    private sealed record Envelope(int Version, string Salt, string Nonce, string Data, string Tag);
    private static readonly byte[] Entropy = "TeleSelfCloud local database master key v1"u8.ToArray();
    private static string At(string root, string name) => Path.Combine(root, name);
    public static bool IsConfigured(string root) => File.Exists(At(root, PolicyFile)) || File.Exists(At(root, KeyFile)) || File.Exists(At(root, PlanFile));
    public static bool KeyAvailable(string root)
    {
        var policy = ReadPolicy(root);
        try { var key = LoadKey(root, policy); CryptographicOperations.ZeroMemory(key); return true; }
        catch (CryptographicException) { return false; }
        catch (FileNotFoundException) { return false; }
    }
    public static string? KeyForOpening(string root) => IsConfigured(root) ? DatabaseKey(root) : null;
    public static LocalRecordCipher RecordCipher(string root, string identity, string purpose = "cache-verification")
    {
        var policy = ReadPolicy(root);
        return new(DatabaseKey(root), policy.ProtectionId, purpose, identity);
    }
    public static LocalDatabaseProtectionStatus Status(string root)
    {
        if (!IsConfigured(root)) return new(false, null, "Off");
        var policy = ReadPolicy(root); var key = LoadKey(root, policy);
        try { return new(true, policy.ProtectionId, ReadPlan(root, policy, key)?.Stage ?? "Requested"); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static string DatabaseKey(string root)
    {
        var policy = ReadPolicy(root); var key = LoadKey(root, policy);
        try
        {
            if (ReadPlan(root, policy, key)?.Stage != "Ready" || !File.Exists(At(root, Db)))
                throw new InvalidDataException("Complete local database migration or restore the profile before opening its catalog.");
            return Convert.ToBase64String(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static void Request(string root, string backupPath, string passphrase, LocalProfileLease lease)
    {
        lease.RequireWithin(root);
        if (IsConfigured(root)) throw new InvalidOperationException("This catalog already has a protection key. Recover its existing key.");
        var destination = Path.GetFullPath(backupPath);
        if (destination.StartsWith(lease.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save the database recovery key outside the local profile.");
        var key = RandomNumberGenerator.GetBytes(32); var id = Guid.NewGuid().ToString("N");
        try
        {
            var proof = Proof(id, key); var backup = new LocalDatabaseKeyBackup(1, id, proof, AesGcmFileCipher.WrapFileKey(key, passphrase));
            WriteAtomic(destination, JsonSerializer.SerializeToUtf8Bytes(backup, Json), false);
            var check = RecoverBackup(ReadBackup(destination), passphrase);
            try { if (!CryptographicOperations.FixedTimeEquals(key, check)) throw new CryptographicException("The database recovery key failed verification."); }
            finally { CryptographicOperations.ZeroMemory(check); }
            WriteAtomic(At(root, PolicyFile), JsonSerializer.SerializeToUtf8Bytes(new Policy(1, id, proof, "chacha20-v1"), Json), false);
            SaveKey(root, key, false);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static void Recover(string root, string backupPath, string passphrase, LocalProfileLease lease)
    {
        lease.RequireWithin(root); var policy = ReadPolicy(root); var backup = ReadBackup(backupPath);
        if (backup.ProtectionId != policy.ProtectionId || backup.Proof != policy.Proof) throw new InvalidDataException("The recovery key does not match this local database profile.");
        var key = RecoverBackup(backup, passphrase);
        try
        {
            if (Proof(policy.ProtectionId, key) != policy.Proof) throw new CryptographicException("The local database key failed authentication.");
            if (File.Exists(At(root, KeyFile))) File.Copy(At(root, KeyFile), At(root, KeyFile + "." + Guid.NewGuid().ToString("N") + ".backup"), false);
            SaveKey(root, key, true);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static void Export(string root, string destination, string passphrase, LocalProfileLease lease)
    {
        lease.RequireWithin(root);
        if (Path.GetFullPath(destination).StartsWith(lease.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Save the database recovery key outside the local profile.");
        var policy = ReadPolicy(root); var key = LoadKey(root, policy);
        try
        {
            WriteAtomic(destination, JsonSerializer.SerializeToUtf8Bytes(new LocalDatabaseKeyBackup(1, policy.ProtectionId, policy.Proof, AesGcmFileCipher.WrapFileKey(key, passphrase)), Json), false);
            var check = RecoverBackup(ReadBackup(destination), passphrase);
            try { if (!CryptographicOperations.FixedTimeEquals(key, check)) throw new CryptographicException("The database recovery key failed verification."); }
            finally { CryptographicOperations.ZeroMemory(check); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static async Task MigrateOfflineAsync(string root, LocalProfileLease lease, CancellationToken token, Action<string>? checkpoint = null, Action<string>? progress = null)
    {
        // Progress observers are informational and must never strand a durable switch.
        void Notify(string stage) { try { progress?.Invoke(stage); } catch (Exception) { } }
        lease.RequireWithin(root); RejectLink(root);
        RejectLink(At(root, "local-db-migration.lock"));
        using var guard = new FileStream(At(root, "local-db-migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var policy = ReadPolicy(root); var key = LoadKey(root, policy);
        try
        {
            var plan = ReadPlan(root, policy, key); if (plan?.Stage == "Ready") { _ = DatabaseKey(root); Notify("Ready"); return; }
            if (plan is not null) Notify(plan.Stage);
            SqliteConnection.ClearAllPools();
            var primary = At(root, Db);
            if (plan is null || plan.Stage == "Prepared")
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(primary)) throw new InvalidDataException("The source catalog is missing. Its protection key was kept.");
                RejectLink(primary);
                using var pinned = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var source = OpenRaw(primary, null, readOnly: true);
                Notify("Inspecting");
                var digest = await SqliteLogicalDigest.ComputeAsync(source, token);
                var pieces = CapturePieces(root, includeShm: false);
                if (plan is not null && (plan.LogicalHash != digest || !SamePieces(plan.Pieces.Where(p => p.Name != "shm").ToArray(), pieces)))
                    throw new InvalidDataException("The source catalog changed after migration was prepared. All copies were kept.");
                plan ??= new(1, policy.ProtectionId, Guid.NewGuid().ToString("N"), "Prepared", digest, pieces);
                SavePlan(root, policy, key, plan); checkpoint?.Invoke("Prepared");
                Notify("Prepared");
                RejectLink(At(root, "local-db-migrations"));
                var work = Work(root, plan); Directory.CreateDirectory(work); RejectLink(work);
                var candidate = At(work, "candidate.db");
                // Only this plan's fixed scratch names; no staging/cache or source is deleted.
                foreach (var name in new[] { "candidate.db", "candidate.db-journal", "candidate.db-wal", "candidate.db-shm" })
                    if (File.Exists(At(work, name))) { RejectLink(At(work, name)); File.Delete(At(work, name)); }
                await using (var copy = OpenRaw(candidate, null, readOnly: false))
                {
                    Notify("Copying"); source.BackupDatabase(copy);
                    token.ThrowIfCancellationRequested();
                    using (var temp = copy.CreateCommand()) { temp.CommandText = "PRAGMA temp_store=MEMORY;"; temp.ExecuteNonQuery(); }
                    Notify("Encrypting"); var password = Encoding.UTF8.GetBytes(Convert.ToBase64String(key));
                    try { if (SQLitePCL.raw.sqlite3_rekey(copy.Handle, password) != SQLitePCL.raw.SQLITE_OK) throw new IOException("The database copy could not be encrypted. The source was kept."); }
                    finally { CryptographicOperations.ZeroMemory(password); }
                }
                Notify("Verifying");
                await using (var encrypted = OpenRaw(candidate, Convert.ToBase64String(key), readOnly: true))
                    if (await SqliteLogicalDigest.ComputeAsync(encrypted, token) != digest) throw new InvalidDataException("The encrypted database copy differs from its source.");
                if (!SamePieces(pieces, CapturePieces(root, includeShm: false))) throw new InvalidDataException("The source catalog changed while its copy was prepared.");
                plan = plan with { Stage = "Verified", CandidateHash = Hash(candidate) };
                SavePlan(root, policy, key, plan); checkpoint?.Invoke("Verified");
                Notify("Verified");
            }
            RejectLink(At(root, "local-db-migrations"));
            var directory = Work(root, plan!); RejectLink(directory); var next = At(directory, "candidate.db");
            if (plan!.Stage == "Verified")
            {
                token.ThrowIfCancellationRequested();
                if (!SamePieces(plan.Pieces, CapturePieces(root, includeShm: false)) || Hash(next) != plan.CandidateHash)
                    throw new InvalidDataException("The prepared migration no longer matches its catalog. All copies were kept.");
                plan = plan with { Stage = "Switching", Pieces = CapturePieces(root, includeShm: true) };
                SavePlan(root, policy, key, plan); checkpoint?.Invoke("Switching");
                Notify("Switching");
            }
            // Cancellation after switching intent cannot strand sidecar moves or cleanup acknowledgment.
            if (plan.Stage == "Switching")
            {
                foreach (var piece in plan.Pieces.Where(p => p.Name != "main"))
                {
                    var old = PiecePath(root, piece.Name); var moved = At(directory, "old-" + piece.Name);
                    if (File.Exists(old))
                    {
                        if (Hash(old) != piece.Hash || File.Exists(moved)) throw new InvalidDataException("The catalog sidecars differ from the migration plan. All copies were kept.");
                        File.Move(old, moved, false);
                    }
                    else if (!File.Exists(moved) || Hash(moved) != piece.Hash) throw new InvalidDataException("A saved catalog sidecar is missing or changed.");
                }
                checkpoint?.Invoke("SidecarsMoved");
                var original = plan.Pieces.Single(p => p.Name == "main"); var oldMain = At(directory, "old-main");
                if (Hash(primary) != plan.CandidateHash)
                {
                    if (Hash(primary) != original.Hash || Hash(next) != plan.CandidateHash || File.Exists(oldMain)) throw new InvalidDataException("The catalog switch is ambiguous. All copies were kept.");
                    File.Replace(next, primary, oldMain, false);
                }
                if (!File.Exists(oldMain) || Hash(oldMain) != original.Hash) throw new InvalidDataException("The original catalog backup could not be verified.");
                checkpoint?.Invoke("AfterReplace");
                plan = plan with { Stage = "Switched" }; SavePlan(root, policy, key, plan); checkpoint?.Invoke("Switched");
                Notify("Switched");
            }
            if (plan.Stage == "Switched")
            {
                if (Hash(primary) != plan.CandidateHash) throw new InvalidDataException("The switched catalog changed before migration completed.");
                await using (var final = OpenRaw(primary, Convert.ToBase64String(key), true))
                    if (await SqliteLogicalDigest.ComputeAsync(final, CancellationToken.None) != plan.LogicalHash) throw new InvalidDataException("The switched catalog failed final verification.");
                Notify("ProtectingOriginals");
                foreach (var piece in plan.Pieces) await ProtectOriginalAsync(directory, plan, piece, key);
                checkpoint?.Invoke("ArchivesProtected");
                plan = plan with { Stage = "Ready" }; SavePlan(root, policy, key, plan); checkpoint?.Invoke("Ready");
                Notify("Ready");
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static Policy ReadPolicy(string root)
    {
        var policy = JsonSerializer.Deserialize<Policy>(ReadBounded(At(root, PolicyFile)), Json) ?? throw new InvalidDataException("The local database key policy is empty.");
        if (policy.Version != 1 || policy.Cipher != "chacha20-v1" || !Guid.TryParseExact(policy.ProtectionId, "N", out _) || string.IsNullOrEmpty(policy.Proof)) throw new InvalidDataException("The local database key policy is invalid. It was kept.");
        return policy;
    }
    private static byte[] LoadKey(string root, Policy policy)
    {
        var key = ProtectedData.Unprotect(ReadBounded(At(root, KeyFile)), Entropy, DataProtectionScope.CurrentUser);
        if (key.Length == 32 && Proof(policy.ProtectionId, key) == policy.Proof) return key;
        CryptographicOperations.ZeroMemory(key); throw new CryptographicException("The local database key failed authentication.");
    }
    private static void SaveKey(string root, byte[] key, bool replace) => WriteAtomic(At(root, KeyFile), ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser), replace);
    private static LocalDatabaseKeyBackup ReadBackup(string path) => JsonSerializer.Deserialize<LocalDatabaseKeyBackup>(ReadBounded(path), Json) ?? throw new InvalidDataException("The database recovery key is empty.");
    private static byte[] RecoverBackup(LocalDatabaseKeyBackup backup, string passphrase)
    {
        if (backup.Version != 1 || !Guid.TryParseExact(backup.ProtectionId, "N", out _) || backup.RecoveryKey is null || string.IsNullOrEmpty(backup.Proof)) throw new InvalidDataException("The database recovery key is invalid.");
        var key = AesGcmFileCipher.UnwrapFileKey(backup.RecoveryKey, passphrase);
        if (key.Length == 32 && Proof(backup.ProtectionId, key) == backup.Proof) return key;
        CryptographicOperations.ZeroMemory(key); throw new CryptographicException("The database recovery key failed verification.");
    }
    private static string Proof(string id, byte[] key) => Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("TeleSelfCloud local database policy v1|chacha20-v1|" + id)));
    private static string Work(string root, Plan plan) => At(At(root, "local-db-migrations"), plan.OperationId);
    private static string PiecePath(string root, string name) => At(root, name switch { "main" => Db, "wal" => Db + "-wal", "shm" => Db + "-shm", _ => throw new InvalidDataException("Unknown catalog migration piece.") });
    // A read-only SQLite connection can create/remove a zero-byte WAL without changing
    // catalog contents. Track every WAL containing bytes; an empty WAL has no frames.
    private static Piece[] CapturePieces(string root, bool includeShm) => (includeShm ? new[] { "main", "wal", "shm" } : new[] { "main", "wal" }).Where(n => File.Exists(PiecePath(root, n)) && (n != "wal" || new FileInfo(PiecePath(root, n)).Length > 0)).Select(n => new Piece(n, Hash(PiecePath(root, n)))).ToArray();
    private static bool SamePieces(Piece[] left, Piece[] right) => left.Length == right.Length && left.All(p => right.Contains(p));
    private static string Hash(string path) { RejectLink(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static SqliteConnection OpenRaw(string path, string? password, bool readOnly)
    {
        var options = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Cache = SqliteCacheMode.Private, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 };
        if (password is not null) options.Password = password;
        var connection = new SqliteConnection(options.ToString());
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static Plan? ReadPlan(string root, Policy policy, byte[] key)
    {
        var path = At(root, PlanFile); if (!File.Exists(path)) return null;
        var plain = Unseal(ReadBounded(path, 4 * 1024 * 1024), policy, key);
        try
        {
            var plan = JsonSerializer.Deserialize<Plan>(plain, Json) ?? throw new InvalidDataException("The catalog migration plan is empty.");
            if (plan.Version != 1 || plan.ProtectionId != policy.ProtectionId || !Guid.TryParseExact(plan.OperationId, "N", out _) || plan.Stage is not ("Prepared" or "Verified" or "Switching" or "Switched" or "Ready") || plan.Pieces is null || plan.Pieces.Length is < 1 or > 3 || plan.Pieces.Any(p => p is null || p.Name is not ("main" or "wal" or "shm") || !IsHash(p.Hash)) || plan.Pieces.Count(p => p.Name == "main") != 1 || plan.Pieces.Select(p => p.Name).Distinct().Count() != plan.Pieces.Length || !IsHash(plan.LogicalHash) || plan.Stage != "Prepared" && !IsHash(plan.CandidateHash)) throw new InvalidDataException("The catalog migration plan is invalid. It was kept.");
            return plan;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static void SavePlan(string root, Policy policy, byte[] key, Plan plan) => WriteAtomic(At(root, PlanFile), Seal(JsonSerializer.SerializeToUtf8Bytes(plan, Json), policy, key), true);
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static byte[] Seal(byte[] plain, Policy policy, byte[] key)
    {
        var salt = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(12); var data = new byte[plain.Length]; var tag = new byte[16];
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, "TeleSelfCloud local database journal v1"u8.ToArray());
        try { using var aes = new AesGcm(derived, 16); aes.Encrypt(nonce, plain, data, tag, Encoding.UTF8.GetBytes(policy.ProtectionId).Concat(salt).ToArray()); return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(data), Convert.ToBase64String(tag)), Json); }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] Unseal(byte[] bytes, Policy policy, byte[] key)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json) ?? throw new InvalidDataException("The catalog migration envelope is empty.");
        if (envelope.Salt is null || envelope.Nonce is null || envelope.Data is null || envelope.Tag is null) throw new InvalidDataException("The catalog migration envelope is invalid.");
        var salt = Convert.FromBase64String(envelope.Salt); var nonce = Convert.FromBase64String(envelope.Nonce); var data = Convert.FromBase64String(envelope.Data); var tag = Convert.FromBase64String(envelope.Tag);
        if (envelope.Version != 1 || salt.Length != 32 || nonce.Length != 12 || tag.Length != 16) throw new InvalidDataException("The catalog migration envelope is invalid.");
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, "TeleSelfCloud local database journal v1"u8.ToArray()); var plain = new byte[data.Length];
        try { using var aes = new AesGcm(derived, 16); aes.Decrypt(nonce, data, tag, plain, Encoding.UTF8.GetBytes(policy.ProtectionId).Concat(salt).ToArray()); return plain; }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }
    private static async Task ProtectOriginalAsync(string directory, Plan plan, Piece piece, byte[] key)
    {
        var raw = At(directory, "old-" + piece.Name); var archive = raw + ".tscenc";
        RejectLink(raw); RejectLink(archive);
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, Convert.FromHexString(plan.OperationId), Encoding.UTF8.GetBytes("TeleSelfCloud local database original v1|" + piece.Name));
        try
        {
            if (File.Exists(raw) && Hash(raw) != piece.Hash) throw new InvalidDataException("The original database copy changed. It was kept.");
            if (!File.Exists(archive))
            {
                if (!File.Exists(raw)) throw new InvalidDataException("The original database copy is missing.");
                var temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var input = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.None))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    { await AesGcmFileCipher.EncryptAsync(input, output, derived); output.Flush(true); }
                    File.Move(temporary, archive, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            await using var encrypted = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sink = new DigestSinkStream(); await AesGcmFileCipher.DecryptAsync(encrypted, sink, derived);
            if (sink.Finish() != piece.Hash) throw new InvalidDataException("The protected original database failed verification.");
            if (File.Exists(raw)) File.Delete(raw);
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }
    private static byte[] ReadBounded(string path, int limit = 65536)
    {
        RejectLink(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new InvalidDataException("The local database protection record exceeds its limit.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static void RejectLink(string path) { if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The local database protection path is a filesystem link. It was kept."); }
    private static void WriteAtomic(string path, byte[] bytes, bool replace)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); RejectLink(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); } File.Move(temporary, path, replace); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
