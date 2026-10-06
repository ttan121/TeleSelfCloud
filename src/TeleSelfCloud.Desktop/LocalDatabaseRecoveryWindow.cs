using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public sealed class LocalDatabaseRecoveryWindow : Window
{
    private bool busy;
    private readonly TextBox source = new() { IsReadOnly = true, MinHeight = 32 };
    private readonly PasswordBox password = new() { MinHeight = 32 };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    public LocalDatabaseRecoveryWindow(string root, LocalProfileLease lease, Func<bool>? scopeIsCurrent = null)
    {
        Title = UiText.Instance.Get("localdb.recover"); Width = 540; SizeToContent = SizeToContent.Height;
        Closing += (_, args) => { if (busy) args.Cancel = true; };
        MinWidth = 400; MaxHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = UiText.Instance.Get("localdb.recoveryInstructions"), TextWrapping = TextWrapping.Wrap });
        var relative = Path.GetRelativePath(lease.Root, root);
        panel.Children.Add(new TextBlock { Text = relative == "." ? UiText.Instance.Get("localdb.shared") : relative, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 16) });
        var choose = new Button { Content = UiText.Instance.Get("localdb.chooseBackup"), MinHeight = 32, Margin = new(0, 0, 0, 8) };
        choose.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "TeleSelfCloud database key (*.tsc-db-key.json)|*.tsc-db-key.json|JSON (*.json)|*.json" }; if (dialog.ShowDialog(this) == true) source.Text = dialog.FileName; };
        panel.Children.Add(choose); panel.Children.Add(source);
        panel.Children.Add(new TextBlock { Text = UiText.Instance.Get("metadata.crypto.passphrase"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 8) });
        AutomationProperties.SetName(source, UiText.Instance.Get("localdb.chooseBackup"));
        AutomationProperties.SetName(password, UiText.Instance.Get("metadata.crypto.passphrase"));
        panel.Children.Add(password); panel.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        var restore = new Button { Content = UiText.Instance.Get("localdb.recover"), IsDefault = true, IsEnabled = false, MinHeight = 32, Margin = new(0, 0, 12, 0) };
        void Refresh() => restore.IsEnabled = source.Text.Length > 0 && password.Password.Length >= 12;
        source.TextChanged += (_, _) => Refresh(); password.PasswordChanged += (_, _) => Refresh();
        restore.Click += async (_, _) =>
        {
            if (scopeIsCurrent is not null && !scopeIsCurrent()) { error.Text = UiText.Instance.Get("localdb.scopeChanged"); return; }
            busy = true; restore.IsEnabled = choose.IsEnabled = password.IsEnabled = false; var passphrase = password.Password; var backupPath = source.Text; password.Clear();
            try { await Task.Run(() => LocalDatabaseProtection.Recover(root, backupPath, passphrase, lease)); busy = false; DialogResult = true; }
            catch (Exception) { error.Text = UiText.Instance.Get("localdb.recoveryFailed"); }
            finally { busy = false; choose.IsEnabled = password.IsEnabled = true; Refresh(); }
        };
        actions.Children.Add(restore); actions.Children.Add(new Button { Content = UiText.Instance.Get("dialog.cancel"), IsCancel = true, MinHeight = 32 });
        panel.Children.Add(actions); Content = new ScrollViewer { Content = panel, Background = SystemColors.WindowBrush, Foreground = SystemColors.WindowTextBrush, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
