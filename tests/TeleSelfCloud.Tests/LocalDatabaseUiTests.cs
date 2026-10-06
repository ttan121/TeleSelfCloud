using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalDatabaseUiTests
{
    [Theory]
    [InlineData("vi", 760, 650)]
    [InlineData("en", 1000, 760)]
    public void SettingsReportsOffPendingReadyAndScopedBusyActionsWithoutStartingSession(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LocalDbUi", Guid.NewGuid().ToString("N")); var root = Path.Combine(parent, "profile");
            MainWindow? window = null; LocalDatabaseRecoveryWindow? recovery = null; LocalProfileLease? lease = null;
            try
            {
                Directory.CreateDirectory(root); UiText.Instance.SetLanguage(language, Path.Combine(root, "ui-language.json"));
                var db = Path.Combine(root, "manifests.db"); var store = new SqliteManifestStore(db);
                Task.Run(() => store.ListAsync(default)).GetAwaiter().GetResult();
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), store, new StagedPartAssembler()), Path.Combine(root, "staging"), store,
                    new SqliteTransferQueueStore(db), new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);
                var enable = Assert.IsType<Button>(window.FindName("EnableLocalDatabaseButton")); Assert.False(enable.IsEnabled);
                lease = LocalProfileLease.TryAcquire(root)!; window.DatabaseProfileLease = lease; Assert.True(enable.IsEnabled);
                var report = Assert.IsType<TextBlock>(window.FindName("LocalDatabaseReport")); Assert.Equal(UiText.Instance.Get("localdb.off"), report.Text);
                var type = typeof(MainWindow);
                var saveMethod = type.GetMethod("SaveLocalDatabaseKeyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var rejected = Path.Combine(parent, "must-not-write.json");
                var staging = type.GetField("_stagingRoot", BindingFlags.Instance | BindingFlags.NonPublic)!;
                staging.SetValue(window, Path.Combine(root, "accounts", "42", "staging"));
                ((Task)saveMethod.Invoke(window, [root, lease, rejected, "ui fixture recovery passphrase", true])!).GetAwaiter().GetResult();
                Assert.False(File.Exists(rejected)); Assert.False(LocalDatabaseProtection.IsConfigured(root));
                Assert.Equal(UiText.Instance.Get("localdb.scopeChanged"), Assert.IsAssignableFrom<TextBlock>(window.FindName("StatusText")).Text);
                staging.SetValue(window, Path.Combine(root, "staging"));
                foreach (var field in new[] { "_operationBusy", "_shutdownStarted" })
                {
                    type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    ((Task)saveMethod.Invoke(window, [root, lease, rejected, "ui fixture recovery passphrase", true])!).GetAwaiter().GetResult();
                    Assert.False(File.Exists(rejected)); Assert.False(LocalDatabaseProtection.IsConfigured(root));
                    type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                }
                LocalDatabaseProtection.Request(root, Path.Combine(parent, "recovery.tsc-db-key.json"), "ui fixture recovery passphrase", lease);
                var refresh = type.GetMethod("ApplyLocalDatabaseAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!;
                refresh.Invoke(window, null); Assert.False(enable.IsEnabled); Assert.Equal(UiText.Instance.Get("localdb.pending"), report.Text);
                Task.Run(() => LocalDatabaseStartup.PrepareAsync(root, lease, _ => throw new InvalidOperationException(), default)).GetAwaiter().GetResult();
                refresh.Invoke(window, null); Assert.Equal(UiText.Instance.Get("localdb.enabled"), report.Text);
                var cacheEnable = Assert.IsType<Button>(window.FindName("EnableLocalCacheProtectionButton")); Assert.True(cacheEnable.IsEnabled);
                cacheEnable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(LocalCacheProtection.IsConfigured(root)); Assert.False(cacheEnable.IsEnabled);
                Assert.Equal(UiText.Instance.Get("localcache.pending"), Assert.IsType<TextBlock>(window.FindName("LocalCacheProtectionReport")).Text);
                Task.Run(() => LocalDatabaseStartup.PrepareAsync(root, lease, _ => throw new InvalidOperationException(), default)).GetAwaiter().GetResult();
                refresh.Invoke(window, null); Assert.Equal(UiText.Instance.Get("localcache.enabled"), Assert.IsType<TextBlock>(window.FindName("LocalCacheProtectionReport")).Text);
                Assert.True(Assert.IsType<Button>(window.FindName("ExportLocalDatabaseKeyButton")).IsEnabled);
                type.GetField("_operationBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true); refresh.Invoke(window, null);
                Assert.False(Assert.IsType<Button>(window.FindName("RecoverLocalDatabaseKeyButton")).IsEnabled);
                type.GetField("_operationBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false); refresh.Invoke(window, null);
                // Render actual Settings content; never Show/Loaded or restore authentication.
                type.GetMethod("NavigateToPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["settings"]);
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content); content.Measure(new(width, height)); content.Arrange(new(0, 0, width, height)); content.UpdateLayout();
                foreach (var name in new[] { "EnableLocalDatabaseButton", "ExportLocalDatabaseKeyButton", "RecoverLocalDatabaseKeyButton" })
                {
                    var button = Assert.IsType<Button>(window.FindName(name)); Assert.True(button.ActualWidth > 0); Assert.True(button.ActualHeight >= 28);
                    var point = button.TransformToAncestor(content).Transform(new Point(0, 0)); Assert.True(point.X >= 0 && point.X + button.ActualWidth <= width + 1);
                }
                var capture = Environment.GetEnvironmentVariable("TSC_DATABASE_UI_CAPTURE");
                void Save(FrameworkElement visual, string name, int w, int h)
                {
                    if (string.IsNullOrWhiteSpace(capture)) return; Directory.CreateDirectory(capture);
                    var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(capture, name)); encoder.Save(output);
                }
                Save(content, "settings-" + language + ".png", width, height);
                recovery = new LocalDatabaseRecoveryWindow(root, lease); var recoveryContent = Assert.IsAssignableFrom<FrameworkElement>(recovery.Content);
                recoveryContent.Measure(new(540, 540)); recoveryContent.Arrange(new(0, 0, 540, 540)); recoveryContent.UpdateLayout();
                var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(recovery.Content).Content);
                var password = Assert.Single(panel.Children.OfType<PasswordBox>()); Assert.Equal(UiText.Instance.Get("metadata.crypto.passphrase"), AutomationProperties.GetName(password));
                var actions = Assert.IsType<WrapPanel>(panel.Children[^1]); Assert.False(Assert.IsType<Button>(actions.Children[0]).IsEnabled);
                Save(recoveryContent, "recovery-" + language + ".png", 540, 540);
                recovery.Close();
                var keyPath = Path.Combine(root, "local-db-key.dpapi"); var keyBefore = File.ReadAllBytes(keyPath);
                recovery = new LocalDatabaseRecoveryWindow(root, lease, () => false);
                var recoveryType = typeof(LocalDatabaseRecoveryWindow);
                ((TextBox)recoveryType.GetField("source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recovery)!).Text = Path.Combine(parent, "recovery.tsc-db-key.json");
                ((PasswordBox)recoveryType.GetField("password", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recovery)!).Password = "ui fixture recovery passphrase";
                var guardedPanel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(recovery.Content).Content);
                var guardedRestore = Assert.IsType<Button>(Assert.IsType<WrapPanel>(guardedPanel.Children[^1]).Children[0]); Assert.True(guardedRestore.IsEnabled);
                guardedRestore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(UiText.Instance.Get("localdb.scopeChanged"), ((TextBlock)recoveryType.GetField("error", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recovery)!).Text);
                Assert.Equal(keyBefore, File.ReadAllBytes(keyPath)); Assert.Empty(Directory.GetFiles(root, "local-db-key.dpapi.*.backup"));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                recovery?.Close();
                if (window is not null) { var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame); }
                lease?.Dispose(); dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(parent)) Directory.Delete(parent, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Database UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
