using System.Security.Cryptography;
using System.Text.Json;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Internal fixed-record migration; adapters define paths, limits, initialization and validation.</summary>
internal sealed class LocalRecordMigration(string stateName, string journalName, string workName, int maxPlaintextBytes, Action<byte[]> validate, byte[]? initialState)
{
    private sealed record Plan(int Version, string OperationId, string Stage, string? SourceHash, string PlainHash, string? CandidateHash);
    private string State => stateName; private string Journal => journalName;
    public void RequireReady(string root, LocalRecordCipher journalCipher)
    {
        var path = Path.Combine(root, Journal); RejectLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 16384) throw new InvalidDataException("The local record protection latch exceeds its limit.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); var plain = journalCipher.Unprotect(bytes);
        try
        {
            var plan = JsonSerializer.Deserialize<Plan>(plain);
            if (plan is null || plan.Version != 1 || plan.Stage != "Ready" || !Guid.TryParseExact(plan.OperationId, "N", out _) || !HashValid(plan.PlainHash) || !HashValid(plan.CandidateHash) ||
                plan.SourceHash is not null && !HashValid(plan.SourceHash) || !File.Exists(Path.Combine(root, State)))
                throw new InvalidDataException("Finish local record protection migration or restore its verified backup before opening this workspace.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public async Task MigrateAsync(string root, LocalProfileLease lease, LocalRecordCipher stateCipher, LocalRecordCipher journalCipher,
        CancellationToken token, Action<string>? checkpoint = null, bool profileLeaseIsExclusive = false)
    {
        lease.RequireWithin(root); RejectLink(root);
        var source = Path.Combine(root, State); var journal = Path.Combine(root, Journal);
        var lockPath = journal + ".lock"; RejectLink(lockPath);
        using var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var writerPath = source + ".lock"; RejectLink(writerPath);
        // Some record adapters already hold a stronger per-operation lease whose path
        // is source + ".lock". The process-wide LocalProfileLease excludes every other
        // app instance; in that mode the migration journal lock still serializes migrations.
        using var writer = profileLeaseIsExclusive ? null : new FileStream(writerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Plan? plan = null;
        if (File.Exists(journal))
        {
            var bytes = journalCipher.Unprotect(await ReadAsync(journal, token, 16384));
            try { plan = JsonSerializer.Deserialize<Plan>(bytes) ?? throw new InvalidDataException("The local record migration plan is empty."); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if (plan.Version != 1 || !Guid.TryParseExact(plan.OperationId, "N", out _) || plan.Stage is not ("Prepared" or "Verified" or "Switching" or "Switched" or "Ready") ||
                !HashValid(plan.PlainHash) || plan.SourceHash is not null && !HashValid(plan.SourceHash) || plan.Stage != "Prepared" && !HashValid(plan.CandidateHash))
                throw new InvalidDataException("The local record migration plan is invalid. Its files were kept.");
        }
        if (plan?.Stage == "Ready")
        {
            // Normal cache updates change bytes after Ready: authenticate current state,
            // do not compare it to the original migration snapshot or recreate a lost file.
            var current = stateCipher.Unprotect(await ReadAsync(source, token));
            try { Validate(current); } finally { CryptographicOperations.ZeroMemory(current); }
            return;
        }
        if (plan is null || plan.Stage == "Prepared")
        {
            token.ThrowIfCancellationRequested();
            byte[] plain; string? sourceHash;
            if (File.Exists(source)) { plain = await ReadAsync(source, token); sourceHash = Hash(plain); }
            else { plain = initialState?.ToArray() ?? throw new InvalidDataException("The required local record is missing. Restore it before migration."); sourceHash = null; }
            try
            {
                Validate(plain); var plainHash = Hash(plain);
                if (plan is not null && (plan.SourceHash != sourceHash || plan.PlainHash != plainHash)) throw new InvalidDataException("The local record source changed. All copies were kept.");
                plan ??= new(1, Guid.NewGuid().ToString("N"), "Prepared", sourceHash, plainHash, null);
                SavePlan(journal, plan, journalCipher); checkpoint?.Invoke("Prepared");
                var directory = Work(root, plan); EnsureWork(root, directory);
                await EnsureCopyAsync(Path.Combine(directory, "candidate.tsc"), plain, stateCipher, token);
                if (sourceHash is not null) await EnsureCopyAsync(Path.Combine(directory, "original.tsc"), plain, stateCipher, token);
                if (await FileHashOrNullAsync(source, token) != sourceHash) throw new InvalidDataException("The local record source changed during snapshot. All copies were kept.");
                plan = plan with { Stage = "Verified", CandidateHash = await FileHashOrNullAsync(Path.Combine(directory, "candidate.tsc"), token) };
                SavePlan(journal, plan, journalCipher); checkpoint?.Invoke("Verified");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        var work = Work(root, plan!); EnsureWork(root, work);
        var candidate = Path.Combine(work, "candidate.tsc"); var raw = Path.Combine(work, "old-raw");
        if (plan!.Stage == "Verified")
        {
            token.ThrowIfCancellationRequested();
            if (await FileHashOrNullAsync(source, token) != plan.SourceHash || await FileHashOrNullAsync(candidate, token) != plan.CandidateHash)
                throw new InvalidDataException("The verified local record migration changed. All copies were kept.");
            plan = plan with { Stage = "Switching" }; SavePlan(journal, plan, journalCipher); checkpoint?.Invoke("Switching");
        }
        if (plan.Stage == "Switching")
        {
            if (await FileHashOrNullAsync(source, default) != plan.CandidateHash)
            {
                if (await FileHashOrNullAsync(source, default) != plan.SourceHash || await FileHashOrNullAsync(candidate, default) != plan.CandidateHash || File.Exists(raw))
                    throw new InvalidDataException("The local record switch is ambiguous. All copies were kept.");
                RejectLink(source); RejectLink(raw);
                if (plan.SourceHash is null) File.Move(candidate, source, false); else File.Replace(candidate, source, raw, false);
            }
            if (plan.SourceHash is not null && await FileHashOrNullAsync(raw, default) != plan.SourceHash) throw new InvalidDataException("The original local record recovery copy is missing or changed.");
            checkpoint?.Invoke("AfterReplace"); plan = plan with { Stage = "Switched" }; SavePlan(journal, plan, journalCipher); checkpoint?.Invoke("Switched");
        }
        if (plan.Stage == "Switched")
        {
            if (await FileHashOrNullAsync(source, default) != plan.CandidateHash) throw new InvalidDataException("The switched local record changed before completion.");
            await VerifyCopyAsync(source, plan.PlainHash, stateCipher, default);
            if (plan.SourceHash is not null)
            {
                await VerifyCopyAsync(Path.Combine(work, "original.tsc"), plan.SourceHash, stateCipher, default);
                if (File.Exists(raw)) { if (await FileHashOrNullAsync(raw, default) != plan.SourceHash) throw new InvalidDataException("The original local record changed. It was kept."); RejectLink(raw); File.Delete(raw); }
            }
            checkpoint?.Invoke("OriginalProtected"); plan = plan with { Stage = "Ready" }; SavePlan(journal, plan, journalCipher); checkpoint?.Invoke("Ready");
        }
    }
    private void Validate(byte[] plain) { if (plain.Length > maxPlaintextBytes) throw new InvalidDataException("The local record exceeds its limit."); validate(plain); }
    private static bool HashValid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private string Work(string root, Plan plan) => Path.Combine(root, workName, plan.OperationId);
    private void EnsureWork(string root, string work) { RejectLink(Path.Combine(root, workName)); RejectLink(work); Directory.CreateDirectory(work); }
    private async Task<byte[]> ReadAsync(string path, CancellationToken token, int? limit = null)
    {
        RejectLink(path); await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (input.Length > (limit ?? maxPlaintextBytes + 76)) throw new InvalidDataException("The local record migration record exceeds its limit.");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token); return bytes;
    }
    private async Task<string?> FileHashOrNullAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null; var bytes = await ReadAsync(path, token);
        try { return Hash(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void SavePlan(string path, Plan plan, LocalRecordCipher cipher)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(plan);
        try { AtomicWrite(path, cipher.Protect(plain), true); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private async Task EnsureCopyAsync(string path, byte[] plain, LocalRecordCipher cipher, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (!File.Exists(path)) AtomicWrite(path, cipher.Protect(plain), false);
        await VerifyCopyAsync(path, Hash(plain), cipher, token);
    }
    private async Task VerifyCopyAsync(string path, string expected, LocalRecordCipher cipher, CancellationToken token)
    {
        var decoded = cipher.Unprotect(await ReadAsync(path, token));
        try { if (Hash(decoded) != expected) throw new InvalidDataException("The protected local record copy differs from its source."); Validate(decoded); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }
    private static void RejectLink(string path) { if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The local record migration path is a link. Its files were kept."); }
    private static void AtomicWrite(string path, byte[] bytes, bool replace)
    {
        RejectLink(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); } File.Move(temporary, path, replace); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
