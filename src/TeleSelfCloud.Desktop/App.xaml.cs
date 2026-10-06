using System.IO;
using System.Windows;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class App : Application
{
    private LocalProfileLease? _profileLease;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var stage = StartupStage.ProfileLease;
        try
        {
            var localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TeleSelfCloud", "P0");
            _profileLease = LocalProfileLease.TryAcquire(localRoot);
            stage = StartupStage.Preferences;
            UiText.Instance.LoadPreference(Path.Combine(localRoot, "ui-language.json"));
            if (_profileLease is null)
            {
                MessageBox.Show(UiText.Instance.Get("startup.profileInUse"), "TeleSelfCloud", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
            LocalPreviewWorkspace.CleanupOrphans(localRoot, _profileLease);
            LocalOpenedFileWorkspace.CleanupOrphans(localRoot, _profileLease);
            LocalStagingContentStore.CleanupOrphans(localRoot, _profileLease);
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            stage = StartupStage.DatabaseProtection;
            using (var stop = new CancellationTokenSource())
            {
                var progressWindow = new LocalDatabaseStartupWindow(localRoot, stop, showOnProgress: true);
                try
                {
                    await LocalDatabaseStartup.PrepareAsync(localRoot, _profileLease,
                        root => Task.FromResult(new LocalDatabaseRecoveryWindow(root, _profileLease) { Owner = progressWindow.IsVisible ? progressWindow : null }.ShowDialog() == true), stop.Token, progressWindow);
                }
                finally { progressWindow.CompleteAndClose(); }
            }
            var databaseKey = LocalDatabaseProtection.KeyForOpening(localRoot);

            stage = StartupStage.LegacyDiagnostics;
            StartupDiagnostics.ProtectLegacyLog(localRoot);
            stage = StartupStage.CatalogMigration;

            var legacyStore = new JsonManifestStore(Path.Combine(localRoot, "manifests"));
            var manifestStore = new SqliteManifestStore(Path.Combine(localRoot, "manifests.db"), databaseKey);
            var transferQueueStore = new SqliteTransferQueueStore(Path.Combine(localRoot, "manifests.db"), databaseKey);
            var syncCheckpointStore = new SqliteRemoteSyncCheckpointStore(Path.Combine(localRoot, "manifests.db"), databaseKey);
            var folderStore = new SqliteLocalFolderStore(Path.Combine(localRoot, "manifests.db"), databaseKey);
            await manifestStore.ImportLegacyAsync(legacyStore, CancellationToken.None);
            var sharedStagingContent = LocalStagingContentStoreFactory.Create(localRoot, localRoot, _profileLease);
            var externalStagingCount = 0;
            if (LocalDatabaseProtection.IsConfigured(localRoot))
            {
                using var stagingStop = new CancellationTokenSource();
                var stagingProgress = new LocalDatabaseStartupWindow(localRoot, stagingStop, showOnProgress: true);
                try
                {
                    var stagingMigration = await LocalStagingProtectionMigrator.MigrateAsync(Path.Combine(localRoot, "staging"), manifestStore,
                        sharedStagingContent, stagingStop.Token, (current, total) => stagingProgress.Report(
                            new LocalDatabaseStartupProgress(localRoot, current, total, "StagingEncrypting")));
                    externalStagingCount = stagingMigration.OutsideCatalogCount;
                }
                finally { stagingProgress.CompleteAndClose(); }
            }
            await StagingOrphanReconciler.ReconcileAsync(localRoot, Path.Combine(localRoot, "staging"),
                manifestStore, _profileLease, DateTimeOffset.UtcNow, CancellationToken.None);
            stage = StartupStage.QueueRecovery;
            await transferQueueStore.RecoverInterruptedAsync(CancellationToken.None);
            stage = StartupStage.Window;
            var workflow = new LocalFileWorkflow(
                new FileTransferCoordinator(),
                manifestStore,
                new StagedPartAssembler(sharedStagingContent), sharedStagingContent);
            var window = new MainWindow(workflow, Path.Combine(localRoot, "staging"), manifestStore, transferQueueStore, syncCheckpointStore, folderStore) { DatabaseProfileLease = _profileLease };
            MainWindow = window;
            window.Show();
            window.ReportExternalStagingPaths(externalStagingCount);
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        catch (OperationCanceledException) { Shutdown(0); }
        catch (Exception ex)
        {
            var report = StartupDiagnostics.Create(ex, stage);
            try
            {
                if (_profileLease is not null)
                {
                    var logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeleSelfCloud", "P0");
                    StartupDiagnostics.Write(logRoot, report);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
            MessageBox.Show(
                string.Format(UiText.Instance.Get("startup.failed"), report.FailureCode, report.ReportId),
                UiText.Instance.Get("startup.failedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _profileLease?.Dispose(); _profileLease = null; }
        finally { base.OnExit(e); }
    }
}

