using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Tests;

public sealed class DedupFallbackUiTests
{
    [Theory]
    [InlineData("success", "vi")]
    [InlineData("success", "en")]
    [InlineData("running", "vi")]
    [InlineData("busy", "vi")]
    [InlineData("selection", "vi")]
    [InlineData("failure", "vi")]
    [InlineData("account-changed", "vi")]
    public void ExplicitSwitchPreservesQueueStateAndRejectsUnsafeScopes(string mode, string language)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.FallbackUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null; LocalProfileLease? lease = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = typeof(MainWindow);
            void Pump(Task task)
            {
                var frame = new DispatcherFrame(); task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
                if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            }
            try
            {
                Directory.CreateDirectory(root); UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                var db = Path.Combine(root, "manifests.db"); var store = new SqliteManifestStore(db); var queue = new SqliteTransferQueueStore(db);
                var manifest = new FileManifest(1, "target", "fixture.bin", 1, new string('A', 64), 1,
                    [new(0, 0, 1, new string('A', 64), null, false, Path.Combine(root, "part"), new("42", "original", 0, "-100/10"))], false, "42");
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), store, new StagedPartAssembler()), Path.Combine(root, "staging"), store,
                    queue, new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);
                lease = LocalProfileLease.TryAcquire(root)!; window.DatabaseProfileLease = lease;
                void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(window, value);
                void Refresh(string method) => Pump((Task)type.GetMethod(method, flags)!.Invoke(window, null)!);
                Set("_activeTelegramAccountId", "42");
                Pump((Task)type.GetMethod("OpenRegisteredVaultAsync", flags)!.Invoke(window, [new TelegramStorageChannelInfo(-100, "42", "Fixture"), CancellationToken.None])!);
                store = Assert.IsType<SqliteManifestStore>(type.GetField("_manifestStore", flags)!.GetValue(window));
                queue = Assert.IsType<SqliteTransferQueueStore>(type.GetField("_transferQueueStore", flags)!.GetValue(window));
                Task.Run(async () => { await store.SaveAsync(manifest, default); await queue.EnsureAsync("target", "fixture.bin", 1, default); await queue.SetStateAsync("target", TransferQueueState.Running, null, default); await queue.SetStateAsync("target", TransferQueueState.Failed, "fixture", default); }).GetAwaiter().GetResult();
                Refresh("RefreshManifestsAsync"); Refresh("RefreshQueueAsync");
                var list = Assert.IsType<ListView>(window.FindName("TransferList")); list.SelectedIndex = 0;
                var button = Assert.IsType<Button>(window.FindName("CopyFallbackButton"));
                Assert.Equal(Visibility.Visible, button.Visibility); Assert.False(button.IsEnabled); // No authenticated session in this fixture.
                Assert.Equal(UiText.Instance.Get("dedup.fallbackAction"), AutomationProperties.GetName(button));
                var panel = Assert.IsType<Border>(window.FindName("TransferDetailPanel"));
                panel.Measure(new(300, 250)); panel.Arrange(new(0, 0, 300, panel.DesiredSize.Height)); panel.UpdateLayout();
                foreach (var control in ((WrapPanel)panel.Child).Children.OfType<Button>())
                {
                    var point = control.TransformToAncestor(panel).Transform(new Point());
                    Assert.True(control.ActualHeight >= 28); Assert.True(point.X >= 0 && point.X + control.ActualWidth <= 301);
                    Assert.True(point.Y >= 0 && point.Y + control.ActualHeight <= panel.ActualHeight + 1);
                }
                var capture = Environment.GetEnvironmentVariable("TSC_FALLBACK_UI_CAPTURE");
                if (mode == "success" && !string.IsNullOrWhiteSpace(capture))
                {
                    Directory.CreateDirectory(capture); var image = new RenderTargetBitmap(300, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(panel);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(capture, "fallback-" + language + ".png")); encoder.Save(output);
                }
                var item = Task.Run(() => queue.ListAsync(default)).GetAwaiter().GetResult().Single();
                if (mode == "running") Task.Run(async () => { await queue.EnsureAsync("other", "other.bin", 1, default); await queue.SetStateAsync("other", TransferQueueState.Running, null, default); }).GetAwaiter().GetResult();
                if (mode == "busy") Set("_operationBusy", true);
                if (mode == "selection") list.SelectedIndex = -1;
                var called = false;
                async Task<FileManifest> Action(CancellationToken token)
                {
                    called = true; await Task.Yield(); token.ThrowIfCancellationRequested();
                    if (mode == "failure") throw new IOException("fixture private detail must not appear in UI");
                    var updated = manifest with { Parts = [manifest.Parts[0] with { CopySource = null, RemoteId = "-100/20", Confirmed = true }] };
                    await store.SaveAsync(updated, token);
                    if (mode == "account-changed") Set("_activeTelegramAccountId", "99");
                    return updated;
                }
                Pump((Task)type.GetMethod("SaveCopyFallbackAsync", flags)!.Invoke(window, [item, (Func<CancellationToken, Task<FileManifest>>)Action])!);
                var updatedTask = Task.Run(() => queue.ListAsync(default)).GetAwaiter().GetResult().Single(t => t.TaskId == "target");
                Assert.Equal(TransferQueueState.Failed, updatedTask.State); Assert.Equal(1, updatedTask.AttemptCount);
                var updatedManifest = Task.Run(() => store.LoadAsync("target", default)).GetAwaiter().GetResult()!;
                var saved = mode is "success" or "account-changed";
                Assert.Equal(saved ? 1 : 0, updatedTask.TransferredBytes); Assert.Equal(!saved, updatedManifest.Parts[0].CopySource is not null);
                Assert.Equal(mode is "success" or "account-changed" or "failure", called);
                var statusKey = mode switch { "success" => "dedup.fallbackDone", "account-changed" => "dedup.fallbackOriginal", "failure" => "dedup.fallbackFailed", _ => "dedup.fallbackScope" };
                Assert.Equal(UiText.Instance.Get(statusKey), Assert.IsAssignableFrom<TextBlock>(window.FindName("StatusText")).Text);
                Set("_operationBusy", false);
                if (mode == "success") Assert.Equal(Visibility.Collapsed, button.Visibility);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    type.GetField("_operationBusy", flags)!.SetValue(window, false);
                    var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame);
                }
                lease?.Dispose(); dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Fallback UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
