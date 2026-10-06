using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace TeleSelfCloud.Desktop;

/// <summary>Startup-only progress; stopping never releases the profile lease before work finishes.</summary>
public sealed class LocalDatabaseStartupWindow : Window, IProgress<LocalDatabaseStartupProgress>
{
    private readonly string sharedRoot;
    private readonly CancellationTokenSource stop;
    private readonly bool showOnProgress;
    private bool completed, stopRequested;
    public TextBlock ScopeText { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 8) };
    public TextBlock StageText { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) };
    public Button StopButton { get; } = new() { MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0), IsCancel = true };
    public LocalDatabaseStartupWindow(string root, CancellationTokenSource stop, bool showOnProgress = false)
    {
        sharedRoot = Path.GetFullPath(root); this.stop = stop; this.showOnProgress = showOnProgress;
        Title = UiText.Instance.Get("localdb.startupTitle"); Width = 540; MinWidth = 400; MaxHeight = 650;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = UiText.Instance.Get("localdb.startupIntro"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(ScopeText); panel.Children.Add(StageText);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6 });
        StopButton.Content = UiText.Instance.Get("localdb.stopStartup");
        StopButton.Click += (_, _) => RequestStop(); panel.Children.Add(StopButton);
        AutomationProperties.SetLiveSetting(StageText, AutomationLiveSetting.Polite);
        Content = new ScrollViewer { Content = panel, Background = SystemColors.WindowBrush, Foreground = SystemColors.WindowTextBrush, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closing += (_, args) => { if (!completed) { args.Cancel = true; RequestStop(); } };
    }
    public void Report(LocalDatabaseStartupProgress value)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => Report(value)); return; }
        if (completed) return;
        if (showOnProgress && !IsVisible) Show();
        var relative = Path.GetRelativePath(sharedRoot, value.Root);
        ScopeText.Text = value.CatalogCount == 0 ? UiText.Instance.Get("localdb.inventoryScope") :
            string.Format(UiText.Instance.Get("localdb.catalogCounter"), value.CatalogIndex, value.CatalogCount,
                relative == "." ? UiText.Instance.Get("localdb.shared") : relative);
        if (!stopRequested)
        {
            var known = value.Stage is "CacheMigrating" or "Inventory" or "Checking" or "Recovery" or "Inspecting" or "Prepared" or "Copying" or "Encrypting" or "Verifying" or "Verified" or "Switching" or "Switched" or "ProtectingOriginals" or "Ready";
            StageText.Text = UiText.Instance.Get("localdb.stage." + (known ? value.Stage : "Checking"));
            StopButton.Content = UiText.Instance.Get(value.CanCancel ? "localdb.stopStartup" : "localdb.stopAfterCurrent");
        }
    }
    private void RequestStop()
    {
        if (completed || stopRequested) return;
        stopRequested = true; StopButton.IsEnabled = false; StageText.Text = UiText.Instance.Get("localdb.stopping"); stop.Cancel();
    }
    public void CompleteAndClose() { completed = true; Close(); }
}
