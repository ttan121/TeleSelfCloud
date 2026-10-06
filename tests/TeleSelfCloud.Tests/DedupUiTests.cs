using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class DedupUiTests
{
    private sealed class Requests : ITelegramUpdateSource
    {
        public event EventHandler<JsonObject>? UpdateReceived { add { } remove { } }
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("UI fixture must not contact Telegram.");
    }
    [Theory]
    [InlineData("vi", 760, 650)]
    [InlineData("en", 1100, 800)]
    public void OptInEncryptionBusyResumeFactoryAndUploadScopeGuards(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.DedupUi", Guid.NewGuid().ToString("N")); MainWindow? window = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = typeof(MainWindow);
            try
            {
                Directory.CreateDirectory(root); UiText.Instance.SetLanguage(language, Path.Combine(root, "ui-language.json"));
                var db = Path.Combine(root, "db"); var store = new SqliteManifestStore(db); Task.Run(() => store.ListAsync(default)).GetAwaiter().GetResult();
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), store, new StagedPartAssembler()), Path.Combine(root, "staging"), store,
                    new SqliteTransferQueueStore(db), new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);
                var dedup = Assert.IsType<CheckBox>(window.FindName("DeduplicateUploadCheck")); var encryption = Assert.IsType<CheckBox>(window.FindName("EncryptContentCheck"));
                Assert.NotEqual(true, dedup.IsChecked); Assert.True(dedup.IsEnabled); Assert.Equal(UiText.Instance.Get("upload.dedup"), AutomationProperties.GetName(dedup));
                Assert.Equal(UiText.Instance.Get("upload.dedupTip"), AutomationProperties.GetHelpText(dedup)); dedup.IsChecked = true;
                var help = Assert.IsType<ToolTip>(dedup.ToolTip);
                Assert.Equal(UiText.Instance.Get("upload.dedupTip"), help.Content);
                // Render the popup surface without opening a native window or a real profile.
                foreach (var token in new[] { "SurfaceContainer", "TextPrimary", "ContentBorder" }) help.Resources[token] = window.Resources[token];
                help.Measure(new Size(420, double.PositiveInfinity));
                help.Arrange(new Rect(new Point(), help.DesiredSize)); help.UpdateLayout();
                Assert.InRange(help.ActualWidth, 100, 420);
                Assert.InRange(help.ActualHeight, 60, 400); // Multiple lines, rather than a screen-wide strip.
                var helpCapture = Environment.GetEnvironmentVariable("TSC_TOOLTIP_CAPTURE");
                if (!string.IsNullOrWhiteSpace(helpCapture))
                {
                    Directory.CreateDirectory(helpCapture);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(help.ActualWidth), (int)Math.Ceiling(help.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(help);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var image = File.Create(Path.Combine(helpCapture, "dedup-tooltip-" + language + ".png")); encoder.Save(image);
                }
                var client = new Requests(); var transport = new TelegramFileTransport(client, -100, Path.Combine(root, "downloads"));
                var factory = type.GetMethod("CreateUploadDeduplication", flags)!;
                Assert.Null(factory.Invoke(window, [client, transport, "42", false]));
                var service = Assert.IsType<TelegramUploadDeduplication>(factory.Invoke(window, [client, transport, "42", true]));
                Assert.Contains(typeof(TelegramUploadDeduplication).GetFields(flags), field =>
                    field.FieldType == typeof(Func<FileManifest, CancellationToken, Task<string?>>) && field.GetValue(service) is not null);
                dedup.IsChecked = false; Assert.IsType<TelegramUploadDeduplication>(factory.Invoke(window, [client, transport, "42", true])); // Saved intent resume remains available.
                var toolbar = Assert.IsType<Border>(window.FindName("UploadToolbar")); toolbar.Visibility = Visibility.Visible;
                Assert.IsType<TextBlock>(window.FindName("ChosenFileName")).Text = "fixture-source.bin";
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                void Render(string state)
                {
                    content.Measure(new(width, height)); content.Arrange(new(0, 0, width, height)); content.UpdateLayout();
                    foreach (var control in new FrameworkElement[] { dedup, encryption, Assert.IsType<Button>(window.FindName("EnqueueUploadButton")) })
                    {
                        Assert.True(control.ActualWidth > 0); Assert.True(control.ActualHeight >= 28); var point = control.TransformToAncestor(content).Transform(new Point());
                        Assert.True(point.X >= 0 && point.X + control.ActualWidth <= width + 1); Assert.True(point.Y >= 0 && point.Y + control.ActualHeight <= height + 1);
                    }
                    var capture = Environment.GetEnvironmentVariable("TSC_DEDUP_UI_CAPTURE"); if (string.IsNullOrWhiteSpace(capture)) return; Directory.CreateDirectory(capture);
                    var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(toolbar), null, new Rect(0, 0, toolbar.ActualWidth, toolbar.ActualHeight));
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(toolbar.ActualWidth), (int)Math.Ceiling(toolbar.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(capture, "dedup-" + language + "-" + state + ".png")); encoder.Save(output);
                }
                Render("plain"); encryption.IsChecked = true; Assert.True(dedup.IsEnabled); dedup.IsChecked = true; Render("encrypted"); encryption.IsChecked = false; Assert.True(dedup.IsEnabled); dedup.IsChecked = false;
                type.GetField("_operationBusy", flags)!.SetValue(window, true); type.GetMethod("ApplyDedupAvailability", flags)!.Invoke(window, null); Assert.False(dedup.IsEnabled); Assert.False(encryption.IsEnabled);
                type.GetField("_operationBusy", flags)!.SetValue(window, false); type.GetField("_shutdownStarted", flags)!.SetValue(window, true); type.GetMethod("ApplyDedupAvailability", flags)!.Invoke(window, null); Assert.False(dedup.IsEnabled);
                type.GetField("_shutdownStarted", flags)!.SetValue(window, false);
                // Identity-only token: do not construct/start/dispose a native session or invoke any session methods.
                var identity = (TelegramAuthSession)RuntimeHelpers.GetUninitializedObject(typeof(TelegramAuthSession));
                var channel = new TelegramStorageChannelInfo(-100, "42", "Fixture"); var source = Path.Combine(root, "source"); var staging = Path.Combine(root, "staging");
                void Set(string name, object value) => type.GetField(name, flags)!.SetValue(window, value);
                Set("_telegramSession", identity); Set("_storageChannel", channel); Set("_activeTelegramAccountId", "42"); Set("_chosenFilePath", source);
                var match = type.GetMethod("UploadScopeMatches", flags)!;
                Assert.True((bool)match.Invoke(window, [identity, channel, store, source, staging])!);
                Set("_activeTelegramAccountId", "99"); Assert.False((bool)match.Invoke(window, [identity, channel, store, source, staging])!); Set("_activeTelegramAccountId", "42");
                Set("_chosenFilePath", source + ".changed"); Assert.False((bool)match.Invoke(window, [identity, channel, store, source, staging])!);
                Set("_chosenFilePath", source);
                Assert.False((bool)match.Invoke(window, [identity, channel, new SqliteManifestStore(db), source, staging])!);
                Set("_storageChannel", new TelegramStorageChannelInfo(-101, "42", "Other vault")); Assert.False((bool)match.Invoke(window, [identity, channel, store, source, staging])!);
                type.GetField("_telegramSession", flags)!.SetValue(window, null);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null) { type.GetField("_telegramSession", flags)!.SetValue(window, null); type.GetField("_shutdownStarted", flags)!.SetValue(window, false); var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame); }
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Dedup UI fixture timed out."); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
