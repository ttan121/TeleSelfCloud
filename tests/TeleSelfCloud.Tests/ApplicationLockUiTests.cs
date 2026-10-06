using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class ApplicationLockUiTests
{
    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void ConfiguredProfileStartsBehindAccessibleLockOverlay(string language)
    {
        RunOnSta((window, root, dispatcher) =>
        {
            var overlay = Assert.IsType<Grid>(window.FindName("AppLockOverlay"));
            Assert.Equal(byte.MaxValue, Assert.IsType<SolidColorBrush>(overlay.Background).Color.A);
            var unlock = Assert.IsType<PasswordBox>(window.FindName("AppLockUnlockPasswordBox"));
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(UiText.Instance.Get("appLock.passphrase"), AutomationProperties.GetName(unlock));
            Assert.Equal(Visibility.Visible, unlock.Visibility);
            var status = Assert.IsAssignableFrom<TextBlock>(window.FindName("AppLockStatusText"));
            Assert.Equal(UiText.Instance.Get("appLock.statusOn"), status.Text);
            Capture(window, "AppLockOverlay", $"locked-{language}.png");
            var password = Assert.IsType<PasswordBox>(window.FindName("AppLockUnlockPasswordBox"));
            password.Password = "correct horse battery staple";
            Invoke(window, "AppLockUnlock_Click", window, new RoutedEventArgs());
            PumpUntil(dispatcher, () => !BoolField(window, "_applicationLocked"));
            Invoke(window, "NavigateToPage", "settings");
            Invoke(window, "AppLockConfigure_Click", window, new RoutedEventArgs());
            var setup = Assert.IsType<Grid>(window.FindName("AppLockSetupOverlay"));
            Assert.Equal(Visibility.Visible, setup.Visibility);
            Assert.Equal(UiText.Instance.Get("appLock.newPassphrase"), AutomationProperties.GetName(Assert.IsType<PasswordBox>(window.FindName("AppLockNewPasswordBox"))));
            Capture(window, "AppLockSetupOverlay", $"setup-{language}.png");
            Invoke(window, "AppLockSetupCancel_Click", window, new RoutedEventArgs());
        }, language);
    }

    [Fact]
    public void WrongPassphraseKeepsWindowLockedCorrectPassphraseUnlocksAndIdleRelocksWithoutPausingQueue()
    {
        RunOnSta((window, root, dispatcher) =>
        {
            var queue = (SqliteTransferQueueStore)typeof(MainWindow).GetField("_transferQueueStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Task.Run(async () =>
            {
                await queue.EnsureAsync("running", "running.bin", 1, default);
                await queue.SetStateAsync("running", TransferQueueState.Running, null, default);
            }).GetAwaiter().GetResult();
            var password = Assert.IsType<PasswordBox>(window.FindName("AppLockUnlockPasswordBox"));
            var overlay = Assert.IsType<Grid>(window.FindName("AppLockOverlay"));
            password.Password = "wrong passphrase";
            Invoke(window, "AppLockUnlock_Click", window, new RoutedEventArgs());
            PumpUntil(dispatcher, () => !BoolField(window, "_unlockVerificationInProgress"));
            Assert.True(BoolField(window, "_applicationLocked"));
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(UiText.Instance.Get("appLock.invalid"), Assert.IsAssignableFrom<TextBlock>(window.FindName("AppLockUnlockErrorText")).Text);

            password.Password = "correct horse battery staple";
            PumpFor(dispatcher, TimeSpan.FromMilliseconds(1100));
            Invoke(window, "AppLockUnlock_Click", window, new RoutedEventArgs());
            PumpUntil(dispatcher, () => !BoolField(window, "_applicationLocked"));
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(TransferQueueState.Running, Task.Run(async () => (await queue.ListAsync(default)).Single()).GetAwaiter().GetResult().State);

            Invoke(window, "AppLockNow_Click", window, new RoutedEventArgs());
            Assert.True(BoolField(window, "_applicationLocked"));
            var lastInput = DateTimeOffset.UtcNow.AddMinutes(-4);
            Field(window, "_lastApplicationInputUtc", lastInput);
            Invoke(window, "AppLock_PreProcessInput", null, null);
            Assert.Equal(lastInput, Field(window, "_lastApplicationInputUtc"));
            password.Password = "correct horse battery staple";
            Invoke(window, "AppLockUnlock_Click", window, new RoutedEventArgs());
            Assert.True(BoolField(window, "_unlockVerificationInProgress"));
            Invoke(window, "LockApplication"); // A system-lock signal arriving during PBKDF2 must invalidate that pending unlock.
            PumpUntil(dispatcher, () => !BoolField(window, "_unlockVerificationInProgress"));
            Assert.True(BoolField(window, "_applicationLocked"));
            password.Password = "correct horse battery staple";
            Invoke(window, "AppLockUnlock_Click", window, new RoutedEventArgs());
            PumpUntil(dispatcher, () => !BoolField(window, "_applicationLocked"));

            Invoke(window, "AppLock_SessionSwitch", null, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock));
            PumpUntil(dispatcher, () => BoolField(window, "_applicationLocked"));
            password.Password = "correct horse battery staple";
            Invoke(window, "AppLockUnlock_Click", window, new RoutedEventArgs());
            PumpUntil(dispatcher, () => !BoolField(window, "_applicationLocked"));
            Invoke(window, "AppLock_SessionSwitch", null, new SessionSwitchEventArgs(SessionSwitchReason.RemoteDisconnect));
            PumpUntil(dispatcher, () => BoolField(window, "_applicationLocked"));
            password.Password = "correct horse battery staple";
            Invoke(window, "AppLockUnlock_Click", window, new RoutedEventArgs());
            PumpUntil(dispatcher, () => !BoolField(window, "_applicationLocked"));
            Invoke(window, "AppLock_SessionSwitch", null, new SessionSwitchEventArgs(SessionSwitchReason.SessionUnlock));
            PumpUntil(dispatcher, () => BoolField(window, "_applicationLocked"));
            password.Password = "correct horse battery staple";
            Invoke(window, "AppLockUnlock_Click", window, new RoutedEventArgs());
            PumpUntil(dispatcher, () => !BoolField(window, "_applicationLocked"));

            Field(window, "_lastApplicationInputUtc", DateTimeOffset.UtcNow.AddMinutes(-6));
            Invoke(window, "AppLockTimer_Tick", window, EventArgs.Empty);
            Assert.True(BoolField(window, "_applicationLocked"));
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(TransferQueueState.Running, Task.Run(async () => (await queue.ListAsync(default)).Single()).GetAwaiter().GetResult().State);
        });
    }

    [Fact]
    public void CorruptLockRecordKeepsTheProfileBehindOfflineRecoveryScreen()
    {
        RunOnSta((window, root, _) =>
        {
            File.WriteAllText(Path.Combine(root, "app-lock.json"), "{");
            Invoke(window, "InitializeApplicationLock");
            Assert.True(BoolField(window, "_appLockFaulted"));
            Assert.True(BoolField(window, "_applicationLocked"));
            Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("AppLockOverlay")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<PasswordBox>(window.FindName("AppLockUnlockPasswordBox")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(window.FindName("AppLockRecoveryText")).Visibility);
        });
    }

    [Fact]
    public void ExistingProfileWithoutPolicyStaysUnlockedAndCanEnableAppLockFromSettings()
    {
        RunOnSta((window, root, _) =>
        {
            Assert.False(BoolField(window, "_applicationLocked"));
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(window.FindName("AppLockOverlay")).Visibility);
            Assert.Equal(UiText.Instance.Get("appLock.statusOff"), Assert.IsAssignableFrom<TextBlock>(window.FindName("AppLockStatusText")).Text);
            Invoke(window, "NavigateToPage", "settings");
            Invoke(window, "AppLockConfigure_Click", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(window.FindName("AppLockCurrentCredentialPanel")).Visibility);
            var newPassword = Assert.IsType<PasswordBox>(window.FindName("AppLockNewPasswordBox"));
            var confirmation = Assert.IsType<PasswordBox>(window.FindName("AppLockConfirmPasswordBox"));
            newPassword.Password = "new local app passphrase";
            confirmation.Password = "new local app passphrase";
            Invoke(window, "AppLockSave_Click", window, new RoutedEventArgs());
            Assert.True(BoolField(window, "_appLockConfigured"));
            Assert.True(new AppLockStore(Path.Combine(root, "app-lock.json")).Verify("new local app passphrase"));
            Assert.Equal(5, ((ComboBox)window.FindName("AppLockIdleTimeoutBox")!).SelectedItem is ComboBoxItem { Tag: string tag } ? int.Parse(tag) : -1);
            Invoke(window, "AppLockNow_Click", window, new RoutedEventArgs());
            Assert.True(BoolField(window, "_applicationLocked"));
        }, configureAppLock: false);
    }

    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static bool BoolField(MainWindow window, string name) => Assert.IsType<bool>(Field(window, name));
    private static void Field(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> predicate)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) =>
        {
            if (predicate()) frame.Continue = false;
        }, dispatcher);
        timer.Start();
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop(); timeout.Stop();
        Assert.True(predicate(), "The asynchronous app-lock action did not settle in time.");
    }

    private static void PumpFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }

    private static void Capture(MainWindow window, string elementName, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("TSC_APP_LOCK_CAPTURE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        window.Width = 760; window.Height = 650;
        window.Measure(new Size(760, 650)); window.Arrange(new Rect(0, 0, 760, 650)); window.UpdateLayout();
        var overlay = Assert.IsType<Grid>(window.FindName(elementName));
        overlay.Measure(new Size(760, 650)); overlay.Arrange(new Rect(0, 0, 760, 650)); overlay.UpdateLayout();
        var image = new RenderTargetBitmap(760, 650, 96, 96, PixelFormats.Pbgra32); image.Render(overlay);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, fileName)); encoder.Save(output);
    }

    private static void RunOnSta(Action<MainWindow, string, Dispatcher> action, string language = "en", bool configureAppLock = true)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.AppLockUi", Guid.NewGuid().ToString("N"));
            LocalProfileLease? lease = null;
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                lease = LocalProfileLease.TryAcquire(root)!;
                if (configureAppLock) new AppLockStore(Path.Combine(root, "app-lock.json")).Configure("correct horse battery staple", 5);
                var database = Path.Combine(root, "manifests.db");
                var manifests = new SqliteManifestStore(database);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(database),
                    new SqliteRemoteSyncCheckpointStore(database), new SqliteLocalFolderStore(database), root);
                window.DatabaseProfileLease = lease;
                action(window, root, dispatcher);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    typeof(MainWindow).GetField("_operationBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                    var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame);
                }
                lease?.Dispose(); dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "The app-lock UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
