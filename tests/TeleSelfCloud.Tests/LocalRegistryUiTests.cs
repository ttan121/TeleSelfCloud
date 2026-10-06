using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalRegistryUiTests
{
    [Theory]
    [InlineData("vi", 760, 650)]
    [InlineData("en", 1100, 800)]
    public void AccountRegistrySettingsKeepPrimaryScopeWhileSecondaryVaultIsActive(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.RegistryUi", Guid.NewGuid().ToString("N")); var shared = Path.Combine(parent, "profile");
            var accountRoot = TelegramAccountProfileStore.GetDirectory(shared, "42"); MainWindow? window = null; LocalProfileLease? lease = null;
            try
            {
                Directory.CreateDirectory(accountRoot); UiText.Instance.SetLanguage(language, Path.Combine(shared, "ui-language.json"));
                var db = Path.Combine(shared, "manifests.db"); var store = new SqliteManifestStore(db); Task.Run(() => store.ListAsync(default)).GetAwaiter().GetResult();
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), store, new StagedPartAssembler()), Path.Combine(shared, "staging"), store,
                    new SqliteTransferQueueStore(db), new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), shared);
                lease = LocalProfileLease.TryAcquire(shared)!; window.DatabaseProfileLease = lease;
                var type = typeof(MainWindow); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                void Set(string field, object value) => type.GetField(field, flags)!.SetValue(window, value);
                void Refresh() => type.GetMethod("ApplyLocalDatabaseAvailability", flags)!.Invoke(window, null);
                var button = Assert.IsType<Button>(window.FindName("EnableLocalRegistryProtectionButton")); var report = Assert.IsType<TextBlock>(window.FindName("LocalRegistryReport"));
                Assert.False(button.IsEnabled); Assert.Equal(UiText.Instance.Get("localregistry.selectAccount"), report.Text);
                Set("_telegramAccountLoaded", true); Set("_activeTelegramAccountId", "42"); Refresh();
                Assert.False(button.IsEnabled); Assert.Equal(UiText.Instance.Get("localregistry.prerequisite"), report.Text);
                var accountDb = Path.Combine(accountRoot, "manifests.db"); Task.Run(() => new SqliteManifestStore(accountDb).ListAsync(default)).GetAwaiter().GetResult();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                LocalDatabaseProtection.Request(accountRoot, Path.Combine(parent, "account-key.json"), "registry UI recovery passphrase", lease);
                Task.Run(() => LocalDatabaseStartup.PrepareAsync(shared, lease, _ => throw new InvalidOperationException(), default)).GetAwaiter().GetResult();
                var registry = new TelegramVaultRegistry(accountRoot, "42");
                Task.Run(() => registry.RegisterAsync(new(-101, "42", "Primary"), true, default)).GetAwaiter().GetResult();
                var state = Task.Run(() => registry.RegisterAsync(new(-102, "42", "Secondary"), true, default)).GetAwaiter().GetResult();
                var secondary = registry.GetDataDirectory(state, -102); Directory.CreateDirectory(secondary);
                Set("_stagingRoot", Path.Combine(secondary, "staging")); Refresh(); Assert.True(button.IsEnabled);
                Assert.Equal(string.Format(UiText.Instance.Get("localregistry.scope"), "42"), Assert.IsType<TextBlock>(window.FindName("LocalRegistryScope")).Text);
                var save = type.GetMethod("SaveLocalRegistryProtectionAsync", flags)!;
                foreach (var field in new[] { "_operationBusy", "_shutdownStarted" })
                {
                    Set(field, true); ((Task)save.Invoke(window, [accountRoot, "42", lease])!).GetAwaiter().GetResult();
                    Assert.False(LocalVaultRegistryProtection.IsConfigured(accountRoot)); Set(field, false);
                }
                Set("_activeTelegramAccountId", "99"); ((Task)save.Invoke(window, [accountRoot, "42", lease])!).GetAwaiter().GetResult();
                Assert.False(LocalVaultRegistryProtection.IsConfigured(accountRoot)); Set("_activeTelegramAccountId", "42"); Refresh();
                type.GetMethod("NavigateToPage", flags)!.Invoke(window, ["settings"]);
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var capture = Environment.GetEnvironmentVariable("TSC_REGISTRY_UI_CAPTURE");
                void Capture(string stage)
                {
                    content.Measure(new(width, height)); content.Arrange(new(0, 0, width, height)); content.UpdateLayout();
                    Assert.True(button.ActualWidth > 0); Assert.True(button.ActualHeight >= 28);
                    var location = button.TransformToAncestor(content).Transform(new Point()); Assert.True(location.X >= 0 && location.X + button.ActualWidth <= width + 1);
                    if (string.IsNullOrWhiteSpace(capture)) return; Directory.CreateDirectory(capture);
                    var card = Assert.IsType<Border>(window.FindName("LocalDatabaseActions"));
                    var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(capture, "registry-" + language + "-" + stage + ".png")); encoder.Save(output);
                }
                Capture("off"); var before = File.ReadAllBytes(Path.Combine(accountRoot, "vaults.json"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(UiText.Instance.Get("localregistry.working"), Assert.IsAssignableFrom<TextBlock>(window.FindName("StatusText")).Text);
                if (language == "en") { Assert.True((bool)type.GetField("_operationBusy", flags)!.GetValue(window)!); Set("_activeTelegramAccountId", "99"); }
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) =>
                { if (!(bool)type.GetField("_operationBusy", flags)!.GetValue(window)!) frame.Continue = false; }, dispatcher);
                timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
                if (language == "en")
                {
                    Assert.Equal(UiText.Instance.Get("localregistry.savedOriginal"), Assert.IsAssignableFrom<TextBlock>(window.FindName("StatusText")).Text);
                    Assert.False(LocalVaultRegistryProtection.IsConfigured(TelegramAccountProfileStore.GetDirectory(shared, "99")));
                    Set("_activeTelegramAccountId", "42"); Refresh();
                }
                Assert.True(LocalVaultRegistryProtection.IsConfigured(accountRoot)); Assert.False(LocalVaultRegistryProtection.IsConfigured(secondary));
                Assert.Equal(before, File.ReadAllBytes(Path.Combine(accountRoot, "vaults.json"))); Assert.False(button.IsEnabled);
                Assert.Equal(UiText.Instance.Get("localregistry.pending"), report.Text); Capture("pending");
                Task.Run(() => LocalDatabaseStartup.PrepareAsync(shared, lease, _ => throw new InvalidOperationException(), default)).GetAwaiter().GetResult();
                Refresh(); Assert.Equal(UiText.Instance.Get("localregistry.enabled"), report.Text); Capture("enabled");
                using var protectedRegistry = LocalVaultRegistryProtection.OpenRegistry(accountRoot, "42");
                Assert.Equal(-102, Task.Run(() => protectedRegistry.LoadAsync(default)).GetAwaiter().GetResult()!.ActiveChatId);
                var runtimeRegistry = Assert.IsType<TelegramVaultRegistry>(type.GetMethod("CreateVaultRegistry", flags)!.Invoke(window, ["42"]));
                Assert.Same(runtimeRegistry, type.GetMethod("CreateVaultRegistry", flags)!.Invoke(window, ["42"]));
                var closed = new DispatcherFrame(); window.Closed += (_, _) => closed.Continue = false; window.Close(); if (closed.Continue) Dispatcher.PushFrame(closed);
                Assert.Throws<ObjectDisposedException>(() => runtimeRegistry.GetDataDirectory(state, -101)); window = null;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null) { var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame); }
                lease?.Dispose(); dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(parent)) Directory.Delete(parent, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Registry UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
