using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class LocalDatabaseProgressUiTests
{
    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void ProgressReportsStepsAndLateStopWaitsWithoutClosingOrReopening(string language)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ProgressUi", Guid.NewGuid().ToString("N"));
            LocalDatabaseStartupWindow? window = null;
            try
            {
                Directory.CreateDirectory(root); UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                using var stop = new CancellationTokenSource(); window = new(root, stop);
                window.Report(new(root, 0, 0, "Inventory")); Assert.Equal(UiText.Instance.Get("localdb.inventoryScope"), window.ScopeText.Text);
                Assert.Equal(UiText.Instance.Get("localdb.stage.Inventory"), window.StageText.Text);
                window.Report(new(root, 1, 2, "Copying"));
                Assert.Equal(UiText.Instance.Get("localdb.stage.Copying"), window.StageText.Text);
                Assert.Equal(UiText.Instance.Get("localdb.stopStartup"), window.StopButton.Content); Assert.False(window.IsVisible);
                window.Report(new(root, 1, 2, "Switching"));
                Assert.Equal(UiText.Instance.Get("localdb.stopAfterCurrent"), window.StopButton.Content);
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content); content.Measure(new(540, 320)); content.Arrange(new(0, 0, 540, 320)); content.UpdateLayout();
                var capture = Environment.GetEnvironmentVariable("TSC_DATABASE_UI_CAPTURE");
                void Save(string name)
                {
                    if (string.IsNullOrWhiteSpace(capture)) return; Directory.CreateDirectory(capture);
                    var bitmap = new RenderTargetBitmap(540, 320, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(capture, name)); encoder.Save(output);
                }
                Save("progress-" + language + ".png");
                window.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(stop.IsCancellationRequested); Assert.False(window.StopButton.IsEnabled);
                Assert.Equal(UiText.Instance.Get("localdb.stopping"), window.StageText.Text);
                // Worker reports are marshalled; stopping text must survive late Ready updates.
                Task.Run(() => window.Report(new(root, 1, 2, "Ready"))).GetAwaiter().GetResult();
                dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(UiText.Instance.Get("localdb.stopping"), window.StageText.Text);
                content.Measure(new(540, 320)); content.Arrange(new(0, 0, 540, 320)); content.UpdateLayout(); Save("stopping-" + language + ".png");
                var closed = 0; window.Closed += (_, _) => closed++;
                window.Close(); Assert.Equal(0, closed); // X waits for worker completion.
                window.CompleteAndClose(); Assert.Equal(1, closed);
                var text = window.StageText.Text; window.Report(new(root, 2, 2, "Checking")); Assert.Equal(text, window.StageText.Text); Assert.False(window.IsVisible);
                window = null;
                // App uses automatic Show: queued reports after completion must not
                // reopen a closed window, even after its CancellationTokenSource ends.
                using var endedStop = new CancellationTokenSource();
                var ended = new LocalDatabaseStartupWindow(root, endedStop, showOnProgress: true); ended.CompleteAndClose();
                Task.Run(() => ended.Report(new(root, 2, 2, "Checking"))).GetAwaiter().GetResult(); dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.False(ended.IsVisible);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.CompleteAndClose(); dispatcher.InvokeShutdown(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Progress UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
