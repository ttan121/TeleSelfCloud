using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LegacyLocalDataMutationUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(TransferQueueState.Pending)]
    [InlineData(TransferQueueState.Paused)]
    [InlineData(TransferQueueState.Failed)]
    public void GeneratedQueueCancelActivationPreservesLegacyTask(TransferQueueState state) => InLegacyView((window, stores, root) =>
    {
        if (state != TransferQueueState.Pending)
        {
            Pump(stores.Queue.SetStateAsync("queued", TransferQueueState.Running, null, default));
            Pump(stores.Queue.SetStateAsync("queued", state, state == TransferQueueState.Failed ? "fixture failure" : null, default));
        }
        InvokeTask(window, "RefreshQueueAsync");
        var before = JsonSerializer.Serialize(Pump(stores.Queue.ListAsync(default)));
        var list = Assert.IsType<ListView>(window.FindName("TransferList"));
        var row = Assert.IsAssignableFrom<FrameworkElement>(list.ItemTemplate.LoadContent());
        row.DataContext = list.Items.Cast<object>().Single();
        row.Measure(new Size(900, 250)); row.Arrange(new Rect(0, 0, 900, 250)); row.UpdateLayout();
        var cancel = Descendants(row).OfType<Button>().Single(button => Equals(button.Content, UiText.Instance.Get("queue.cancelItem")));
        Assert.Equal(Visibility.Visible, cancel.Visibility);

        // The same routed activation is used by mouse, keyboard and automation. Template controls
        // are created outside the window's logical tree and must be safe even when still enabled.
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(UiText.Instance.Get("vault.viewLegacyOpened"), Assert.IsAssignableFrom<TextBlock>(window.FindName("StatusText")).Text);
        Assert.Equal(before, JsonSerializer.Serialize(Pump(stores.Queue.ListAsync(default))));
    });

    [Fact]
    public void DirectRefreshFolderActionsAndPreparationDoNotMutateLegacyStores() => InLegacyView((window, stores, root) =>
    {
        var manifestsBefore = JsonSerializer.Serialize(Pump(stores.Manifests.ListAsync(default)));
        var queueBefore = JsonSerializer.Serialize(Pump(stores.Queue.ListAsync(default)));
        InvokeTask(window, "RefreshManifestsAsync");
        Assert.Equal(queueBefore, JsonSerializer.Serialize(Pump(stores.Queue.ListAsync(default))));

        var called = false;
        Task<FolderOperationResult> FolderAction() { called = true; throw new InvalidOperationException("injected failure"); }
        InvokeTask(window, "RunFolderOperationAsync", (Func<Task<FolderOperationResult>>)FolderAction);
        Assert.False(called);
        Assert.IsType<InvalidOperationException>(Record.Exception(() => Pump(window.CreateFolderAsync("must-not-be-created"))));
        Assert.Empty(Pump(stores.Folders.ListAsync("42", default)));
        Invoke(window, "FolderRename_Click", new MenuItem { Tag = "historical" }, new RoutedEventArgs());
        Invoke(window, "FolderDelete_Click", new MenuItem { Tag = "historical" }, new RoutedEventArgs());

        var source = Path.Combine(root, "source.bin"); File.WriteAllBytes(source, [1, 2, 3]);
        Write(window, "_chosenFilePath", source); Write(window, "_chosenUploadPaths", new[] { source });
        Invoke(window, "PrepareFile_Click", window.FindName("PrepareFileButton")!, new RoutedEventArgs());
        Assert.False(Directory.Exists(stores.StagingRoot));
        Assert.Equal(manifestsBefore, JsonSerializer.Serialize(Pump(stores.Manifests.ListAsync(default))));
        Assert.Equal(queueBefore, JsonSerializer.Serialize(Pump(stores.Queue.ListAsync(default))));
    });

    [Fact]
    public void UploadDropIsRejectedAndAppLockOverlayRemainsUsable() => InLegacyView((window, stores, root) =>
    {
        var source = Path.Combine(root, "drop.bin"); File.WriteAllBytes(source, [1]);
        var data = new DataObject(DataFormats.FileDrop, new[] { source });
        Assert.False((bool)Invoke(window, "CanAcceptUploadDrop", data)!);
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
        var dropped = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy,
            window.FindName("FilesPage")!, new Point()]);
        dropped.RoutedEvent = DragDrop.DropEvent;
        Invoke(window, "FilesPage_Drop", window.FindName("FilesPage")!, dropped);
        Assert.True(dropped.Handled); Assert.Equal(DragDropEffects.None, dropped.Effects);
        Assert.Equal(string.Empty, typeof(MainWindow).GetField("_chosenFilePath", Private)!.GetValue(window));
        Assert.Empty((string[])typeof(MainWindow).GetField("_chosenUploadPaths", Private)!.GetValue(window)!);

        var overlay = Assert.IsType<Grid>(window.FindName("AppLockOverlay"));
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        content.Measure(new Size(1000, 760)); content.Arrange(new Rect(0, 0, 1000, 760)); content.UpdateLayout();
        Assert.True(Assert.IsType<Button>(window.FindName("AppLockUnlockButton")).IsEnabled);
        Assert.True(Descendants(overlay).OfType<Button>().Single(button => Equals(button.Content, UiText.Instance.Get("appLock.closeApp"))).IsEnabled);
    });

    private static void InLegacyView(Action<MainWindow, VaultProfileStores, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LegacyMutationUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root); UiText.Instance.SetLanguage("en", Path.Combine(root, "language.json"));
                var stores = new VaultProfileStores(TelegramAccountProfileStore.GetDirectory(root, "42"));
                var hash = new string('A', 64);
                foreach (var id in new[] { "queued", "unqueued" })
                    Pump(stores.Manifests.SaveAsync(new(1, id, id + ".bin", 1, hash, 1, [new(0, 0, 1, hash, null, false)], false, "42"), default));
                Pump(stores.Queue.EnsureAsync("queued", "queued.bin", 1, default));
                using var registry = new TelegramVaultRegistry(stores.Root, "42");
                var registered = Pump(registry.RegisterIsolatedPrimaryAsync(new(-101, "42", "Fixture"), default));
                var db = Path.Combine(root, "ui.db"); var manifestStore = new SqliteManifestStore(db);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifestStore, new StagedPartAssembler()),
                    Path.Combine(root, "ui-staging"), manifestStore, new SqliteTransferQueueStore(db),
                    new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);
                Write(window, "_activeTelegramAccountId", "42"); Write(window, "_telegramAccountLoaded", true);
                Write(window, "_storageChannel", new TelegramStorageChannelInfo(-101, "42", "Fixture")); Write(window, "_vaultRegistry", registered);
                InvokeTask(window, "OpenLegacyLocalDataViewAsync", "42");
                check(window, stores, root);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null) { Write(window, "_allowClose", true); window.Close(); }
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Legacy mutation UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Write(MainWindow window, string field, object value) => typeof(MainWindow).GetField(field, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string method, params object[] args) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static void InvokeTask(MainWindow window, string method, params object[] args) => Pump((Task)Invoke(window, method, args)!);
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
