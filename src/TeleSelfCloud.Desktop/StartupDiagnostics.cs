using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public enum StartupStage { ProfileLease, Preferences, LegacyDiagnostics, CatalogMigration, QueueRecovery, Window, DatabaseProtection }
public sealed record StartupDiagnostic(int Version, string ReportId, DateTimeOffset AtUtc, string Stage, string FailureCode);

public static class StartupDiagnostics
{
    private const int MaxLegacyBytes = 4 * 1024 * 1024;
    private const string MigratedMarker = "TSC-LEGACY-DIAGNOSTIC-PROTECTED|1|";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TeleSelfCloud legacy startup diagnostics v1");
    public static StartupDiagnostic Create(Exception error, StartupStage stage) => new(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
        Enum.IsDefined(stage) ? stage.ToString() : "Unknown", SafeFailure.Code(error));
    public static void Write(string profileRoot, StartupDiagnostic report)
    {
        if (report.Version != 1 || !Guid.TryParseExact(report.ReportId, "N", out _) ||
            report.Stage != "Unknown" && (!Enum.TryParse<StartupStage>(report.Stage, out var parsedStage) || !Enum.IsDefined(parsedStage) || parsedStage.ToString() != report.Stage) ||
            report.FailureCode is not ("TELEGRAM_FAILURE" or "KEY_OR_AUTHENTICATION_FAILURE" or "DATA_VALIDATION_FAILURE" or "ACCESS_DENIED" or "LOCAL_STORAGE_FAILURE" or "CANCELED" or "UNEXPECTED_FAILURE"))
            throw new InvalidDataException("Invalid diagnostic record.");
        AtomicWrite(Path.Combine(profileRoot, "startup-diagnostic.json"), JsonSerializer.SerializeToUtf8Bytes(report), true);
    }
    // Caller owns LocalProfileLease. Keep old diagnostics recoverable without keeping a plaintext copy.
    public static void ProtectLegacyLog(string profileRoot)
    {
        var source = Path.Combine(profileRoot, "startup-error.log");
        if (!File.Exists(source)) return;
        if (LocalFileSystemPathGuard.ContainsReparsePoint(source))
            throw new InvalidDataException("The legacy diagnostic is a filesystem link. It was kept.");
        using (var input = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (input.Length > MaxLegacyBytes) throw new InvalidDataException("The legacy diagnostic exceeds the safe migration limit. It was kept.");
            var plaintext = new byte[checked((int)input.Length)];
            try
            {
                input.ReadExactly(plaintext);
                if (plaintext.Length == MigratedMarker.Length + 32)
                {
                    var marker = Encoding.UTF8.GetString(plaintext);
                    if (marker.StartsWith(MigratedMarker, StringComparison.Ordinal) && Guid.TryParseExact(marker[MigratedMarker.Length..], "N", out _)) return;
                }
                var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
                var archiveId = Guid.NewGuid().ToString("N");
                var destination = Path.Combine(profileRoot, "diagnostics", $"legacy-{archiveId}.dpapi");
                AtomicWrite(destination, protectedBytes, false);
                var verified = ProtectedData.Unprotect(File.ReadAllBytes(destination), Entropy, DataProtectionScope.CurrentUser);
                try { if (!CryptographicOperations.FixedTimeEquals(plaintext, verified)) throw new CryptographicException("The diagnostic archive failed verification."); }
                finally { CryptographicOperations.ZeroMemory(verified); }
                input.Position = 0; input.SetLength(0);
                input.Write(Encoding.UTF8.GetBytes(MigratedMarker + archiveId)); input.Flush(true);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }
    public static byte[] RecoverLegacyArchive(string path)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path)) throw new InvalidDataException("The diagnostic archive path is a filesystem link.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxLegacyBytes + 65536) throw new InvalidDataException("Invalid diagnostic archive.");
        var protectedBytes = new byte[checked((int)input.Length)];
        input.ReadExactly(protectedBytes);
        return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
    }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var fullPath = Path.GetFullPath(path);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(fullPath))
            throw new InvalidDataException("The diagnostic destination is a filesystem link. It was kept.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(fullPath))
            throw new InvalidDataException("The diagnostic destination is a filesystem link. It was kept.");
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (LocalFileSystemPathGuard.ContainsReparsePoint(fullPath))
                throw new InvalidDataException("The diagnostic destination is a filesystem link. It was kept.");
            File.Move(temporary, fullPath, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
