using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class DiagnosticPrivacyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.Diagnostics", Guid.NewGuid().ToString("N"));
    private const string Secret = "api_hash=secret-token passphrase=never-log C:/Private/Finance.bin";
    public DiagnosticPrivacyTests() => Directory.CreateDirectory(root);
    [Fact]
    public void StartupReportNeverReadsExceptionTextDataOrStack()
    {
        var error = new HostileException(); error.Data[Secret] = Secret;
        var report = StartupDiagnostics.Create(error, StartupStage.CatalogMigration);
        StartupDiagnostics.Write(root, report);
        var bytes = File.ReadAllText(Path.Combine(root, "startup-diagnostic.json"));
        Assert.DoesNotContain(Secret, bytes); Assert.DoesNotContain("HostileException", bytes);
        Assert.Equal("UNEXPECTED_FAILURE", report.FailureCode);
        Assert.Equal(report, JsonSerializer.Deserialize<StartupDiagnostic>(bytes));
        Assert.Throws<InvalidDataException>(() => StartupDiagnostics.Write(root, report with { Stage = "999" }));
        Assert.Throws<InvalidDataException>(() => StartupDiagnostics.Write(root, report with { FailureCode = Secret }));
    }
    [Fact]
    public void LegacyLogIsArchivedVerifiedAndReplacedWhileLockedWithoutLosingBytes()
    {
        var path = Path.Combine(root, "startup-error.log"); File.WriteAllText(path, Secret);
        var original = File.ReadAllBytes(path);
        StartupDiagnostics.ProtectLegacyLog(root);
        var archive = Assert.Single(Directory.GetFiles(Path.Combine(root, "diagnostics"), "*.dpapi"));
        Assert.Equal(original, StartupDiagnostics.RecoverLegacyArchive(archive));
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(File.ReadAllBytes(archive)));
        var marker = File.ReadAllText(path); Assert.StartsWith("TSC-LEGACY-DIAGNOSTIC-PROTECTED|1|", marker);
        StartupDiagnostics.ProtectLegacyLog(root);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "diagnostics"), "*.dpapi")); Assert.Equal(marker, File.ReadAllText(path));
    }
    [Fact]
    public void ArchiveWriteFailureKeepsOriginalLogForRecovery()
    {
        var path = Path.Combine(root, "startup-error.log"); File.WriteAllText(path, Secret);
        File.WriteAllText(Path.Combine(root, "diagnostics"), "directory blocked");
        Assert.ThrowsAny<IOException>(() => StartupDiagnostics.ProtectLegacyLog(root));
        Assert.Equal(Secret, File.ReadAllText(path));
    }
    [Fact]
    public void ArchiveParentJunctionKeepsPlaintextSourceAndWritesNothingOutsideProfile()
    {
        var outside = Path.Combine(root, "outside-diagnostics"); Directory.CreateDirectory(outside);
        var alias = Path.Combine(root, "diagnostics"); CreateJunction(alias, outside);
        var source = Path.Combine(root, "startup-error.log"); File.WriteAllText(source, Secret);

        Assert.Throws<InvalidDataException>(() => StartupDiagnostics.ProtectLegacyLog(root));

        Assert.Equal(Secret, File.ReadAllText(source)); Assert.Empty(Directory.GetFiles(outside));
        Directory.Delete(alias, recursive: false);
    }
    [Fact]
    public void ArchiveRecoveryRejectsJunctionAndDoesNotReadAnExternalArchive()
    {
        var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "startup-error.log"), Secret);
        StartupDiagnostics.ProtectLegacyLog(sourceRoot);
        var outside = Path.Combine(sourceRoot, "diagnostics");
        var aliasRoot = Path.Combine(root, "archive-alias"); CreateJunction(aliasRoot, outside);

        Assert.Throws<InvalidDataException>(() => StartupDiagnostics.RecoverLegacyArchive(Path.Combine(aliasRoot, Path.GetFileName(Directory.GetFiles(outside).Single()))));

        Directory.Delete(aliasRoot, recursive: false);
        Assert.Equal(Secret, Encoding.UTF8.GetString(StartupDiagnostics.RecoverLegacyArchive(Directory.GetFiles(outside).Single())));
    }
    private static void CreateJunction(string link, string target)
    {
        using var create = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", link, target }
        }) ?? throw new InvalidOperationException("Could not start diagnostic path fixture creation.");
        create.WaitForExit(); Assert.Equal(0, create.ExitCode);
        Assert.True(LocalFileSystemPathGuard.ContainsReparsePoint(link));
    }
    [Fact]
    public void OversizedOrLockedLegacyLogIsKeptWithoutTruncation()
    {
        var path = Path.Combine(root, "startup-error.log");
        using (var oversized = File.Create(path)) oversized.SetLength(4 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => StartupDiagnostics.ProtectLegacyLog(root));
        Assert.Equal(4 * 1024 * 1024 + 1, new FileInfo(path).Length);
        File.WriteAllText(path, Secret);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.ThrowsAny<IOException>(() => StartupDiagnostics.ProtectLegacyLog(root));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueFailureIsSanitizedAtStorageBoundaryAndKeepsProgress(bool direct)
    {
        var db = Path.Combine(root, "queue.db"); var store = new SqliteTransferQueueStore(db);
        await store.EnqueueAsync("file", "safe.bin", 100, default);
        if (direct)
        {
            await store.SetStateAsync("file", TransferQueueState.Running, null, default);
            await store.UpdateProgressAsync("file", 40, 100, default);
            await store.SetStateAsync("file", TransferQueueState.Failed, Secret, default);
        }
        else await Assert.ThrowsAsync<IOException>(() => new TransferQueueStartGuard(store).RunAsync("file", () => Task.CompletedTask,
            async () => { await store.UpdateProgressAsync("file", 40, 100, default); throw new IOException(Secret); }));
        var item = Assert.Single(await store.ListAsync(default));
        Assert.Equal(direct ? SafeFailure.Unknown : SafeFailure.Storage, item.LastError);
        Assert.Equal(40, item.TransferredBytes); Assert.Equal(TransferQueueState.Failed, item.State);
        using var connection = new SqliteConnection($"Data Source={db}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT LastError FROM TransferQueue";
        Assert.DoesNotContain(Secret, (string)command.ExecuteScalar()!);
    }
    [Fact]
    public void ProviderTextAndCustomExceptionsCannotLeakThroughClassification()
    {
        Assert.Equal(SafeFailure.Unknown, SafeFailure.Describe(new HostileException()));
        Assert.Equal(SafeFailure.Telegram, SafeFailure.Describe(TelegramRequestException.From(400, Secret)));
        Assert.Equal("FILE_PART_INVALID", SafeFailure.Describe(TelegramRequestException.From(400, "FILE_PART_INVALID")));
        Assert.Equal(SafeFailure.Crypto, SafeFailure.Describe(new CryptographicException(Secret)));
        Assert.Equal(SafeFailure.Unknown, SafeFailure.Normalize("FILE_PART_INVALID " + Secret));
        Assert.Equal(SafeFailure.Unknown, SafeFailure.Normalize(new string('x', 10000)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyQueueErrorsAreMinimizedAtomicallyWithoutChangingResumeData(bool rejectWrite)
    {
        var db = Path.Combine(root, "legacy.db"); var first = new SqliteTransferQueueStore(db);
        await first.EnqueueAsync("file", "safe.bin", 100, default);
        await first.SetStateAsync("file", TransferQueueState.Running, null, default);
        await first.UpdateProgressAsync("file", 40, 100, default);
        await first.SetStateAsync("file", TransferQueueState.Failed, SafeFailure.Storage, default);
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open(); using var update = connection.CreateCommand();
            update.CommandText = "UPDATE TransferQueue SET LastError=$secret;"; update.Parameters.AddWithValue("$secret", Secret); update.ExecuteNonQuery();
            if (rejectWrite)
            {
                update.CommandText = "CREATE TRIGGER RejectErrorMigration BEFORE UPDATE OF LastError ON TransferQueue BEGIN SELECT RAISE(ABORT, 'migration rejected'); END;";
                update.ExecuteNonQuery();
            }
        }
        var reopened = new SqliteTransferQueueStore(db);
        if (rejectWrite) await Assert.ThrowsAsync<SqliteException>(() => reopened.ListAsync(default));
        else
        {
            var item = Assert.Single(await reopened.ListAsync(default));
            Assert.Equal(SafeFailure.Unknown, item.LastError); Assert.Equal(40, item.TransferredBytes);
            Assert.Equal(TransferQueueState.Failed, item.State); Assert.Equal(1, item.AttemptCount);
        }
        using var verify = new SqliteConnection($"Data Source={db}"); verify.Open(); using var select = verify.CreateCommand();
        select.CommandText = "SELECT LastError FROM TransferQueue";
        Assert.Equal(rejectWrite ? Secret : SafeFailure.Unknown, (string)select.ExecuteScalar()!);
    }
    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void StartupAndQueueRecoveryInstructionsAreLocalized(string language)
    {
        var previous = UiText.Instance.Language;
        try
        {
            UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
            var prompt = string.Format(UiText.Instance.Get("startup.failed"), "KEY_OR_AUTHENTICATION_FAILURE", "0123456789abcdef0123456789abcdef");
            Assert.DoesNotContain(Secret, prompt); Assert.DoesNotContain("startup.failed", prompt);
            Assert.Contains(language == "vi" ? "phục hồi" : "restore", prompt);
            Assert.Contains(language == "vi" ? "Kiểm tra" : "Check", UiText.Instance.LocalizeMessage(SafeFailure.Storage));
        }
        finally { UiText.Instance.SetLanguage(previous, Path.Combine(root, "language.json")); }
    }
    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("Sensitive Message getter must not be called.");
        public override string ToString() => throw new InvalidOperationException("Sensitive ToString must not be called.");
        public override string? StackTrace => Secret;
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
