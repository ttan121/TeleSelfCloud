using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using TeleSelfCloud.Infrastructure.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private VaultMetadataKey? _metadataKey;
    private bool _metadataKeyReadFailed;
    private long _metadataKeyGeneration;
    private void LoadMetadataKey(string root, string account, long chat)
    {
        _metadataKeyGeneration++;
        _metadataKey?.Dispose(); _metadataKey = null; _metadataKeyReadFailed = false;
        try { _metadataKey = VaultMetadataKeyStore.Load(root, account, chat); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or System.Text.Json.JsonException or FormatException or UnauthorizedAccessException)
        { _metadataKeyReadFailed = true; }
    }
    private VaultMetadataKey? MetadataKeyForPublishing()
    {
        if (_metadataKeyReadFailed || (_storageChannel is not null && _storageChannel.AccountId != _activeTelegramAccountId) ||
            (_metadataKey is not null && (_storageChannel is null || _metadataKey.AccountId != _storageChannel.AccountId || _metadataKey.ChatId != _storageChannel.ChatId)) ||
            (_storageChannel is not null && _metadataKey is null && VaultMetadataKeyStore.IsConfigured(GetVaultDataDirectory(_storageChannel))))
            throw new InvalidDataException("Recover this vault's metadata key before publishing. Its protected files were kept.");
        return _metadataKey;
    }
    private void ApplyMetadataEncryptionAvailability()
    {
        if (MetadataEncryptionActions is null) return;
        var scoped = _storageChannel is not null && _storageChannel.AccountId == _activeTelegramAccountId;
        MetadataEncryptionActions.Visibility = scoped ? Visibility.Visible : Visibility.Collapsed;
        var ready = scoped && !_operationBusy && !_shutdownStarted;
        EnableMetadataEncryptionButton.IsEnabled = ready && _metadataKey is null && !_metadataKeyReadFailed &&
            !VaultMetadataKeyStore.IsConfigured(GetVaultDataDirectory(_storageChannel!));
        ExportMetadataKeyButton.IsEnabled = ready && _metadataKey is not null;
        RecoverMetadataKeyButton.IsEnabled = ready;
        MetadataEncryptionReport.Text = UiText.Instance.Get(_metadataKeyReadFailed ? "metadata.crypto.keyUnavailable" : _metadataKey is not null ? "metadata.crypto.enabled" : "metadata.crypto.off");
    }
    private async void MetadataEncryptionAction_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _shutdownStarted || _storageChannel is null || _storageChannel.AccountId != _activeTelegramAccountId) return;
        var channel = _storageChannel; var root = GetVaultDataDirectory(channel);
        var generation = _metadataKeyGeneration; var session = _telegramSession;
        var create = sender == EnableMetadataEncryptionButton;
        var recover = sender == RecoverMetadataKeyButton;
        if (create && (_metadataKey is not null || VaultMetadataKeyStore.IsConfigured(root))) return;
        if (!create && !recover && _metadataKey is null) return;
        string? sourcePath = null;
        if (recover)
        {
            var open = new OpenFileDialog { Filter = "TeleSelfCloud metadata key (*.tsc-key.json)|*.tsc-key.json|JSON (*.json)|*.json" };
            if (open.ShowDialog(this) != true) return; sourcePath = open.FileName;
        }
        var passphrase = Prompt(UiText.Instance.Get("metadata.crypto.passphrase"), UiText.Instance.Get("metadata.crypto.title"), secret: true, allowEmpty: false,
            UiText.Instance.Get("metadata.crypto.helper"));
        if (passphrase is null) return;
        if (passphrase.Length < 12) { StatusText.Text = UiText.Instance.Get("encryption.passphraseTooShort"); return; }
        if (!recover)
        {
            var confirm = Prompt(UiText.Instance.Get("encryption.confirm.prompt"), UiText.Instance.Get("metadata.crypto.title"), secret: true);
            if (confirm != passphrase) { StatusText.Text = UiText.Instance.Get("encryption.passphraseMismatch"); return; }
        }
        SaveFileDialog? save = null;
        if (!recover)
        {
            save = new SaveFileDialog { Filter = "TeleSelfCloud metadata key (*.tsc-key.json)|*.tsc-key.json", FileName = "vault-metadata.tsc-key.json" };
            if (save.ShowDialog(this) != true) return;
            var destination = Path.GetFullPath(save.FileName);
            if (destination.StartsWith(Path.GetFullPath(LocalDataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { StatusText.Text = UiText.Instance.Get("metadata.crypto.backupOutsideProfile"); return; }
        }
        await SaveMetadataKeyActionAsync(root, channel, generation, create, recover, recover ? sourcePath! : save!.FileName, passphrase,
            create ? token => VaultMetadataKeyCreationGuard.EnsureNoProtectedHistoryAsync(session ?? throw new InvalidOperationException("The active Telegram session is unavailable."), channel.ChatId, token) : null);
    }
    private bool MetadataKeyScopeMatches(string root, TelegramStorageChannelInfo channel, long generation)
    {
        if (_shutdownStarted || generation != _metadataKeyGeneration || _activeTelegramAccountId != channel.AccountId ||
            _storageChannel?.AccountId != channel.AccountId || _storageChannel.ChatId != channel.ChatId || _vaultRegistry?.AccountId != channel.AccountId) return false;
        return Path.GetFullPath(root).Equals(GetVaultDataDirectory(_storageChannel), StringComparison.OrdinalIgnoreCase);
    }
    private async Task SaveMetadataKeyActionAsync(string root, TelegramStorageChannelInfo channel, long generation, bool create, bool recover,
        string path, string passphrase, Func<CancellationToken, Task>? preflight)
    {
        if (_operationBusy || !MetadataKeyScopeMatches(root, channel, generation))
        { StatusText.Text = UiText.Instance.Get("metadata.crypto.scopeChanged"); return; }
        using var snapshot = !create && !recover ? _metadataKey?.Clone() : null;
        if (!create && !recover && snapshot is null) { StatusText.Text = UiText.Instance.Get("metadata.crypto.keyUnavailable"); return; }
        if (snapshot is not null && (snapshot.AccountId != channel.AccountId || snapshot.ChatId != channel.ChatId))
        { StatusText.Text = UiText.Instance.Get("metadata.crypto.scopeChanged"); return; }
        VaultMetadataKey? candidate = null;
        SetBusy(true, UiText.Instance.Get("metadata.crypto.working"), canCancel: create);
        var token = OperationToken;
        try
        {
            if (recover)
            {
                var backup = VaultMetadataKeyStore.ReadBackup(path);
                candidate = await Task.Run(() => VaultMetadataKey.Recover(backup, channel.AccountId, channel.ChatId, passphrase));
                if (!MetadataKeyScopeMatches(root, channel, generation)) { StatusText.Text = UiText.Instance.Get("metadata.crypto.scopeChanged"); return; }
                VaultMetadataKeyStore.Save(root, candidate, recoverExisting: true);
            }
            else
            {
                if (create)
                    await (preflight ?? throw new InvalidOperationException("Metadata history verification is required."))(token);
                if (!MetadataKeyScopeMatches(root, channel, generation)) { StatusText.Text = UiText.Instance.Get("metadata.crypto.scopeChanged"); return; }
                candidate = create ? VaultMetadataKey.Create(channel.AccountId, channel.ChatId) : null;
                var key = candidate ?? snapshot!;
                var backup = await Task.Run(() => key.ExportBackup(passphrase));
                token.ThrowIfCancellationRequested();
                if (!MetadataKeyScopeMatches(root, channel, generation)) { StatusText.Text = UiText.Instance.Get("metadata.crypto.scopeChanged"); return; }
                VaultMetadataKeyStore.WriteBackup(path, backup);
                using var check = await Task.Run(() => VaultMetadataKey.Recover(VaultMetadataKeyStore.ReadBackup(path), channel.AccountId, channel.ChatId, passphrase));
                if (check.KeyId != key.KeyId || check.ScopeProof() != key.ScopeProof()) throw new CryptographicException("The recovery backup does not match the prepared metadata key.");
                token.ThrowIfCancellationRequested();
                if (!MetadataKeyScopeMatches(root, channel, generation)) { StatusText.Text = UiText.Instance.Get("metadata.crypto.backupOnly"); return; }
                if (create) VaultMetadataKeyStore.Save(root, candidate!);
            }
            LoadMetadataKey(root, channel.AccountId, channel.ChatId);
            StatusText.Text = UiText.Instance.Get("metadata.crypto.saved");
        }
        catch (CryptographicException) { StatusText.Text = UiText.Instance.Get("metadata.crypto.invalidRecovery"); }
        catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
        finally { candidate?.Dispose(); SetBusy(false); ApplyActionAvailability(); }
    }
}
