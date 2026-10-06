using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class ExplorerPresentationUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void AllFilesIncludesNestedFoldersWhileFolderSelectionKeepsItsOwnScope(string language)
    {
        Fixture(language, (window, root) =>
        {
            var itemType = typeof(MainWindow).GetNestedType("ManifestItem", BindingFlags.NonPublic)!;
            var items = Array.CreateInstance(itemType, 5);
            var paths = new[] { "", "test", "test/nested", "photos", "test" };
            for (var i = 0; i < paths.Length; i++)
            {
                var manifest = new FileManifest(1, "folder-" + i, $"file-{i}.txt", 1, new string('a', 64), 1, [], false,
                    FolderPath: paths[i], IsHidden: i == 4);
                items.SetValue(Activator.CreateInstance(itemType, Private | BindingFlags.Public, null, [manifest, null, null, false], null), i);
            }
            Set(window, "_manifestItems", items);
            var list = (ListView)window.FindName("ManifestList");
            void SelectFolder(string path) => Call(window, "FolderTree_SelectionChanged", window.FindName("FolderTree"),
                new RoutedPropertyChangedEventArgs<object>(new TreeViewItem(), new TreeViewItem { Tag = path }));
            foreach (var icons in new[] { false, true, false })
            {
                ((RadioButton)window.FindName(icons ? "IconsViewButton" : "DetailsViewButton")).IsChecked = true;
                SelectFolder("test"); Assert.Single(list.Items.Cast<object>());
                SelectFolder(""); Assert.Equal(4, list.Items.Count);
                ((TextBox)window.FindName("SearchBox")).Text = "file-1";
                Assert.Single(list.Items.Cast<object>());
                ((TextBox)window.FindName("SearchBox")).Text = "";
                Assert.Equal(4, list.Items.Count);
            }
        });
    }

    private static byte[] PdfFixture()
    {
        const string content = "0.2 0.5 0.9 rg 25 70 150 160 re f BT /F1 14 Tf 25 250 Td (PDF preview fixture) Tj ET";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var builder = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(builder.Length); builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = builder.Length;
        builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append($"{offset:D10} 00000 n \n");
        builder.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }

    [Theory]
    [InlineData("vi", false, 1200)]
    [InlineData("vi", true, 760)]
    [InlineData("en", false, 760)]
    [InlineData("en", true, 1200)]
    public void IconCardsLoadLocalThumbnailsAndDiscardImagesWhenRecycled(string language, bool dark, int width)
    {
        Fixture(language, (window, root) =>
        {
            Call(window, "ApplyTheme", dark);
            var pixels = new byte[400 * 240 * 4];
            for (var y = 0; y < 240; y++) for (var x = 0; x < 400; x++)
            {
                var offset = (y * 400 + x) * 4;
                pixels[offset] = (byte)(x * 255 / 400);
                pixels[offset + 1] = (byte)(y * 255 / 240);
                pixels[offset + 2] = 80; pixels[offset + 3] = 255;
            }
            var path = Path.Combine(root, "image-part.bin");
            var bitmap = BitmapSource.Create(400, 240, 96, 96, PixelFormats.Bgra32, null, pixels, 400 * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var image = new FileManifest(1, "image", "Landscape.png", bytes.Length, hash, bytes.Length,
                [new PartRecord(0, 0, bytes.Length, hash, null, false, path)], false);
            var remote = image with { FileId = "remote", FileName = "Remote.jpg", Parts = [image.Parts[0] with { StagingPath = null }] };
            var pdf = PdfFixture();
            var pdfPath = Path.Combine(root, "pdf-part.bin"); File.WriteAllBytes(pdfPath, pdf);
            var pdfHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf));
            var document = new FileManifest(1, "document", "Document.pdf", pdf.Length, pdfHash, pdf.Length,
                [new PartRecord(0, 0, pdf.Length, pdfHash, null, false, pdfPath)], false);
            var store = new SqliteManifestStore(Path.Combine(root, "catalog.db"));
            for (var index = 0; index < 48; index++)
            {
                var manifest = index == 1 ? remote : index == 2 ? document : image with { FileId = "image-" + index, FileName = $"Landscape-{index:D2}.png" };
                Task.Run(() => store.SaveAsync(manifest, default)).GetAwaiter().GetResult();
            }
            window.Width = width; window.Height = 760; window.Show(); window.UpdateLayout();
            var list = (ListView)window.FindName("ManifestList");
            WaitFor(() => list.Items.Count == 48);
            ((RadioButton)window.FindName("IconsViewButton")).IsChecked = true;
            window.UpdateLayout();
            var thumbnails = Descendants<ExplorerThumbnail>(list).ToArray();
            var local = thumbnails.First(t => t.Manifest?.FileId.StartsWith("image-", StringComparison.Ordinal) == true);
            WaitFor(() => local.Source is not null);
            Assert.True(((BitmapSource)local.Source).IsFrozen);
            Assert.Equal(192, ((BitmapSource)local.Source).PixelWidth);
            var documentThumb = thumbnails.Single(t => t.Manifest?.FileId == "document");
            WaitFor(() => documentThumb.Source is not null);
            Assert.Equal(192, ((BitmapSource)documentThumb.Source).PixelWidth);
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
            Assert.InRange(thumbnails.Length, 1, 30);
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(47));
            Capture(window, $"image-cards-{language}-{dark}-{width}");
            // Reusing the same image element must clear the preceding file immediately.
            local.Manifest = remote;
            Assert.Null(local.Source);
            local.Manifest = image with { TotalSha256 = new string('0', 64) };
            WaitFor(() => typeof(ExplorerThumbnail).GetField("_request", Private)!.GetValue(local) is null);
            Assert.Null(local.Source);
            local.Manifest = image;
            WaitFor(() => local.Source is not null);
            var midpoint = bytes.Length / 2;
            var firstPath = Path.Combine(root, "first.part"); var lastPath = Path.Combine(root, "last.part");
            File.WriteAllBytes(firstPath, bytes[..midpoint]); File.WriteAllBytes(lastPath, bytes[midpoint..]);
            local.Manifest = image with { Parts = [
                new PartRecord(0, 0, midpoint, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes[..midpoint])), null, false, firstPath),
                new PartRecord(1, midpoint, bytes.Length - midpoint, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes[midpoint..])), null, false, lastPath)] };
            WaitFor(() => local.Source is not null);
            local.Manifest = image with { FileName = "unknown.tscunknownformat" };
            WaitFor(() => typeof(ExplorerThumbnail).GetField("_request", Private)!.GetValue(local) is null);
            Assert.Null(local.Source);
            local.Manifest = image;
            WaitFor(() => local.Source is not null);
            ((RadioButton)window.FindName("DetailsViewButton")).IsChecked = true;
            window.UpdateLayout();
            WaitFor(() => local.Source is null);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            using var unlocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    private static void WaitFor(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
            (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Assert.True(condition(), "Thumbnail fixture timed out.");
    }

    [Theory]
    [InlineData("vi", false, 760)]
    [InlineData("vi", true, 1200)]
    [InlineData("en", false, 1200)]
    [InlineData("en", true, 760)]
    public void CloseUploadOptionsClearsSourcesAndReopensWithoutChangingQueueOrSourceFiles(string language, bool dark, int width)
    {
        Fixture(language, (window, root) =>
        {
            Call(window, "ApplyTheme", dark);
            var source = Path.Combine(root, "fixture.txt");
            File.WriteAllText(source, "isolated source");
            var queue = new SqliteTransferQueueStore(Path.Combine(root, "catalog.db"));
            Task.Run(async () =>
            {
                await queue.EnsureAsync("pending-fixture", "pending.txt", 1, default);
                await queue.EnsureAsync("completed-fixture", "completed.txt", 1, default);
                await queue.SetStateAsync("completed-fixture", TransferQueueState.Running, null, default);
                await queue.SetStateAsync("completed-fixture", TransferQueueState.Completed, null, default);
            }).GetAwaiter().GetResult();
            var before = Task.Run(() => queue.ListAsync(default)).GetAwaiter().GetResult();
            Call(window, "SetUploadSelection", new object[] { new[] { source } });
            var toolbar = (Border)window.FindName("UploadToolbar");
            var close = (Button)window.FindName("CloseUploadSelectionButton");
            Assert.Equal(Visibility.Visible, toolbar.Visibility);
            Assert.Equal(UiText.Instance.Get("upload.closeSelection"), close.Content);
            Assert.Equal(close.Content, AutomationProperties.GetName(close));
            Assert.Equal(UiText.Instance.Get("upload.closeSelectionTip"), close.ToolTip);
            window.Width = width; window.Height = 760;
            window.Show(); window.UpdateLayout();
            Assert.True(close.IsEnabled);
            Capture(window, $"upload-close-{language}-{dark}-{width}");
            Call(window, "SetBusy", true, null, true, "status.cancel");
            Assert.False(close.IsEnabled);
            Call(window, "CloseUploadSelection_Click", close, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, toolbar.Visibility);
            Call(window, "SetBusy", false, null, true, "status.cancel");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, toolbar.Visibility);
            Assert.Empty((string[])typeof(MainWindow).GetField("_chosenUploadPaths", Private)!.GetValue(window)!);
            Assert.Equal(string.Empty, typeof(MainWindow).GetField("_chosenFilePath", Private)!.GetValue(window));
            Assert.Null(((TextBlock)window.FindName("ChosenFileName")).ToolTip);
            Assert.Equal("isolated source", File.ReadAllText(source));
            Assert.Equal(before, Task.Run(() => queue.ListAsync(default)).GetAwaiter().GetResult());
            Call(window, "SetUploadSelection", new object[] { new[] { source } });
            Assert.Equal(Visibility.Visible, toolbar.Visibility);
            Assert.True(close.IsEnabled);
        });
    }

    [Theory]
    [InlineData("vi", false)]
    [InlineData("vi", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public void ConnectClickExplainsBlockedStatesAndKeepsFeedbackNearTheButton(string language, bool dark)
    {
        Fixture(language, (window, root) =>
        {
            Call(window, "ApplyTheme", dark);
            var feedback = (TextBlock)window.FindName("StorageConnectionFeedback");
            void Blocked(string key)
            {
                // All these cases return before any Telegram call or profile access.
                var task = (Task)typeof(MainWindow).GetMethod("ConnectPrivateStorageAsync", Private)!.Invoke(window, new object[] { true })!;
                Assert.True(task.IsCompletedSuccessfully);
                Assert.Equal(UiText.Instance.Get(key), feedback.Text);
                Assert.Equal(feedback.Text, ((TextBlock)window.FindName("StatusText")).Text);
                Assert.Equal(Visibility.Visible, feedback.Visibility);
            }
            Blocked("storage.connection.signIn");
            var session = (TelegramAuthSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TelegramAuthSession));
            typeof(TelegramAuthSession).GetField("_currentState", Private)!.SetValue(session, "authorizationStateReady");
            Set(window, "_telegramSession", session);
            try
            {
                Blocked("storage.connection.profileLoading");
                Set(window, "_vaultProfileSetupFailed", true);
                Blocked("vault.profileUnavailable");
                Set(window, "_storageConnectionInProgress", true);
                Blocked("storage.connection.progress");
                Set(window, "_storageConnectionInProgress", false);
                Set(window, "_operationBusy", true);
                Blocked("storage.connection.busy");
                Set(window, "_operationBusy", false);
                var error = UiText.Instance.LocalizeMessage("Storage channel setup failed: " + UiText.Instance.Get("vault.legacyNeedsIsolation"));
                Call(window, "ShowStorageConnectionFeedback", error);
                window.Width = 760; window.Height = 650;
                window.Show(); window.UpdateLayout();
                Assert.Equal(TextWrapping.Wrap, feedback.TextWrapping);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(feedback));
                Assert.True(feedback.ActualHeight > 20);
                Assert.True(feedback.ActualWidth <= 260);
                Capture(window, $"connection-feedback-{language}-{dark}");
                Call(window, "ShowStorageConnectionFeedback", new object?[] { null });
                Assert.Equal(Visibility.Collapsed, feedback.Visibility);
                Assert.Empty(feedback.Text);
            }
            finally
            {
                Set(window, "_telegramSession", null);
                Set(window, "_operationBusy", false);
                Set(window, "_storageConnectionInProgress", false);
            }
        });
    }

    [Fact]
    public void ExplicitConnectUsesSelectedVaultOnlyForTheCurrentAccount()
    {
        Fixture("vi", (window, root) =>
        {
            Set(window, "_activeTelegramAccountId", "fixture");
            var first = new TelegramStorageChannelInfo(-101, "fixture", "First");
            var selected = new TelegramStorageChannelInfo(-102, "fixture", "Selected");
            Call(window, "UpdateVaultPickerPresentation", new[] { first, selected }, selected.ChatId, true);
            object? Target(bool explicitClick) => typeof(MainWindow).GetMethod("GetSelectedStorageConnectionChatId", Private)!.Invoke(window, new object[] { explicitClick });
            Assert.Equal(selected.ChatId, Target(true));
            Assert.Null(Target(false)); // Automatic restoration continues using the persisted active vault.
            Set(window, "_activeTelegramAccountId", "another-account");
            Assert.Null(Target(true));
            ((ComboBox)window.FindName("VaultPicker")).SelectedItem = null;
            Assert.Null(Target(true));
        });
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void FailedAccountProfileKeepsVaultOptionsVisibleButDisabled(string language)
    {
        Fixture(language, (window, root) =>
        {
            // Identity-only state fixture: never start or dispose a native Telegram session.
            var session = (TelegramAuthSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TelegramAuthSession));
            var stateField = typeof(TelegramAuthSession).GetField("_currentState", Private)!;
            stateField.SetValue(session, "authorizationStateReady");
            Set(window, "_telegramSession", session);
            try
            {
            Set(window, "_vaultProfileSetupFailed", true);
            Set(window, "_telegramAccountLoaded", false);
            Call(window, "ApplyVaultActionAvailability");
            Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("VaultActions")).Visibility);
            var state = (TextBlock)window.FindName("VaultPickerState");
            Assert.Equal(Visibility.Visible, state.Visibility);
            Assert.Equal(UiText.Instance.Get("vault.profileUnavailable"), state.Text);
            Assert.False(((ComboBox)window.FindName("VaultPicker")).IsEnabled);
            foreach (var name in new[] { "SwitchVaultButton", "AddVaultButton", "CreateVaultButton", "DiscoverVaultsButton" })
                Assert.False(((Button)window.FindName(name)).IsEnabled);
            foreach (var authState in new[] { "authorizationStateWaitPhoneNumber", "authorizationStateWaitCode", "authorizationStateWaitPassword", "authorizationStateWaitOtherDeviceConfirmation" })
            {
                stateField.SetValue(session, authState);
                Call(window, "ApplyVaultActionAvailability");
                Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("VaultActions")).Visibility);
            }
            Set(window, "_telegramSession", null);
            Set(window, "_vaultProfileSetupFailed", false);
            Call(window, "ApplyVaultActionAvailability");
            Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("VaultActions")).Visibility);
            }
            finally { Set(window, "_telegramSession", null); }
        });
    }

    [Theory]
    [InlineData("vi", false, 1920, 1000)]
    [InlineData("vi", true, 760, 650)]
    [InlineData("en", false, 760, 650)]
    [InlineData("en", true, 1000, 760)]
    public void VaultChoicesEmptyErrorAndPresentationShareTheActualState(string language, bool dark, int width, int height)
    {
        Fixture(language, (window, root) =>
        {
            window.Width = width; window.Height = height;
            Call(window, "ApplyTheme", dark);
            var actions = (StackPanel)window.FindName("VaultActions"); actions.Visibility = Visibility.Visible;
            var picker = (ComboBox)window.FindName("VaultPicker");
            var state = (TextBlock)window.FindName("VaultPickerState");
            var channel = new TelegramStorageChannelInfo(-101, "fixture", "Kho fixture / Fixture vault");
            Call(window, "UpdateVaultPickerPresentation", Array.Empty<TelegramStorageChannelInfo>(), null, true);
            window.Show(); window.UpdateLayout();
            actions.Visibility = Visibility.Visible; window.UpdateLayout();
            Assert.False(picker.IsEnabled);
            Assert.Empty(picker.Items);
            Assert.Equal(Visibility.Visible, state.Visibility);
            Assert.Equal(UiText.Instance.Get("vault.empty.help"), state.Text);
            Assert.Equal(Visibility.Visible, ((TextBlock)picker.Template.FindName("EmptyChoice", picker)).Visibility);
            Assert.Equal(UiText.Instance.Get("vault.heading"), AutomationProperties.GetName(picker));
            Set(window, "_pendingLegacyIsolationChannel", channel);
            Call(window, "UpdateVaultPickerPresentation", Array.Empty<TelegramStorageChannelInfo>(), null, true);
            Assert.Equal(UiText.Instance.Get("vault.empty.catalogError"), state.Text);
            Assert.Equal(state.Text, AutomationProperties.GetHelpText(picker));
            Capture(window, $"vault-error-{language}-{dark}-{width}");
            Set(window, "_pendingLegacyIsolationChannel", null);
            Set(window, "_storageChannel", channel);
            Call(window, "UpdateVaultPickerPresentation", new[] { channel }, channel.ChatId, true);
            Assert.True(picker.IsEnabled);
            Assert.Single(picker.Items);
            var choice = picker.Items[0];
            Assert.Equal(channel.DisplayLabel, choice.GetType().GetProperty("DisplayLabel")!.GetValue(choice));
            Assert.Equal(UiText.Instance.Get("vault.choice.active"), choice.GetType().GetProperty("StatusLabel")!.GetValue(choice));
            Assert.Equal(Visibility.Collapsed, state.Visibility);
            Keyboard.Focus(picker);
            Assert.True(picker.IsKeyboardFocusWithin);
            picker.IsDropDownOpen = true; window.UpdateLayout();
            var popup = (System.Windows.Controls.Primitives.Popup)picker.Template.FindName("PART_Popup", picker);
            ((FrameworkElement)popup.Child).UpdateLayout();
            var item = (ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.NotNull(item.ContentTemplate);
            Assert.Contains(Descendants<TextBlock>(item), text => text.Text == UiText.Instance.Get("vault.choice.active"));
            Assert.Equal(channel.DisplayLabel, AutomationProperties.GetName(item));
            Assert.Equal(UiText.Instance.Get("vault.choice.active"), AutomationProperties.GetHelpText(item));
            Assert.Equal(((SolidColorBrush)window.Resources["TextPrimary"]).Color, ((SolidColorBrush)item.Foreground).Color);
            Assert.True(Contrast((SolidColorBrush)item.Foreground, (SolidColorBrush)item.Background) >= 4.5);
            item.IsSelected = true; item.Focus(); window.UpdateLayout();
            var border = (Border)item.Template.FindName("ChoiceBorder", item);
            Assert.Equal(((SolidColorBrush)window.Resources["AccentBlue"]).Color, ((SolidColorBrush)border.BorderBrush).Color);
            Capture((FrameworkElement)popup.Child, $"vault-popup-{language}-{dark}-{width}");
            Assert.True(Contrast((SolidColorBrush)item.Foreground, (SolidColorBrush)item.Background) >= 4.5);
            item.IsEnabled = false; window.UpdateLayout();
            Assert.True(Contrast((SolidColorBrush)item.Foreground, (SolidColorBrush)item.Background) >= 4.5);
            item.IsEnabled = true;
            picker.IsDropDownOpen = false;
            Assert.Same(choice, picker.SelectedItem);
            Call(window, "UpdateVaultPickerPresentation", new[] { channel }, channel.ChatId, false);
            Assert.False(picker.IsEnabled);
            Assert.Same(choice.GetType(), picker.SelectedItem.GetType());
            Capture(window, $"vault-closed-{language}-{dark}-{width}");
            Assert.False(File.Exists(Path.Combine(root, "telegram-vaults.json")));
        });
    }

    [Theory]
    [InlineData("vi", false, 1920, 1000)]
    [InlineData("en", false, 760, 650)]
    [InlineData("vi", true, 760, 650)]
    [InlineData("en", true, 1000, 760)]
    public void ViewsPreserveResultsFiltersSortMultiSelectionActionsAndRecycleTenThousandItems(string language, bool dark, int width, int height)
    {
        Fixture(language, (window, root) =>
        {
            window.Width = width; window.Height = height;
            Call(window, "ApplyTheme", dark);
            window.Show(); window.UpdateLayout();
            var itemType = typeof(MainWindow).GetNestedType("ManifestItem", BindingFlags.NonPublic)!;
            var items = Array.CreateInstance(itemType, 10_000);
            for (var index = 0; index < items.Length; index++)
            {
                var manifest = new FileManifest(1, "fixture-" + index, $"document-{index:D5}.txt", index + 1, new string('a', 64), 1, [], false);
                items.SetValue(Activator.CreateInstance(itemType, Private | BindingFlags.Public, null, [manifest, null, null, false], null), index);
            }
            Set(window, "_manifestItems", items);
            Call(window, "ApplyManifestFilterAndSort"); window.UpdateLayout();
            var list = (ListView)window.FindName("ManifestList");
            var details = (RadioButton)window.FindName("DetailsViewButton");
            var icons = (RadioButton)window.FindName("IconsViewButton");
            Assert.Equal(UiText.Instance.Get("files.view.iconsTip"), AutomationProperties.GetName(icons));
            Assert.Equal(UiText.Instance.Get("files.view.detailsTip"), AutomationProperties.GetName(details));
            foreach (var mode in new[] { true, false, true })
            {
                (mode ? icons : details).IsChecked = true; window.UpdateLayout();
                Assert.Equal(10_000, list.Items.Count);
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
                Assert.InRange(Realized(list), 1, 150);
                Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(9_999));
                list.ScrollIntoView(list.Items[9_999]); window.UpdateLayout();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(9_999));
                Assert.InRange(Realized(list), 1, 150);
                list.ScrollIntoView(list.Items[0]); window.UpdateLayout();
                list.SelectedIndex = 0;
                window.UpdateLayout();
                var first = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.NotNull(PresentationSource.FromVisual(first));
                Keyboard.Focus(first);
                list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, mode ? Key.Right : Key.Down)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                window.UpdateLayout();
                Assert.Equal(1, list.SelectedIndex);
                list.SelectedItems.Clear();
            }
            ((TextBox)window.FindName("SearchBox")).Text = "document-000";
            ((ComboBox)window.FindName("TypeFilterBox")).SelectedIndex = 4; // Documents includes txt.
            ((ComboBox)window.FindName("FilterBox")).SelectedIndex = 3; // No local cache.
            ((ComboBox)window.FindName("SortBox")).SelectedIndex = 2;
            window.UpdateLayout();
            Assert.Equal(100, list.Items.Count);
            var result = list.ItemsSource;
            var selected = new[] { list.Items[0], list.Items[1] };
            foreach (var item in selected) list.SelectedItems.Add(item);
            Call(window, "ApplyActionAvailability");
            var actionNames = new[] { "PreviewButton", "VerifyButton", "RemoveLocalEntryButton", "CreateFolderButton" };
            var availability = actionNames.Select(name => ((Button)window.FindName(name)).IsEnabled).ToArray();
            foreach (var mode in new[] { false, true, false })
            {
                (mode ? icons : details).IsChecked = true; window.UpdateLayout();
                Assert.Same(result, list.ItemsSource);
                Assert.Equal(selected, list.SelectedItems.Cast<object>());
                Assert.Equal(availability, actionNames.Select(name => ((Button)window.FindName(name)).IsEnabled));
                Assert.Equal("document-00099.txt", list.Items[0].GetType().GetProperty("FileName")!.GetValue(list.Items[0]));
                Assert.Equal(mode, new UiPreferencesStore(Path.Combine(root, "ui-preferences.json")).Load().ExplorerIcons);
                Capture(window, $"explorer-{(mode ? "icons" : "details")}-{language}-{width}");
                var scroll = Descendants<ScrollViewer>(list).First();
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "Explorer should fit the available width.");
            }
            // Source reset after scrolling must discard stale generator positions.
            ((TextBox)window.FindName("SearchBox")).Text = "no-such-file";
            icons.IsChecked = true; window.UpdateLayout();
            Assert.Empty(list.Items);
            ((TextBox)window.FindName("SearchBox")).Text = "document-000"; window.UpdateLayout();
            Assert.Equal(100, list.Items.Count);
            Assert.InRange(Realized(list), 1, 150);
            foreach (var page in new[] { "settings", "transfers", "trash", "files" })
            {
                Call(window, "NavigateToPage", page); window.UpdateLayout();
                var expected = page == "files" ? Visibility.Visible : Visibility.Collapsed;
                Assert.Equal(expected, details.Visibility);
                Assert.Equal(expected, icons.Visibility);
                Assert.True(icons.IsChecked); // Navigation retains the chosen Explorer view.
            }
        });
    }

    [Fact]
    public void ExistingUiPreferencesDefaultToDetailsAndRoundTripOtherSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.UiPreferenceFixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "ui-preferences.json");
            File.WriteAllText(path, "{\"KeepLogin\":true,\"DarkMode\":true}");
            var store = new UiPreferencesStore(path);
            Assert.Equal((true, true, false), store.Load());
            store.Save(true, true, true);
            Assert.Equal((true, true, true), store.Load());
            File.WriteAllText(path, "broken");
            Assert.Equal((false, false, false), store.Load());
        }
        finally { Directory.Delete(root, true); }
    }

    private static int Realized(ListView list) => Enumerable.Range(0, list.Items.Count).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static void Call(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);

    private static void Fixture(string language, Action<MainWindow, string> run)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.PresentationFixture", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage(language, Path.Combine(root, "ui-language.json"));
                var db = Path.Combine(root, "catalog.db");
                var manifests = new SqliteManifestStore(db);
                Task.Run(() => manifests.ListAsync(default)).GetAwaiter().GetResult();
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(db), new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root)
                    { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
                run(window, root);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false;
                    window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame);
                }
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Isolated UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static double Contrast(SolidColorBrush foreground, SolidColorBrush background)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte b) { var x = b / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var a = Luminance(foreground.Color); var b = Luminance(background.Color);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void Capture(FrameworkElement element, string name)
    {
        var path = Environment.GetEnvironmentVariable("TSC_UI_REFINEMENT_CAPTURE");
        if (string.IsNullOrEmpty(path)) return;
        if (element is Window window) element = (FrameworkElement)window.Content;
        element.UpdateLayout(); Directory.CreateDirectory(path);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(path, name + ".png")); encoder.Save(output);
    }
}
