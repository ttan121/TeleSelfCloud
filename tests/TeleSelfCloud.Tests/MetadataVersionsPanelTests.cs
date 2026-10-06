using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class MetadataVersionsPanelTests
{
    [Theory]
    [InlineData("en", 1000, 760)]
    [InlineData("vi", 760, 650)]
    public void RealPanelListsOrganizationAndRestrictsRetryToPreparedChoice(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MetadataPanel", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            MetadataVersionsPanel? panel = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                var database = Path.Combine(root, "manifest.db");
                var manifests = new SqliteManifestStore(database);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(database),
                    new SqliteRemoteSyncCheckpointStore(database), new SqliteLocalFolderStore(database), root);
                var review = Assert.IsType<Button>(window.FindName("ReviewMetadataButton"));
                Assert.False(review.IsEnabled);
                panel = Assert.IsType<MetadataVersionsPanel>(window.FindName("MetadataHistoryPanel"));
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var first = new FileManifest(1, "file", "Báo cáo – tài liệu dự án và kết quả kiểm tra.bin", 3, new string('A', 64), 3,
                    [new PartRecord(0, 0, 3, new string('A', 64), "-100500/20", true)], true, "account", Revision: 2,
                    UpdatedAtUtc: DateTimeOffset.Parse("2026-10-05T00:00:00Z"));
                var second = first with { FolderPath = "Tài liệu/Báo cáo tổng hợp", IsFavorite = true, IsHidden = true };
                var selected = ManifestRevisionSelector.PortableFingerprint(second);
                var pending = new MetadataResolutionPlan(first, second with { Revision = 3 }, selected);
                panel.ShowHistory(new(1, "account", -100500, "file", [first, second], pending), first);
                panel.SetCanApply(true);
                Assert.Equal(selected, panel.SelectedFingerprint);
                Assert.True(panel.ApplyVersionButton.IsEnabled);
                Assert.Equal(UiText.Instance.Get("metadata.pending"), panel.HistoryStatus.Text);
                Assert.Equal(2, panel.VersionsList.Items.Count);
                var rows = panel.VersionsList.Items.Cast<MetadataVersionsPanel.VersionRow>().ToArray();
                Assert.Contains(UiText.Instance.Get("metadata.hidden"), rows.Single(row => row.Fingerprint == selected).Organization);
                Assert.Contains("Tài liệu/Báo cáo tổng hợp", rows.Single(row => row.Fingerprint == selected).AccessibleSummary);
                panel.VersionsList.SelectedItem = rows.Single(row => row.Fingerprint != selected);
                Assert.False(panel.ApplyVersionButton.IsEnabled);
                Assert.True(panel.PrepareVersionButton.IsEnabled);
                panel.VersionsList.SelectedItem = rows.Single(row => row.Fingerprint == selected);
                var applies = 0;
                panel.ApplyRequested += (_, _) => applies++;
                panel.ApplyVersionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, applies);
                Assert.False(panel.PrepareVersionButton.IsEnabled);
                panel.SetCanApply(false);
                panel.ApplyVersionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, applies);
                panel.Visibility = Visibility.Visible;
                Assert.IsType<Border>(window.FindName("FileDetailPanel")).Visibility = Visibility.Visible;
                Assert.IsType<StackPanel>(window.FindName("FilesEmptyState")).Visibility = Visibility.Collapsed;
                Assert.IsType<TextBlock>(window.FindName("DetailFileName")).Text = first.FileName;
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                Assert.True(panel.ActualHeight <= 280);
                var position = panel.TransformToAncestor(content).Transform(new Point());
                Assert.True(position.Y + panel.ActualHeight <= height, "Metadata review extends below the minimum window.");
                var capture = Environment.GetEnvironmentVariable("TSC_METADATA_CAPTURE_DIRECTORY");
                if (!string.IsNullOrEmpty(capture))
                {
                    Directory.CreateDirectory(capture);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(capture, $"metadata-panel-{language}-{width}x{height}.png"));
                    encoder.Save(output);
                }
                panel.ShowHistory(null, first);
                Assert.Empty(panel.VersionsList.Items);
                Assert.False(panel.ApplyVersionButton.IsEnabled);
                Assert.Equal(UiText.Instance.Get("metadata.empty"), panel.HistoryStatus.Text);
                Assert.Equal(UiText.Instance.Get("metadata.apply"), AutomationProperties.GetName(panel.ApplyVersionButton));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                panel?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                if (window is not null)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    window.Closed += (_, _) => frame.Continue = false;
                    window.Close();
                    if (frame.Continue) System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                dispatcher.InvokeShutdown();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Metadata panel fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
