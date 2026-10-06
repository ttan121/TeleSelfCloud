using System.IO;
using System.Windows;
using Microsoft.Win32;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private LocalProfileLease? databaseProfileLease;
    public LocalProfileLease? DatabaseProfileLease
    {
        get => databaseProfileLease;
        set { value?.RequireWithin(LocalDataRoot); databaseProfileLease = value; ApplyLocalDatabaseAvailability(); }
    }
    private string LocalDatabaseRoot => Path.GetDirectoryName(Path.GetFullPath(_stagingRoot))!;
    private readonly Dictionary<string, LocalCacheVerificationStore> protectedCacheStores = new(StringComparer.OrdinalIgnoreCase);
    private LocalCacheVerificationStore CacheStoreForRoot(string root)
    {
        root = Path.GetFullPath(root);
        if (LocalCacheProtection.IsConfigured(root)) _ = LocalCacheProtection.IsReady(root);
        if (!protectedCacheStores.TryGetValue(root, out var store)) { store = LocalCacheProtection.OpenStore(root); protectedCacheStores.Add(root, store); }
        return store;
    }
    private VaultProfileStores CreateProtectedVaultStores(string root) => new(root, LocalDatabaseProtection.KeyForOpening(root), CacheStoreForRoot(root));
    private LocalStagingContentStore CreateStagingContentStore(string? catalogRootOverride = null)
    {
        var catalogRoot = Path.GetFullPath(catalogRootOverride ?? LocalDatabaseRoot);
        var lease = DatabaseProfileLease ?? throw new InvalidOperationException("The local profile must be open before staging data is materialized.");
        return LocalStagingContentStoreFactory.Create(LocalDataRoot, catalogRoot, lease);
    }
    private void ApplyLocalDatabaseAvailability()
    {
        if (LocalDatabaseActions is null || string.IsNullOrEmpty(_stagingRoot)) return;
        var root = LocalDatabaseRoot;
        var relative = Path.GetRelativePath(LocalDataRoot, root);
        LocalDatabaseScope.Text = relative == "." ? UiText.Instance.Get("localdb.shared") : relative;
        var ready = DatabaseProfileLease is not null && !_operationBusy && !_shutdownStarted;
        try
        {
            var status = LocalDatabaseProtection.Status(root);
            EnableLocalDatabaseButton.IsEnabled = ready && !status.Configured;
            ExportLocalDatabaseKeyButton.IsEnabled = ready && status.Configured;
            RecoverLocalDatabaseKeyButton.IsEnabled = ready && status.Configured;
            LocalDatabaseReport.Text = UiText.Instance.Get(!status.Configured ? "localdb.off" : status.Stage == "Ready" ? "localdb.enabled" : "localdb.pending");
            EnableLocalCacheProtectionButton.IsEnabled = ready && status.Stage == "Ready" && !LocalCacheProtection.IsConfigured(root);
            LocalCacheProtectionReport.Text = UiText.Instance.Get(!LocalCacheProtection.IsConfigured(root) ? "localcache.off" : File.Exists(Path.Combine(root, "cache-record-migration.tsc")) && LocalCacheProtection.IsReady(root) ? "localcache.enabled" : "localcache.pending");
        }
        catch (Exception)
        {
            EnableLocalDatabaseButton.IsEnabled = ExportLocalDatabaseKeyButton.IsEnabled = false;
            EnableLocalCacheProtectionButton.IsEnabled = false;
            LocalCacheProtectionReport.Text = UiText.Instance.Get("localcache.pending");
            RecoverLocalDatabaseKeyButton.IsEnabled = ready && LocalDatabaseProtection.IsConfigured(root);
            LocalDatabaseReport.Text = UiText.Instance.Get("localdb.recoveryIntro");
        }
        ApplyLocalRegistryAvailability();
    }
    private void ApplyLocalRegistryAvailability()
    {
        EnableLocalRegistryProtectionButton.IsEnabled = false;
        var account = _telegramAccountLoaded ? _activeTelegramAccountId : null;
        if (account is null)
        { LocalRegistryScope.Text = string.Empty; LocalRegistryReport.Text = UiText.Instance.Get("localregistry.selectAccount"); return; }
        LocalRegistryScope.Text = string.Format(UiText.Instance.Get("localregistry.scope"), account);
        try
        {
            var root = GetAccountDataDirectory(account);
            var configured = LocalVaultRegistryProtection.IsConfigured(root);
            if (configured)
            {
                var completed = File.Exists(Path.Combine(root, "vault-registry-migration.tsc"));
                if (completed) LocalVaultRegistryProtection.RequireReady(root, account);
                LocalRegistryReport.Text = UiText.Instance.Get(completed ? "localregistry.enabled" : "localregistry.pending");
                return;
            }
            var ready = LocalDatabaseProtection.IsConfigured(root) && LocalDatabaseProtection.Status(root).Stage == "Ready" && File.Exists(Path.Combine(root, "vaults.json"));
            LocalRegistryReport.Text = UiText.Instance.Get(ready ? "localregistry.off" : "localregistry.prerequisite");
            EnableLocalRegistryProtectionButton.IsEnabled = ready && DatabaseProfileLease is not null && !_operationBusy && !_shutdownStarted;
        }
        catch (Exception) { LocalRegistryReport.Text = UiText.Instance.Get("localregistry.recovery"); }
    }
    private async void EnableLocalRegistryProtection_Click(object sender, RoutedEventArgs e)
    {
        if (DatabaseProfileLease is null || _activeTelegramAccountId is null) return;
        var account = _activeTelegramAccountId;
        await SaveLocalRegistryProtectionAsync(GetAccountDataDirectory(account), account, DatabaseProfileLease);
    }
    private bool RegistryScopeMatches(string root, string account, LocalProfileLease lease) =>
        ReferenceEquals(DatabaseProfileLease, lease) && _telegramAccountLoaded && _activeTelegramAccountId == account &&
        Path.GetFullPath(root).Equals(GetAccountDataDirectory(account), StringComparison.OrdinalIgnoreCase);
    private async Task SaveLocalRegistryProtectionAsync(string root, string account, LocalProfileLease lease)
    {
        if (_operationBusy || _shutdownStarted || !RegistryScopeMatches(root, account, lease))
        { StatusText.Text = UiText.Instance.Get("localregistry.scopeChanged"); return; }
        if (LocalVaultRegistryProtection.IsConfigured(root)) return;
        SetBusy(true, UiText.Instance.Get("localregistry.working"), canCancel: false);
        try
        {
            await Task.Run(() => LocalVaultRegistryProtection.RequestAsync(root, account, lease, CancellationToken.None));
            StatusText.Text = UiText.Instance.Get(RegistryScopeMatches(root, account, lease) ? "localregistry.pending" : "localregistry.savedOriginal");
        }
        catch (Exception) { StatusText.Text = UiText.Instance.Get("localregistry.failed"); }
        finally { SetBusy(false); ApplyActionAvailability(); }
    }
    private async void LocalDatabaseAction_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _shutdownStarted || DatabaseProfileLease is null) return;
        var root = LocalDatabaseRoot; var lease = DatabaseProfileLease;
        if (sender == RecoverLocalDatabaseKeyButton)
        {
            SetBusy(true, UiText.Instance.Get("localdb.working"), canCancel: false);
            try { var dialog = new LocalDatabaseRecoveryWindow(root, lease, () => !_shutdownStarted && DatabaseScopeMatches(root, lease)) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }; if (dialog.ShowDialog() == true) StatusText.Text = UiText.Instance.Get(DatabaseScopeMatches(root, lease) ? "localdb.recovered" : "localdb.savedOriginalScope"); }
            finally { SetBusy(false); ApplyActionAvailability(); }
            return;
        }
        var enable = sender == EnableLocalDatabaseButton;
        if (enable == LocalDatabaseProtection.IsConfigured(root)) return;
        var passphrase = Prompt(UiText.Instance.Get("metadata.crypto.passphrase"), UiText.Instance.Get("localdb.title"), secret: true, allowEmpty: false,
            UiText.Instance.Get("localdb.helper"));
        if (passphrase is null) return;
        if (passphrase.Length < 12) { StatusText.Text = UiText.Instance.Get("encryption.passphraseTooShort"); return; }
        var confirm = Prompt(UiText.Instance.Get("encryption.confirm.prompt"), UiText.Instance.Get("localdb.title"), secret: true);
        if (confirm != passphrase) { StatusText.Text = UiText.Instance.Get("encryption.passphraseMismatch"); return; }
        var save = new SaveFileDialog { Filter = "TeleSelfCloud database key (*.tsc-db-key.json)|*.tsc-db-key.json", FileName = "local-catalog.tsc-db-key.json" };
        if (save.ShowDialog(this) != true) return;
        await SaveLocalDatabaseKeyAsync(root, lease, save.FileName, passphrase, enable);
    }
    private void EnableLocalCacheProtection_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _shutdownStarted || DatabaseProfileLease is null) return;
        var root = LocalDatabaseRoot;
        try { LocalCacheProtection.Request(root, DatabaseProfileLease); StatusText.Text = UiText.Instance.Get("localcache.pending"); }
        catch (Exception) { StatusText.Text = UiText.Instance.Get("localdb.saveFailed"); }
        ApplyActionAvailability();
    }
    private bool DatabaseScopeMatches(string root, LocalProfileLease lease) =>
        ReferenceEquals(DatabaseProfileLease, lease) && Path.GetFullPath(root).Equals(LocalDatabaseRoot, StringComparison.OrdinalIgnoreCase);
    private async Task SaveLocalDatabaseKeyAsync(string root, LocalProfileLease lease, string destination, string passphrase, bool enable)
    {
        // Modal prompts pump the dispatcher: account/session events can change stores
        // while the user chooses a backup. Recheck before any backup/policy/key write.
        if (_operationBusy || _shutdownStarted || !DatabaseScopeMatches(root, lease))
        { StatusText.Text = UiText.Instance.Get("localdb.scopeChanged"); return; }
        SetBusy(true, UiText.Instance.Get("localdb.working"), canCancel: false);
        try
        {
            await Task.Run(() => { if (enable) LocalDatabaseProtection.Request(root, destination, passphrase, lease); else LocalDatabaseProtection.Export(root, destination, passphrase, lease); });
            StatusText.Text = UiText.Instance.Get(!DatabaseScopeMatches(root, lease) ? "localdb.savedOriginalScope" : enable ? "localdb.pending" : "localdb.exported");
        }
        catch (Exception) { StatusText.Text = UiText.Instance.Get("localdb.saveFailed"); }
        finally { SetBusy(false); ApplyActionAvailability(); }
    }
}
