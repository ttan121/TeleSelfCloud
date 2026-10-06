using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class LocalizedTextBlockLifecycleTests
{
    [Fact]
    public void LoadedControlMarshalsLanguageChangesAndUnloadedControlCanBeReloaded()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LocalizedLifecycle", Guid.NewGuid().ToString("N"));
            var dispatcher = Dispatcher.CurrentDispatcher;
            var language = Path.Combine(root, "language.json");
            LocalizedTextBlock? control = null;
            try
            {
                UiText.Instance.SetLanguage("en", language);
                control = new LocalizedTextBlock { Text = "Log in and connect storage before starting queued transfers." };
                var english = control.Text;
                control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Task.Run(() => UiText.Instance.SetLanguage("vi", language)).GetAwaiter().GetResult();
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Assert.Contains("Hãy đăng nhập", control.Text);
                var vietnamese = control.Text;
                control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Task.Run(() => UiText.Instance.SetLanguage("en", language)).GetAwaiter().GetResult();
                Assert.Equal(vietnamese, control.Text);
                control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(english, control.Text);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                control?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                dispatcher.InvokeShutdown();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Localization lifecycle test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
