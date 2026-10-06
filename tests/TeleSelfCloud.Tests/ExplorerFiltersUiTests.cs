using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;
using Xunit.Abstractions;

namespace TeleSelfCloud.Tests;

public sealed class ExplorerFiltersUiTests
{
    private readonly ITestOutputHelper output;
    public ExplorerFiltersUiTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void TenThousandExplorerItemsUseRecyclingVirtualization()
    {
        Exception? failure = null;
        var elapsed = TimeSpan.Zero;
        var realized = 0;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ExplorerLargeList", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage("en", Path.Combine(root, "language.json"));
                var db = Path.Combine(root, "catalog.db");
                var manifests = new SqliteManifestStore(db);
                Task.Run(() => manifests.ListAsync(default)).GetAwaiter().GetResult();
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(db),
                    new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);

                var itemType = typeof(MainWindow).GetNestedType("ManifestItem", BindingFlags.NonPublic)!;
                var items = Array.CreateInstance(itemType, 10_000);
                for (var index = 0; index < items.Length; index++)
                {
                    var name = $"large-{index:D5}.txt";
                    var manifest = new FileManifest(1, "large-" + index, name, index, new string('a', 64), 1, Array.Empty<PartRecord>(), true);
                    items.SetValue(Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null, args: [manifest, null, null, false], culture: null), index);
                }
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                window.WindowStyle = WindowStyle.None; window.ShowInTaskbar = false; window.ShowActivated = false; window.Opacity = 0;
                window.Show(); window.UpdateLayout(); content.UpdateLayout();
                typeof(MainWindow).GetField("_manifestItems", flags)!.SetValue(window, items);
                var timer = Stopwatch.StartNew();
                typeof(MainWindow).GetMethod("ApplyManifestFilterAndSort", flags)!.Invoke(window, null);
                window.UpdateLayout(); content.UpdateLayout();
                timer.Stop(); elapsed = timer.Elapsed;

                var list = Assert.IsType<ListView>(window.FindName("ManifestList"));
                Assert.Equal(10_000, list.Items.Count);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(9_999));
                realized = Enumerable.Range(0, list.Items.Count).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                Assert.InRange(realized, 1, 100);

                list.ScrollIntoView(list.Items[9_999]); list.UpdateLayout(); content.UpdateLayout();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(9_999));
                var scroll = FindVisualChild<ScrollViewer>(list);
                Assert.True(scroll.VerticalOffset > 0, $"The last item did not move into view (offset {scroll.VerticalOffset}, extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight}).");
                realized = Enumerable.Range(0, list.Items.Count).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                Assert.InRange(realized, 1, 100);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null)
                {
                    typeof(MainWindow).GetField("_shutdownStarted", flags)!.SetValue(window, false);
                    var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame);
                }
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "10,000-item Explorer fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"Filtering and laying out 10,000 local fixture rows took {elapsed}.");
        Assert.InRange(realized, 1, 100);
        output.WriteLine($"10,000 rows: filter + layout {elapsed.TotalMilliseconds:F1} ms; realized containers after scroll: {realized}.");
    }

    [Theory]
    [InlineData("vi", 760, 650)]
    [InlineData("en", 1000, 760)]
    public void FileTypeAndAvailabilityFiltersWorkAndRemainAccessible(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ExplorerFilters", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                var db = Path.Combine(root, "catalog.db");
                var manifests = new SqliteManifestStore(db);
                Task.Run(() => manifests.ListAsync(default)).GetAwaiter().GetResult();
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(db),
                    new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);

                var names = new[] { "report.PDF", "clip.MP4", "voice.opus", "photo.JPEG", "source.CS", "opaque.custom" };
                var itemsType = typeof(MainWindow).GetNestedType("ManifestItem", BindingFlags.NonPublic)!;
                var items = Array.CreateInstance(itemsType, names.Length);
                for (var index = 0; index < names.Length; index++)
                {
                    var manifest = new FileManifest(1, "filter-" + index, names[index], index + 1,
                        new string('a', 64), index + 1, [new PartRecord(0, 0, index + 1, new string('a', 64), "-100/1", true)], true);
                    items.SetValue(Activator.CreateInstance(itemsType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null, args: [manifest, null, null, false], culture: null), index);
                }
                typeof(MainWindow).GetField("_manifestItems", flags)!.SetValue(window, items);
                typeof(MainWindow).GetMethod("ApplyManifestFilterAndSort", flags)!.Invoke(window, null);

                var typeFilter = Assert.IsType<ComboBox>(window.FindName("TypeFilterBox"));
                var availability = Assert.IsType<ComboBox>(window.FindName("FilterBox"));
                var sort = Assert.IsType<ComboBox>(window.FindName("SortBox"));
                Assert.Equal(8, typeFilter.Items.Count);
                Assert.Equal(6, availability.Items.Count);
                Assert.Equal(6, sort.Items.Count);
                Assert.Equal(UiText.Instance.Get("files.type"), AutomationProperties.GetName(typeFilter));
                Assert.Equal(UiText.Instance.Get("files.filter"), AutomationProperties.GetName(availability));

                typeFilter.SelectedIndex = 1;
                Assert.Equal(new[] { "photo.JPEG" }, VisibleNames(window));
                typeFilter.SelectedIndex = 0;
                availability.SelectedIndex = 2;
                Assert.Empty(VisibleNames(window)); // No offline part cache in these fixtures.
                availability.SelectedIndex = 0;
                sort.SelectedIndex = 2;
                Assert.Equal(names.OrderByDescending(name => Array.IndexOf(names, name) + 1), VisibleNames(window));
                var typeLabel = Assert.IsType<ComboBoxItem>(typeFilter.Items[4]).Content.ToString();
                Assert.Equal(UiText.Instance.Get("files.type.documents"), typeLabel);

                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                var toolbar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("FilesTopControls"));
                Assert.True(toolbar.ActualHeight >= 32);
                foreach (var control in new FrameworkElement[] { typeFilter, availability, sort })
                {
                    Assert.True(control.ActualWidth > 0 && control.ActualHeight >= 28);
                    var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= width + 1 && bounds.Top >= 0 && bounds.Bottom <= height + 1, $"Explorer filter is clipped: {bounds}.");
                }

                var capture = Environment.GetEnvironmentVariable("TSC_EXPLORER_FILTER_CAPTURE");
                if (!string.IsNullOrWhiteSpace(capture))
                {
                    Directory.CreateDirectory(capture);
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(toolbar), null, new Rect(0, 0, toolbar.ActualWidth, toolbar.ActualHeight));
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(toolbar.ActualWidth), (int)Math.Ceiling(toolbar.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(capture, $"explorer-filters-{language}.png")); encoder.Save(output);
                }
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null)
                {
                    typeof(MainWindow).GetField("_shutdownStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                    var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame);
                }
                dispatcher.InvokeShutdown();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Explorer filter UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string[] VisibleNames(MainWindow window) => Assert.IsAssignableFrom<System.Collections.IEnumerable>(
        Assert.IsType<ListView>(window.FindName("ManifestList")).ItemsSource).Cast<object>()
        .Select(item => (string)item.GetType().GetProperty("FileName")!.GetValue(item)!).ToArray();

    private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try { return FindVisualChild<T>(VisualTreeHelper.GetChild(root, index)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"The visual tree does not contain {typeof(T).Name}.");
    }
}
