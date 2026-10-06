using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow : Window
{
	public void ReportExternalStagingPaths(int count)
	{
		if (count <= 0) return;
		MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture,
			UiText.Instance.Get("localdb.stage.ExternalStagingWarning"), count),
			UiText.Instance.Get("localdb.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct OpenAsInfo
	{
		[MarshalAs(UnmanagedType.LPWStr)]
		public string FilePath;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? ClassName;

		public uint Flags;
	}

	private sealed record ManifestItem(FileManifest Manifest, TransferQueueState? QueueState, LocalCacheVerification? CacheVerification, bool RemoteStatusUnknown)
	{
		public string FileName => Manifest.FileName;

		public string EncryptionLabel
		{
			get
			{
				if (Manifest.Encryption is not null)
				{
					return UiText.Instance.Get("files.encrypted");
				}
				return string.Empty;
			}
		}

		public string FormattedSize
		{
			get
			{
				if (Manifest.LogicalSize >= 1048576)
				{
					return $"{(double)Manifest.LogicalSize / 1048576.0:N1} MB";
				}
				return $"{(double)Manifest.LogicalSize / 1024.0:N1} KB";
			}
		}

		public string FormattedDate => (Manifest.FileModifiedAtUtc ?? Manifest.UpdatedAtUtc ?? DateTimeOffset.UtcNow).LocalDateTime.ToString("g");

		public string StatusLabel
		{
			get
			{
				if (!QueueState.HasValue)
				{
					if (!IsVerifiedOffline)
					{
						if (!Manifest.Committed || !RemoteStatusUnknown)
						{
							if (!Manifest.IsInTrash)
							{
								if (Manifest.Committed)
								{
									return UiText.Instance.Get("files.status.cloud");
								}
								return UiText.Instance.Get("files.status.localDraft");
							}
							return UiText.Instance.Get("files.stateTrash");
						}
						return UiText.Instance.Get("files.status.remoteUnknown");
					}
					return UiText.Instance.Get("files.status.offline");
				}
				return UiText.Instance.Get("queue.state." + QueueState.Value);
			}
		}

		public int LocalPartFilesPresent => Manifest.Parts.Count((PartRecord part) => !string.IsNullOrWhiteSpace(part.StagingPath) && File.Exists(part.StagingPath));

		public bool HasAllPartFiles
		{
			get
			{
				if (Manifest.Parts.Count > 0)
				{
					return LocalPartFilesPresent == Manifest.Parts.Count;
				}
				return false;
			}
		}

		public bool HasCacheVerification => CacheVerification is not null;

		public bool IsVerifiedOffline
		{
			get
			{
				var cacheVerification = CacheVerification;
				if (cacheVerification is not null && cacheVerification.State == LocalCacheIntegrityState.AvailableOffline)
				{
					return cacheVerification.IsCurrentFor(Manifest);
				}
				return false;
			}
		}

		public string LocalPartsStatus
		{
			get
			{
				string text;
				if (CacheVerification is null)
				{
					text = UiText.Instance.Get("files.localNotVerified");
				}
				else
				{
					string text2 = ((!CacheVerification.IsCurrentFor(Manifest)) ? UiText.Instance.Get("files.verificationStale") : (CacheVerification.State switch
					{
						LocalCacheIntegrityState.AvailableOffline => string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("files.verifiedOfflineAt"), CacheVerification.VerifiedAtUtc.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)), 
						LocalCacheIntegrityState.Partial => string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("files.partialCache"), CacheVerification.ValidParts, CacheVerification.PartCount), 
						LocalCacheIntegrityState.Missing => UiText.Instance.Get("files.noLocalCache"), 
						_ => UiText.Instance.Get("files.cacheError"), 
					}));
					text = text2;
				}
				string arg = text;
				return string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("files.localParts"), LocalPartFilesPresent, Manifest.Parts.Count, arg);
			}
		}

		public string DisplayName
		{
			get
			{
				string text;
				if (!Manifest.IsInTrash)
				{
					if (!Manifest.Committed)
					{
						TransferQueueState? queueState = QueueState;
						text = ((!queueState.HasValue) ? UiText.Instance.Get("files.stateStaged") : QueueStateLabel(queueState.GetValueOrDefault()));
					}
					else
					{
						text = (RemoteStatusUnknown ? UiText.Instance.Get("files.stateRemoteUnknown") : UiText.Instance.Get("files.stateUploaded"));
					}
				}
				else
				{
					text = UiText.Instance.Get("files.stateTrash");
				}
				string value = text;
				string text2 = $"{Manifest.FileName}  |  {Manifest.LogicalSize.ToString("N0", CultureInfo.CurrentCulture)} {UiText.Instance.Get("files.bytes")}  |  {Manifest.Parts.Count} {UiText.Instance.Get("files.parts")}  |  {value}  |  {LocalPartsStatus}";
				if (Manifest.Encryption is not null)
				{
					text2 = text2 + "  |  " + UiText.Instance.Get("files.encrypted");
				}
				if (!string.IsNullOrWhiteSpace(Manifest.FolderPath))
				{
					text2 = text2 + "  |  " + Manifest.FolderPath;
				}
				DateTimeOffset? dateTimeOffset = Manifest.FileModifiedAtUtc ?? Manifest.UpdatedAtUtc;
				if (dateTimeOffset.HasValue)
				{
					DateTimeOffset valueOrDefault = dateTimeOffset.GetValueOrDefault();
					text2 = text2 + "  |  " + UiText.Instance.Get("files.modified") + " " + valueOrDefault.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
				}
				return text2;
			}
		}

		private static string QueueStateLabel(TransferQueueState state)
		{
			UiText instance = UiText.Instance;
			return instance.Get(state switch
			{
				TransferQueueState.Pending => "queue.state.pending", 
				TransferQueueState.Running => "queue.state.running", 
				TransferQueueState.Paused => "queue.state.paused", 
				TransferQueueState.Failed => "queue.state.failed", 
				TransferQueueState.Completed => "queue.state.completed", 
				TransferQueueState.Cancelled => "queue.state.cancelled", 
				_ => "files.stateStaged", 
			});
		}
	}

	private sealed class QueueItemView : INotifyPropertyChanged
	{
		private TransferQueueItem _item;

		private DateTimeOffset? _lastSampleAtUtc;

		private long _lastSampleBytes;

		private int _completedParts;

		private int _partCount;

		private string? _retryStatus;

		private double _speedBytesPerSecond;

		public TransferQueueItem Item => _item;

		public string Title => UiText.Instance.Get((_item.Direction == TransferDirection.Upload) ? "queue.upload" : "queue.download") + "  ·  " + _item.FileName;

		public Visibility PauseVisibility => _item.State == TransferQueueState.Running ? Visibility.Visible : Visibility.Collapsed;

		public Visibility CancelVisibility => _item.State is TransferQueueState.Pending or TransferQueueState.Running or TransferQueueState.Paused or TransferQueueState.Failed
			? Visibility.Visible : Visibility.Collapsed;

		public string StateLabel => _item.State switch
		{
			TransferQueueState.Pending => UiText.Instance.Get("queue.state.pending"), 
			TransferQueueState.Running => UiText.Instance.Get("queue.state.running"), 
			TransferQueueState.Paused => UiText.Instance.Get("queue.state.paused"), 
			TransferQueueState.Failed => UiText.Instance.Get("queue.state.failed"), 
			TransferQueueState.Completed => UiText.Instance.Get("queue.state.completed"), 
			TransferQueueState.Cancelled => UiText.Instance.Get("queue.state.cancelled"), 
			_ => _item.State.ToString(), 
		};

		public string GroupLabel
		{
			get
			{
				UiText instance = UiText.Instance;
				string key;
				switch (_item.State)
				{
				case TransferQueueState.Running:
					key = "queue.group.running";
					break;
				case TransferQueueState.Pending:
				case TransferQueueState.Paused:
					key = "queue.group.waiting";
					break;
				case TransferQueueState.Failed:
					key = "queue.group.failed";
					break;
				default:
					key = "queue.group.finished";
					break;
				}
				return instance.Get(key);
			}
		}

		public double ProgressPercent
		{
			get
			{
				if (_item.TotalBytes > 0)
				{
					return Math.Clamp((double)_item.TransferredBytes * 100.0 / (double)_item.TotalBytes, 0.0, 100.0);
				}
				return (_item.State == TransferQueueState.Completed) ? 100 : 0;
			}
		}

		public string ProgressDetails
		{
			get
			{
				string text = $"{_item.TransferredBytes:N0} / {_item.TotalBytes:N0} {UiText.Instance.Get("queue.bytes")}";
				if (_partCount > 0)
				{
					text += $"  ·  {_completedParts}/{_partCount} {UiText.Instance.Get("queue.parts")}";
				}
				if (_item.State == TransferQueueState.Running && _speedBytesPerSecond > 0.0)
				{
					text += $"  ·  {FormatRate(_speedBytesPerSecond)}  ·  {FormatEta(_item.TotalBytes - _item.TransferredBytes, _speedBytesPerSecond)} {UiText.Instance.Get("queue.left")}";
				}
				if (_retryStatus != null)
				{
					text = text + "  ·  " + _retryStatus;
				}
				return text;
			}
		}

		public string AttemptLabel => $"{_item.AttemptCount} {UiText.Instance.Get("queue.attempts")}";

		public string ErrorText
		{
			get
			{
				var lastError = _item.LastError;
				if (lastError == null)
				{
					return string.Empty;
				}
				return UiText.Instance.LocalizeMessage(lastError);
			}
		}

		public Visibility ErrorVisibility
		{
			get
			{
				if (!string.IsNullOrWhiteSpace(_item.LastError))
				{
					return Visibility.Visible;
				}
				return Visibility.Collapsed;
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		public QueueItemView(TransferQueueItem item)
		{
			_item = item;
		}

		public void UpdateItem(TransferQueueItem item)
		{
			_item = item;
			if (item.State != TransferQueueState.Running)
			{
				_speedBytesPerSecond = 0.0;
				_lastSampleAtUtc = null;
				_retryStatus = null;
			}
			NotifyAll();
		}

		public void RefreshLocalization()
		{
			NotifyAll();
		}

		public void UpdateRetryStatus(int retryNumber, int maxAttempts, TimeSpan delay)
		{
			_retryStatus = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("queue.retryStatus"), retryNumber, maxAttempts, delay.TotalSeconds.ToString("N0", CultureInfo.CurrentCulture));
			NotifyAll();
		}

		public void UpdateProgress(TransferProgress progress)
		{
			_retryStatus = null;
			DateTimeOffset utcNow = DateTimeOffset.UtcNow;
			DateTimeOffset? lastSampleAtUtc = _lastSampleAtUtc;
			if (lastSampleAtUtc.HasValue)
			{
				DateTimeOffset valueOrDefault = lastSampleAtUtc.GetValueOrDefault();
				double totalSeconds = (utcNow - valueOrDefault).TotalSeconds;
				long num = progress.TransferredBytes - _lastSampleBytes;
				if (totalSeconds >= 0.25 && num >= 0)
				{
					double num2 = (double)num / totalSeconds;
					_speedBytesPerSecond = ((_speedBytesPerSecond == 0.0) ? num2 : (_speedBytesPerSecond * 0.7 + num2 * 0.3));
				}
			}
			_lastSampleAtUtc = utcNow;
			_lastSampleBytes = progress.TransferredBytes;
			_completedParts = progress.CompletedParts;
			_partCount = progress.PartCount;
			_item = _item with
			{
				TransferredBytes = progress.TransferredBytes,
				TotalBytes = progress.TotalBytes,
				State = TransferQueueState.Running
			};
			NotifyAll();
		}

		private static string FormatRate(double bytesPerSecond)
		{
			double num = bytesPerSecond;
			string value = "B/s";
			if (num >= 1024.0)
			{
				num /= 1024.0;
				value = "KiB/s";
			}
			if (num >= 1024.0)
			{
				num /= 1024.0;
				value = "MiB/s";
			}
			return $"{num:N1} {value}";
		}

		private static string FormatEta(long remainingBytes, double bytesPerSecond)
		{
			if (bytesPerSecond <= 0.0 || remainingBytes <= 0)
			{
				return "<1 min";
			}
			TimeSpan timeSpan = TimeSpan.FromSeconds((double)remainingBytes / bytesPerSecond);
			if (!(timeSpan.TotalDays >= 1.0))
			{
				if (!(timeSpan.TotalHours >= 1.0))
				{
					if (timeSpan.TotalMinutes >= 1.0)
					{
						return $"{(int)timeSpan.TotalMinutes}m {timeSpan.Seconds}s";
					}
					return $"{Math.Max(1, (int)Math.Ceiling(timeSpan.TotalSeconds))}s";
				}
				return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
			}
			return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h";
		}

		private void NotifyAll()
		{
			string[] array = new string[11] { "Item", "Title", "StateLabel", "GroupLabel", "ProgressPercent", "ProgressDetails", "AttemptLabel", "ErrorText", "ErrorVisibility", "PauseVisibility", "CancelVisibility" };
			foreach (string propertyName in array)
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
			}
		}
	}

	private ILocalFileWorkflow _workflow;

	private IManifestStore _manifestStore;

	private ITransferQueueStore _transferQueueStore;

	private IRemoteSyncCheckpointStore _syncCheckpointStore;

	private ILocalFolderStore _folderStore;

	private IManifestStore _sharedManifestStore;

	private ITransferQueueStore _sharedTransferQueueStore;

	private IRemoteSyncCheckpointStore _sharedSyncCheckpointStore;

	private ILocalFolderStore _sharedFolderStore;

	private LocalCacheVerificationStore _localCacheVerificationStore;

	private LocalCacheVerificationStore _sharedLocalCacheVerificationStore;

	private readonly TransferRetryPolicy _transferRetryPolicy = new TransferRetryPolicy();

	private readonly Dictionary<string, QueueItemView> _queueViews = new Dictionary<string, QueueItemView>(StringComparer.Ordinal);

	private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeQueueItemCancellation = new(StringComparer.Ordinal);

	private readonly ConcurrentDictionary<string, byte> _cancelQueueItemOnStop = new(StringComparer.Ordinal);

	private TransferPreferencesStore? _transferPreferences;

	private string _stagingRoot;

	private ManifestItem[] _manifestItems = Array.Empty<ManifestItem>();

	private string _currentFolderPath = string.Empty;

	private string[] _localFolderPaths = Array.Empty<string>();

	private bool _buildingFolderTree;

	private TelegramAuthSession? _telegramSession;

	private TelegramAppCredentials? _telegramAppCredentials;

	private UiPreferencesStore? _uiPreferencesStore;
	private AppLockStore? _appLockStore;
	private readonly AppLockAttemptThrottle _appLockAttemptThrottle = new();
	private readonly DispatcherTimer _appLockTimer = new(DispatcherPriority.Background);
	private DateTimeOffset _lastApplicationInputUtc = DateTimeOffset.UtcNow;
	private bool _appLockConfigured;
	private bool _applicationLocked;
	private bool _appLockFaulted;
	private bool _loadingAppLockControls;
	private bool _appLockSetupChanging;
	private bool _unlockVerificationInProgress;
	private int _appLockIdleTimeoutMinutes;
	private long _appLockGeneration;

	private bool _keepLogin;

	private bool _darkMode;

	private bool _loadingUiPreferences;

	private bool _syncingKeepLoginControls;

	private bool _explicitLogoutRequested;

	private bool? _localDatabaseEncryptionEnabled;

	private string? _telegramSessionDirectory;

	private bool _switchingTelegramSessionDirectory;

	private TelegramStorageChannelInfo? _storageChannel;

	private readonly HashSet<string> _observedRemoteManifestIds = new HashSet<string>(StringComparer.Ordinal);

	private string? _activeTelegramAccountId;

	private bool _telegramAccountLoaded;

	private bool _storageConnectionInProgress;

	private bool _operationBusy;

	private bool _operationCanCancel;

	private string _cancelButtonTextKey = "status.cancel";

	private CancellationTokenSource? _operationCancellation;

	private TaskCompletionSource? _operationFinished;

	private bool _shutdownStarted;

	private Dictionary<DependencyObject, bool>? _legacyLocalViewControlStates;

	private bool _allowClose;

	private bool _isTrashPage;

	private string _chosenFilePath = string.Empty;
	private string[] _chosenUploadPaths = Array.Empty<string>();

	private DateTimeOffset? _lastSyncAtUtc;

	private string? _deferredAuthorizationState;

	private string? _authenticationInputError;

	private IInputElement? _authenticationReturnFocus;

	private string? _lastPromptedAuthorizationState;

	private string _authenticationFlowState = string.Empty;

	private ManifestItem? SelectedManifestItem
	{
		get
		{
			if (!_isTrashPage)
			{
				return ManifestList?.SelectedItem as ManifestItem;
			}
			return TrashManifestList?.SelectedItem as ManifestItem;
		}
	}

	private readonly string _localDataRoot;
	private string LocalDataRoot => _localDataRoot;

	private CancellationToken OperationToken => _operationCancellation?.Token ?? CancellationToken.None;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHOpenWithDialog(nint owner, ref OpenAsInfo info);

	public MainWindow(ILocalFileWorkflow workflow, string stagingRoot, IManifestStore manifestStore, ITransferQueueStore transferQueueStore, IRemoteSyncCheckpointStore syncCheckpointStore, ILocalFolderStore folderStore, string? localDataRoot = null)
	{
		_localDataRoot = Path.GetFullPath(localDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeleSelfCloud", "P0"));
		_loadingUiPreferences = true;
		InitializeComponent();
		UiText.Instance.LoadPreference(Path.Combine(LocalDataRoot, "ui-language.json"));
		_uiPreferencesStore = new UiPreferencesStore(Path.Combine(LocalDataRoot, "ui-preferences.json"));
		(_keepLogin, _darkMode, _explorerIcons) = _uiPreferencesStore.Load();
		ApplyExplorerView();
		KeepLoginCheckBox.IsChecked = _keepLogin;
		KeepLoginSettingsCheckBox.IsChecked = _keepLogin;
		DarkModeCheckBox.IsChecked = _darkMode;
		ApplyTheme(_darkMode);
		_loadingUiPreferences = false;
		foreach (ComboBoxItem item in (IEnumerable)LanguageBox.Items)
		{
			if ((string)item.Tag == UiText.Instance.Language)
			{
				LanguageBox.SelectedItem = item;
				break;
			}
		}
		_workflow = workflow;
		_stagingRoot = stagingRoot;
		_manifestStore = manifestStore;
		_transferQueueStore = transferQueueStore ?? throw new ArgumentNullException("transferQueueStore");
		_syncCheckpointStore = syncCheckpointStore ?? throw new ArgumentNullException("syncCheckpointStore");
		_folderStore = folderStore ?? throw new ArgumentNullException("folderStore");
		_transferQueueStore = transferQueueStore;
		_syncCheckpointStore = syncCheckpointStore;
		_sharedManifestStore = manifestStore;
		_sharedTransferQueueStore = transferQueueStore;
		_sharedSyncCheckpointStore = syncCheckpointStore;
		_sharedFolderStore = folderStore;
		string path = LocalDataRoot;
		_localCacheVerificationStore = CacheStoreForRoot(path);
		_sharedLocalCacheVerificationStore = _localCacheVerificationStore;
		_transferPreferences = new TransferPreferencesStore(Path.Combine(path, "transfer-preferences.json"));
		ParallelTransfersBox.SelectedIndex = _transferPreferences.LoadMaxConcurrentTransfers() - 1;
		LanguageBox.SelectedIndex = ((UiText.Instance.Language == "en") ? 1 : 0);
		InitializeApplicationLock();
		UiText.Instance.LanguageChanged += UiText_LanguageChanged;
		_appLockTimer.Interval = TimeSpan.FromSeconds(1);
		_appLockTimer.Tick += AppLockTimer_Tick;
		_appLockTimer.Start();
		InputManager.Current.PreProcessInput += AppLock_PreProcessInput;
		SystemEvents.SessionSwitch += AppLock_SessionSwitch;
		Closing += MainWindow_Closing;
		Loaded += async (object _, RoutedEventArgs _) =>
		{
			try
			{
				await RefreshManifestsAsync();
			}
			catch (Exception ex)
			{
				StatusText.Text = "Could not load local manifests: " + ex.Message;
			}
			try
			{
				await RefreshQueueAsync();
			}
			catch (Exception ex2)
			{
				StatusText.Text = "Could not load saved transfer queue: " + ex2.Message;
			}
			try
			{
				var telegramAppCredentials = CreateCredentialStore().Load();
				if (telegramAppCredentials is not null)
				{
					_telegramAppCredentials = telegramAppCredentials;
					if (_keepLogin && !_applicationLocked)
					{
						StatusText.Text = "Restoring saved Telegram session...";
						StartTelegramSession(telegramAppCredentials, TelegramSessionProfileStore.ResolveForRestore(LocalDataRoot));
					}
				}
			}
			catch (Exception ex3)
			{
				StatusText.Text = "Could not restore the saved Telegram session.";
				MessageBox.Show(this, UiText.Instance.LocalizeMessage(ex3.Message), UiText.Instance.LocalizeMessage("Telegram session restore"), MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		};
		ContentRendered += (_, _) =>
		{
			if (_applicationLocked && !_appLockFaulted) AppLockUnlockPasswordBox.Focus();
		};
		ApplyActionAvailability();
		Closed += (_, _) =>
		{
			_appLockTimer.Stop();
			_appLockTimer.Tick -= AppLockTimer_Tick;
			InputManager.Current.PreProcessInput -= AppLock_PreProcessInput;
			SystemEvents.SessionSwitch -= AppLock_SessionSwitch;
			UiText.Instance.LanguageChanged -= UiText_LanguageChanged;
			_metadataKey?.Dispose();
			foreach (var cache in protectedCacheStores.Values) cache.Dispose(); protectedCacheStores.Clear();
			foreach (var registry in protectedRegistries.Values) registry.Dispose(); protectedRegistries.Clear();
		};
	}

	private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (LanguageBox.SelectedItem is ComboBoxItem { Tag: string tag })
		{
			UiText.Instance.SetLanguage(tag, Path.Combine(LocalDataRoot, "ui-language.json"));
			UpdateLocalDatabaseSecurityStatus();
			UpdateAppLockSettingsUi();
		}
	}

	private void InitializeApplicationLock()
	{
		_appLockStore = new AppLockStore(Path.Combine(LocalDataRoot, "app-lock.json"));
		_loadingAppLockControls = true;
		try
		{
			_appLockConfigured = _appLockStore.IsConfigured;
			_appLockIdleTimeoutMinutes = _appLockConfigured ? _appLockStore.IdleTimeoutMinutes : 0;
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
		{
			_appLockFaulted = true;
			_appLockConfigured = true;
			AppLockUnlockPasswordBox.Visibility = Visibility.Collapsed;
			AppLockUnlockButton.Visibility = Visibility.Collapsed;
			AppLockRecoveryText.Visibility = Visibility.Visible;
			AppLockUnlockErrorText.Text = UiText.Instance.Get("appLock.unavailable");
			AppLockUnlockErrorText.Visibility = Visibility.Visible;
		}
		_applicationLocked = _appLockConfigured || _appLockFaulted;
		AppLockOverlay.Visibility = _applicationLocked ? Visibility.Visible : Visibility.Collapsed;
		if (_applicationLocked && !_appLockFaulted) AppLockRecoveryText.Visibility = Visibility.Visible;
		foreach (ComboBoxItem item in AppLockIdleTimeoutBox.Items)
		{
			if (item.Tag is string tag && int.TryParse(tag, out var minutes) && minutes == _appLockIdleTimeoutMinutes)
			{
				AppLockIdleTimeoutBox.SelectedItem = item;
				break;
			}
		}
		_loadingAppLockControls = false;
		UpdateAppLockSettingsUi();
	}

	private void UpdateAppLockSettingsUi()
	{
		if (AppLockStatusText is null) return;
		AppLockStatusText.Text = _appLockConfigured ? UiText.Instance.Get("appLock.statusOn") : UiText.Instance.Get("appLock.statusOff");
		AppLockConfigureButton.Content = UiText.Instance.Get(_appLockConfigured ? "appLock.change" : "appLock.create");
		AppLockNowButton.IsEnabled = _appLockConfigured && !_appLockFaulted && !_shutdownStarted;
		AppLockIdleTimeoutBox.IsEnabled = _appLockConfigured && !_appLockFaulted && !_shutdownStarted;
	}

	private void AppLock_PreProcessInput(object? sender, PreProcessInputEventArgs e)
	{
		if (!_applicationLocked && !_shutdownStarted) _lastApplicationInputUtc = DateTimeOffset.UtcNow;
	}

	private void AppLockTimer_Tick(object? sender, EventArgs e)
	{
		if (_appLockConfigured && !_appLockFaulted && !_applicationLocked && _appLockIdleTimeoutMinutes > 0 &&
			DateTimeOffset.UtcNow - _lastApplicationInputUtc >= TimeSpan.FromMinutes(_appLockIdleTimeoutMinutes))
			LockApplication();
	}

	private void AppLock_SessionSwitch(object? sender, SessionSwitchEventArgs e)
	{
		if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock or
			SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.RemoteConnect or
			SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.ConsoleConnect)
			_ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(LockApplication));
	}

	private void LockApplication()
	{
		if (!_appLockConfigured || _appLockFaulted || _shutdownStarted) return;
		_appLockGeneration++;
		if (_applicationLocked)
		{
			AppLockOverlay.Visibility = Visibility.Visible;
			AppLockUnlockPasswordBox.Clear();
			return;
		}
		_applicationLocked = true;
		_lastApplicationInputUtc = DateTimeOffset.UtcNow;
		AppLockSetupOverlay.Visibility = Visibility.Collapsed;
		AppLockUnlockPasswordBox.Clear();
		AppLockUnlockErrorText.Visibility = Visibility.Collapsed;
		AppLockOverlay.Visibility = Visibility.Visible;
		foreach (var preview in OwnedWindows.OfType<FilePreviewWindow>().ToArray()) preview.Close();
		_ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => AppLockUnlockPasswordBox.Focus()));
	}

	private async void AppLockUnlock_Click(object sender, RoutedEventArgs e)
	{
		if (!_applicationLocked || _appLockFaulted || _unlockVerificationInProgress || _shutdownStarted || _appLockStore is null) return;
		var remaining = _appLockAttemptThrottle.Remaining(DateTimeOffset.UtcNow);
		if (remaining > TimeSpan.Zero)
		{
			AppLockUnlockErrorText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("appLock.wait"), Math.Ceiling(remaining.TotalSeconds));
			AppLockUnlockErrorText.Visibility = Visibility.Visible;
			return;
		}
		var passphrase = AppLockUnlockPasswordBox.Password;
		AppLockUnlockPasswordBox.Clear();
		var lockGeneration = _appLockGeneration;
		_unlockVerificationInProgress = true;
		AppLockUnlockButton.IsEnabled = false;
		bool verified;
		try { verified = await Task.Run(() => _appLockStore.Verify(passphrase)); }
		catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or CryptographicException)
		{
			_appLockFaulted = true;
			AppLockUnlockPasswordBox.Visibility = Visibility.Collapsed;
			AppLockUnlockButton.Visibility = Visibility.Collapsed;
			AppLockRecoveryText.Visibility = Visibility.Visible;
			AppLockUnlockErrorText.Text = UiText.Instance.Get("appLock.unavailable");
			AppLockUnlockErrorText.Visibility = Visibility.Visible;
			return;
		}
		finally
		{
			_unlockVerificationInProgress = false;
			AppLockUnlockButton.IsEnabled = !_appLockFaulted;
		}
		if (_shutdownStarted || lockGeneration != _appLockGeneration) return;
		if (verified)
		{
			_appLockAttemptThrottle.Reset();
			_applicationLocked = false;
			AppLockOverlay.Visibility = Visibility.Collapsed;
			AppLockUnlockErrorText.Visibility = Visibility.Collapsed;
			_lastApplicationInputUtc = DateTimeOffset.UtcNow;
			if (_keepLogin && _telegramAppCredentials is not null && _telegramSession is null)
			{
				StatusText.Text = "Restoring saved Telegram session...";
				StartTelegramSession(_telegramAppCredentials, TelegramSessionProfileStore.ResolveForRestore(LocalDataRoot));
			}
			Focus();
			return;
		}
		var delay = _appLockAttemptThrottle.RecordFailure(DateTimeOffset.UtcNow);
		AppLockUnlockErrorText.Text = UiText.Instance.Get("appLock.invalid");
		AppLockUnlockErrorText.Visibility = Visibility.Visible;
		if (delay >= TimeSpan.FromSeconds(8))
		{
			AppLockUnlockButton.IsEnabled = false;
			await Task.Delay(delay);
			if (_applicationLocked && !_appLockFaulted && !_shutdownStarted) AppLockUnlockButton.IsEnabled = true;
		}
	}

	private void AppLockUnlockPasswordBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter) { e.Handled = true; AppLockUnlock_Click(sender, e); }
	}

	private void AppLockOverlay_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape) { e.Handled = true; AppLockUnlockPasswordBox.Focus(); }
	}

	private void AppLockClose_Click(object sender, RoutedEventArgs e) => Close();
	private void AppLockNow_Click(object sender, RoutedEventArgs e) => LockApplication();

	private void AppLockConfigure_Click(object sender, RoutedEventArgs e)
	{
		if (_applicationLocked || _shutdownStarted || _appLockFaulted) return;
		_appLockSetupChanging = _appLockConfigured;
		AppLockCurrentPasswordBox.Clear(); AppLockNewPasswordBox.Clear(); AppLockConfirmPasswordBox.Clear();
		AppLockCurrentCredentialPanel.Visibility = _appLockSetupChanging ? Visibility.Visible : Visibility.Collapsed;
		AppLockSetupRecoveryText.Visibility = _appLockSetupChanging ? Visibility.Visible : Visibility.Collapsed;
		AppLockDisableButton.Visibility = _appLockSetupChanging ? Visibility.Visible : Visibility.Collapsed;
		AppLockSetupErrorText.Visibility = Visibility.Collapsed;
		AppLockSetupOverlay.Visibility = Visibility.Visible;
		_ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
			(_appLockSetupChanging ? AppLockCurrentPasswordBox : AppLockNewPasswordBox).Focus()));
	}

	private void AppLockSetupCancel_Click(object sender, RoutedEventArgs e)
	{
		ClearAppLockSetup();
		AppLockSetupOverlay.Visibility = Visibility.Collapsed;
	}

	private void ClearAppLockSetup()
	{
		AppLockCurrentPasswordBox.Clear(); AppLockNewPasswordBox.Clear(); AppLockConfirmPasswordBox.Clear();
		AppLockSetupErrorText.Text = string.Empty;
		AppLockSetupErrorText.Visibility = Visibility.Collapsed;
	}

	private void AppLockSave_Click(object sender, RoutedEventArgs e)
	{
		if (_appLockStore is null || _shutdownStarted) return;
		if (AppLockNewPasswordBox.Password != AppLockConfirmPasswordBox.Password)
		{
			AppLockSetupErrorText.Text = UiText.Instance.Get("appLock.mismatch");
			AppLockSetupErrorText.Visibility = Visibility.Visible;
			return;
		}
		if (string.IsNullOrWhiteSpace(AppLockNewPasswordBox.Password) || AppLockNewPasswordBox.Password.Length is < 8 or > 256)
		{
			AppLockSetupErrorText.Text = UiText.Instance.Get("appLock.passphraseLength");
			AppLockSetupErrorText.Visibility = Visibility.Visible;
			return;
		}
		try
		{
			var changed = _appLockSetupChanging
				? _appLockStore.ChangePassphrase(AppLockCurrentPasswordBox.Password, AppLockNewPasswordBox.Password)
				: ConfigureAppLock(AppLockNewPasswordBox.Password);
			if (!changed)
			{
				AppLockSetupErrorText.Text = UiText.Instance.Get("appLock.invalid");
				AppLockSetupErrorText.Visibility = Visibility.Visible;
				return;
			}
			_appLockConfigured = true;
			_appLockIdleTimeoutMinutes = _appLockStore.IdleTimeoutMinutes;
			SelectAppLockIdleTimeout(_appLockIdleTimeoutMinutes);
			_lastApplicationInputUtc = DateTimeOffset.UtcNow;
			StatusText.Text = UiText.Instance.Get("appLock.saved");
			UpdateAppLockSettingsUi();
			ClearAppLockSetup();
			AppLockSetupOverlay.Visibility = Visibility.Collapsed;
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
		{
			AppLockSetupErrorText.Text = UiText.Instance.LocalizeMessage(ex.Message);
			AppLockSetupErrorText.Visibility = Visibility.Visible;
		}
	}

	private bool ConfigureAppLock(string passphrase)
	{
		if (passphrase.Length is < 8 or > 256) throw new ArgumentException("The app-lock passphrase must contain 8 to 256 characters.");
		_appLockStore!.Configure(passphrase, 5);
		return true;
	}

	private void AppLockDisable_Click(object sender, RoutedEventArgs e)
	{
		if (_appLockStore is null || !_appLockSetupChanging || _shutdownStarted) return;
		var current = AppLockCurrentPasswordBox.Password;
		if (MessageBox.Show(this, UiText.Instance.Get("appLock.disableConfirm"), UiText.Instance.Get("appLock.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
		if (!_appLockStore.Remove(current))
		{
			AppLockSetupErrorText.Text = UiText.Instance.Get("appLock.invalid");
			AppLockSetupErrorText.Visibility = Visibility.Visible;
			return;
		}
		_appLockConfigured = false;
		_appLockIdleTimeoutMinutes = 0;
		SelectAppLockIdleTimeout(0);
		StatusText.Text = UiText.Instance.Get("appLock.disabled");
		UpdateAppLockSettingsUi();
		ClearAppLockSetup();
		AppLockSetupOverlay.Visibility = Visibility.Collapsed;
	}

	private void AppLockIdleTimeoutBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loadingAppLockControls || !_appLockConfigured || _appLockStore is null || AppLockIdleTimeoutBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out var minutes)) return;
		try
		{
			_appLockStore.SetIdleTimeout(minutes);
			_appLockIdleTimeoutMinutes = minutes;
			_lastApplicationInputUtc = DateTimeOffset.UtcNow;
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
		{
			SelectAppLockIdleTimeout(_appLockIdleTimeoutMinutes);
			StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message);
		}
	}

	private void SelectAppLockIdleTimeout(int minutes)
	{
		_loadingAppLockControls = true;
		foreach (ComboBoxItem item in AppLockIdleTimeoutBox.Items)
			if (item.Tag is string tag && int.TryParse(tag, out var value) && value == minutes) { AppLockIdleTimeoutBox.SelectedItem = item; break; }
		_loadingAppLockControls = false;
	}

	private void UiText_LanguageChanged(object? sender, EventArgs e)
	{
		UpdatePageTitle();
		UpdateLocalDatabaseSecurityStatus();
		UpdateAppLockSettingsUi();
		UpdateLastSyncText(_lastSyncAtUtc);
		CancelOperationButton.Content = UiText.Instance.Get(_cancelButtonTextKey);
		BuildFolderTree(_manifestItems.Select((ManifestItem item) => item.Manifest).ToArray());
		ApplyManifestFilterAndSort();
		ApplyActionAvailability();
		foreach (QueueItemView value in _queueViews.Values)
		{
			value.RefreshLocalization();
		}
		if (TransferList?.ItemsSource is ICollectionView collectionView)
		{
			collectionView.Refresh();
		}
		if (AuthenticationFlowPanel.Visibility == Visibility.Visible && !string.IsNullOrEmpty(_authenticationFlowState))
		{
			PresentAuthenticationFlow(_authenticationFlowState, null, preserveInput: true);
		}
	}

	private void KeepLogin_Changed(object sender, RoutedEventArgs e)
	{
		if (_loadingUiPreferences || _syncingKeepLoginControls)
		{
			return;
		}
		_keepLogin = sender is CheckBox { IsChecked: var isChecked } && isChecked == true;
		_syncingKeepLoginControls = true;
		KeepLoginSettingsCheckBox.IsChecked = _keepLogin;
		KeepLoginCheckBox.IsChecked = _keepLogin;
		_syncingKeepLoginControls = false;
		SaveUiPreferences();
		try
		{
			TelegramCredentialStore telegramCredentialStore = CreateCredentialStore();
			if (_keepLogin)
			{
				if (_telegramAppCredentials != null)
				{
					telegramCredentialStore.Save(_telegramAppCredentials);
				}
				if (_telegramSessionDirectory != null)
				{
					if (_activeTelegramAccountId != null && TelegramSessionProfileStore.IsAccountProfileDirectory(LocalDataRoot, _telegramSessionDirectory))
					{
						TelegramSessionProfileStore.PersistCurrentSession(LocalDataRoot, _activeTelegramAccountId, _telegramSessionDirectory);
					}
					else if (Directory.Exists(_telegramSessionDirectory))
					{
						TelegramSessionProfileStore.PersistSignInSession(LocalDataRoot, _telegramSessionDirectory);
					}
				}
			}
			else
			{
				if (_activeTelegramAccountId != null)
				{
					TelegramSessionProfileStore.ClearCurrentSessionPointer(LocalDataRoot, _activeTelegramAccountId);
				}
				if (_telegramSessionDirectory != null)
				{
					if (_telegramSessionDirectory is not null) TelegramSessionProfileStore.ClearSignInSessionPointer(LocalDataRoot, _telegramSessionDirectory);
				}
			}
		}
		catch (Exception ex)
		{
			StatusText.Text = UiText.Instance.LocalizeMessage("Could not update saved sign-in settings: " + ex.Message);
		}
	}

	private void DarkMode_Changed(object sender, RoutedEventArgs e)
	{
		if (!_loadingUiPreferences)
		{
			_darkMode = DarkModeCheckBox.IsChecked == true;
			ApplyTheme(_darkMode);
			SaveUiPreferences();
		}
	}

	private void SaveUiPreferences()
	{
		try
		{
			_uiPreferencesStore?.Save(_keepLogin, _darkMode, _explorerIcons);
		}
		catch (Exception ex)
		{
			StatusText.Text = UiText.Instance.LocalizeMessage("Could not save interface preferences: " + ex.Message);
		}
	}

	private void ApplyTheme(bool darkMode)
	{
		foreach (var (key, value) in darkMode ? new Dictionary<string, string>
		{
			["SidebarBg"] = "#111827",
			["SidebarHover"] = "#1F2937",
			["ContentBg"] = "#0B1220",
			["ContentBorder"] = "#293548",
			["TextPrimary"] = "#F3F4F6",
			["TextSecondary"] = "#C1CBD8",
			["TextMuted"] = "#9AA9BC",
			["TextOnDark"] = "#F3F4F6",
			["TextOnDarkMuted"] = "#C1CBD8",
			["AccentBlue"] = "#5593FF",
			["OnAccent"] = "#061429",
			["ErrorRed"] = "#FF8078",
			["SurfaceContainer"] = "#121C2B",
			["SelectionBackground"] = "#213A60",
			["ProgressTrack"] = "#2A3648",
			["AccentHover"] = "#8AB2FF",
			["AvatarBackground"] = "#1F2C3E",
			["DangerBorder"] = "#583633",
			["ModalBackdrop"] = "#B3000000"
		} : new Dictionary<string, string>
		{
			["SidebarBg"] = "#F3F3FA",
			["SidebarHover"] = "#E8E7EE",
			["ContentBg"] = "#F9F9FF",
			["ContentBorder"] = "#C5C6D0",
			["TextPrimary"] = "#1A1B20",
			["TextSecondary"] = "#44464F",
			["TextMuted"] = "#666873",
			["TextOnDark"] = "#001945",
			["TextOnDarkMuted"] = "#44464F",
			["AccentBlue"] = "#1B6EF3",
			["OnAccent"] = "#FFFFFF",
			["ErrorRed"] = "#BA1A1A",
			["SurfaceContainer"] = "#FFFFFF",
			["SelectionBackground"] = "#D9E2FF",
			["ProgressTrack"] = "#E1E2EC",
			["AccentHover"] = "#1558C0",
			["AvatarBackground"] = "#E8E7EE",
			["DangerBorder"] = "#FFDAD6",
			["ModalBackdrop"] = "#661A1B20"
		})
		{
			if (Resources[key] is SolidColorBrush solidColorBrush && ColorConverter.ConvertFromString(value) is Color color)
			{
				if (solidColorBrush.IsFrozen)
				{
					Resources[key] = new SolidColorBrush(color);
				}
				else
				{
					solidColorBrush.Color = color;
				}
			}
		}
	}

	private void TelegramLogin_Click(object sender, RoutedEventArgs e)
	{
		var telegramSession = _telegramSession;
		if (telegramSession != null)
		{
			PresentAuthenticationFlow(telegramSession.CurrentAuthorizationState, null, preserveInput: true);
			return;
		}
		try
		{
			TelegramCredentialStore telegramCredentialStore = CreateCredentialStore();
			var telegramAppCredentials = _telegramAppCredentials ?? telegramCredentialStore.Load() ?? RequestAppCredentials();
			if (telegramAppCredentials is not null)
			{
				_telegramAppCredentials = telegramAppCredentials;
				telegramCredentialStore.Save(telegramAppCredentials);
				StartTelegramSession(telegramAppCredentials, TelegramSessionProfileStore.CreateSignInDirectory(LocalDataRoot));
				if (!_keepLogin)
				{
					if (_telegramSessionDirectory is not null) TelegramSessionProfileStore.ClearSignInSessionPointer(LocalDataRoot, _telegramSessionDirectory);
				}
			}
		}
		catch (Exception ex)
		{
			_telegramSession = null;
			_telegramAccountLoaded = false;
			ApplyActionAvailability();
			string english = ((ex is DllNotFoundException || ex is BadImageFormatException) ? (UiText.Instance.Get("error.nativePrerequisite") + "\n\n" + ex.Message) : ex.ToString());
			MessageBox.Show(this, UiText.Instance.LocalizeMessage(english), UiText.Instance.LocalizeMessage("Telegram login could not start"), MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private TelegramCredentialStore CreateCredentialStore()
	{
		return new TelegramCredentialStore(Path.Combine(LocalDataRoot, "telegram-app-credentials.bin"));
	}

	private void StartTelegramSession(TelegramAppCredentials credentials, string sessionDirectory)
	{
		if (_telegramSession != null)
		{
			return;
		}
		ShowStorageConnectionFeedback(null);
		_vaultProfileSetupFailed = false;
		_activeTelegramAccountId = null;
		_telegramAppCredentials = credentials;
		_explicitLogoutRequested = false;
		_telegramAccountLoaded = false;
		_telegramSessionDirectory = Path.GetFullPath(sessionDirectory);
		TelegramDatabaseKey telegramDatabaseKey = TelegramDatabaseKeyStore.LoadOrCreate(_telegramSessionDirectory);
		_localDatabaseEncryptionEnabled = telegramDatabaseKey.IsProtected;
		UpdateLocalDatabaseSecurityStatus();
		TelegramAuthSession telegramAuthSession = (_telegramSession = new TelegramAuthSession(Path.Combine(AppContext.BaseDirectory, "tdjson.dll"), _telegramSessionDirectory, credentials, telegramDatabaseKey.Value));
		telegramAuthSession.StatusChanged += TelegramStatusChanged;
		telegramAuthSession.AuthenticationInputRejected += TelegramAuthenticationInputRejected;
		telegramAuthSession.AuthenticationInputPendingChanged += TelegramAuthenticationInputPendingChanged;
		telegramAuthSession.AuthorizationStateChanged += TelegramAuthorizationStateChanged;
		telegramAuthSession.AccountReceived += TelegramAccountReceived;
		TelegramLoginButton.IsEnabled = false;
		try
		{
			telegramAuthSession.Start();
			ApplyActionAvailability();
			StatusText.Text = "Connecting to Telegram...";
		}
		catch
		{
			_telegramSession = null;
			_telegramSessionDirectory = null;
			TelegramLoginButton.IsEnabled = true;
			telegramAuthSession.DisposeAsync();
			throw;
		}
	}

	private async Task SwitchToIsolatedSignInSessionAsync(TelegramAuthSession previousSession)
	{
		if (_switchingTelegramSessionDirectory || _telegramSession != previousSession)
		{
			return;
		}
		var credentials = _telegramAppCredentials ?? CreateCredentialStore().Load();
		if (credentials is null)
		{
			return;
		}
		_switchingTelegramSessionDirectory = true;
		try
		{
			_telegramSession = null;
			_telegramAccountLoaded = false;
			_activeTelegramAccountId = null;
			UseSharedWorkspace();
			await previousSession.DisposeAsync();
			_telegramSessionDirectory = null;
			_lastPromptedAuthorizationState = null;
			StatusText.Text = "Starting an isolated Telegram sign-in session...";
			StartTelegramSession(credentials, TelegramSessionProfileStore.CreateSignInDirectory(LocalDataRoot));
			if (!_keepLogin)
			{
				if (_telegramSessionDirectory is not null) TelegramSessionProfileStore.ClearSignInSessionPointer(LocalDataRoot, _telegramSessionDirectory);
			}
			await RefreshManifestsAsync();
		}
		finally
		{
			_switchingTelegramSessionDirectory = false;
		}
	}

	private async Task MoveTelegramSessionIntoAccountProfileAsync(string accountId)
	{
		var sessionDirectory = _telegramSessionDirectory;
		if (string.IsNullOrWhiteSpace(sessionDirectory))
		{
			throw new InvalidOperationException("The active TDLib session directory is unavailable.");
		}
		if (TelegramSessionProfileStore.IsAccountProfileDirectory(LocalDataRoot, sessionDirectory))
		{
			if (_keepLogin)
			{
				TelegramSessionProfileStore.PersistCurrentSession(LocalDataRoot, accountId, sessionDirectory);
			}
			else
			{
				TelegramSessionProfileStore.ClearCurrentSessionPointer(LocalDataRoot, accountId);
			}
			return;
		}
		TelegramAppCredentials credentials = _telegramAppCredentials ?? CreateCredentialStore().Load() ?? throw new InvalidOperationException("Telegram application credentials are unavailable for session migration.");
		TelegramAuthSession telegramAuthSession = _telegramSession ?? throw new InvalidOperationException("The active Telegram session is unavailable.");
		_switchingTelegramSessionDirectory = true;
		try
		{
			_telegramSession = null;
			await telegramAuthSession.DisposeAsync();
			StartTelegramSession(credentials, _telegramSessionDirectory = TelegramSessionProfileStore.MoveToAccountProfile(LocalDataRoot, accountId, sessionDirectory, _keepLogin));
		}
		finally
		{
			_switchingTelegramSessionDirectory = false;
		}
	}

	private void TelegramStatusChanged(object? sender, string state)
	{
		Dispatcher.BeginInvoke((Action)(() =>
		{
			StatusText.Text = (state.StartsWith("authorizationState", StringComparison.Ordinal) ? DescribeAuthorizationState(state) : ("Telegram authorization: " + state));
			ApplyActionAvailability();
			var telegramSession = _telegramSession;
			try
			{
				string text = state;
				if (text != null)
				{
					switch (text.Length)
					{
					case 33:
						if (text == "authorizationStateWaitPhoneNumber")
						{
							if (telegramSession != null && !_switchingTelegramSessionDirectory)
							{
								var telegramSessionDirectory = _telegramSessionDirectory;
								if (telegramSessionDirectory != null && TelegramSessionProfileStore.IsAccountProfileDirectory(LocalDataRoot, telegramSessionDirectory))
								{
									ObserveBackgroundTask(SwitchToIsolatedSignInSessionAsync(telegramSession));
									break;
								}
							}
							if (telegramSession != null)
							{
								if (telegramSession.AuthenticationInputPending)
								{
									_deferredAuthorizationState = state;
								}
								else if (_lastPromptedAuthorizationState != state)
								{
									PresentAuthenticationFlow(state);
								}
							}
						}
						break;
					case 26:
						if (!(text == "authorizationStateWaitCode"))
						{
							break;
						}
						goto IL_024d;
					case 30:
						if (!(text == "authorizationStateWaitPassword"))
						{
							break;
						}
						goto IL_024d;
					case 34:
						if (!(text == "authorizationStateWaitEmailAddress"))
						{
							break;
						}
						goto IL_024d;
					case 31:
						if (!(text == "authorizationStateWaitEmailCode"))
						{
							break;
						}
						goto IL_024d;
					case 23:
						if (text == "authorizationStateReady")
						{
							CloseAuthenticationFlowPanel();
							_authenticationFlowState = string.Empty;
							TelegramAccountText.Text = "Connected; loading account...";
						}
						break;
					case 24:
						if (text == "authorizationStateClosed")
						{
							CloseAuthenticationFlowPanel();
							_authenticationFlowState = string.Empty;
							_lastPromptedAuthorizationState = null;
							if (!_switchingTelegramSessionDirectory)
							{
								ObserveBackgroundTask(DisposeTelegramSessionAsync());
							}
						}
						break;
					case 25:
					case 27:
					case 28:
					case 29:
					case 32:
						break;
						IL_024d:
						if (telegramSession != null)
						{
							if (telegramSession.AuthenticationInputPending)
							{
								_deferredAuthorizationState = state;
							}
							else if (_lastPromptedAuthorizationState != state)
							{
								PresentAuthenticationFlow(state);
							}
						}
						break;
					}
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, UiText.Instance.LocalizeMessage(ex.Message), UiText.Instance.Get("auth.telegramAuthorizationTitle"), MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}));
	}

	private async void ObserveBackgroundTask(Task task)
	{
		try
		{
			await task;
		}
		catch (Exception ex)
		{
			if (!_shutdownStarted)
			{
				StatusText.Text = UiText.Instance.LocalizeMessage("Telegram authorization: " + ex.Message);
				MessageBox.Show(this, UiText.Instance.LocalizeMessage(ex.Message), UiText.Instance.Get("auth.telegramAuthorizationTitle"), MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private void TelegramAuthenticationInputRejected(object? sender, string message)
	{
		Dispatcher.BeginInvoke((Action)(() =>
		{
			_authenticationInputError = message;
			StatusText.Text = "Telegram authorization: " + message;
		}));
	}

	private void TelegramAuthenticationInputPendingChanged(object? sender, bool pending)
	{
		Dispatcher.BeginInvoke((Action)(() =>
		{
			ApplyActionAvailability();
			var telegramSession = _telegramSession;
			if (telegramSession != null)
			{
				if (pending)
				{
					LocalizedTextBlock statusText = StatusText;
					statusText.Text = telegramSession.CurrentAuthorizationState switch
					{
						"authorizationStateWaitPhoneNumber" => "Sending phone number to Telegram...", 
						"authorizationStateWaitCode" => "Checking the Telegram login code...", 
						"authorizationStateWaitPassword" => "Checking the two-step verification password...", 
						"authorizationStateWaitEmailAddress" => "Sending the email address to Telegram...", 
						"authorizationStateWaitEmailCode" => "Checking the email verification code...", 
						_ => "Waiting for Telegram...", 
					};
					AuthInputTextBox.IsEnabled = false;
					AuthPasswordBox.IsEnabled = false;
					AuthLastNameTextBox.IsEnabled = false;
					AuthContinueButton.IsEnabled = false;
					AuthCancelButton.IsEnabled = false;
				}
				else
				{
					AuthInputTextBox.IsEnabled = true;
					AuthPasswordBox.IsEnabled = true;
					AuthLastNameTextBox.IsEnabled = true;
					AuthCancelButton.IsEnabled = true;
					var authenticationInputError = _authenticationInputError;
					if (authenticationInputError != null)
					{
						_authenticationInputError = null;
						_lastPromptedAuthorizationState = null;
						PresentAuthenticationFlow(telegramSession.CurrentAuthorizationState, DisplayAuthenticationError(authenticationInputError));
					}
					else
					{
						var deferredAuthorizationState = _deferredAuthorizationState;
						if (deferredAuthorizationState != null && telegramSession.CurrentAuthorizationState == deferredAuthorizationState)
						{
							_deferredAuthorizationState = null;
							PresentAuthenticationFlow(deferredAuthorizationState);
						}
					}
				}
			}
		}));
	}

	private void PresentAuthenticationFlow(string state, string? error = null, bool preserveInput = false)
	{
		bool flag;
		switch (state)
		{
		case "authorizationStateWaitEmailAddress":
		case "authorizationStateWaitRegistration":
		case "authorizationStateWaitPhoneNumber":
		case "authorizationStateWaitCode":
		case "authorizationStateWaitPassword":
		case "authorizationStateWaitEmailCode":
		case "authorizationStateWaitOtherDeviceConfirmation":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return;
		}
		bool flag2 = !string.Equals(_authenticationFlowState, state, StringComparison.Ordinal);
		_authenticationFlowState = state;
		_lastPromptedAuthorizationState = state;
		if (flag2 || !preserveInput)
		{
			AuthInputTextBox.Clear();
			AuthPasswordBox.Clear();
			AuthLastNameTextBox.Clear();
		}
		AuthInputTextBox.Visibility = Visibility.Collapsed;
		AuthPasswordBox.Visibility = Visibility.Collapsed;
		AuthLastNameLabel.Visibility = Visibility.Collapsed;
		AuthLastNameTextBox.Visibility = Visibility.Collapsed;
		AuthHelpText.Text = string.Empty;
		AuthContinueButton.Visibility = Visibility.Visible;
		AuthContinueButton.Content = UiText.Instance.Get("dialog.continue");
		AuthCancelButton.Content = UiText.Instance.Get("dialog.cancel");
		TextBlock authStepText = AuthStepText;
		UiText instance = UiText.Instance;
		TextBlock textBlock = authStepText;
		UiText uiText = instance;
		textBlock.Text = uiText.Get(state switch
		{
			"authorizationStateWaitPhoneNumber" => "auth.phone", 
			"authorizationStateWaitCode" => "auth.code", 
			"authorizationStateWaitPassword" => "auth.password", 
			"authorizationStateWaitEmailAddress" => "auth.email", 
			"authorizationStateWaitEmailCode" => "auth.emailCode", 
			"authorizationStateWaitRegistration" => "auth.firstName", 
			_ => "auth.step.waitDevice", 
		});
		switch (state)
		{
		case "authorizationStateWaitPhoneNumber":
			ConfigureAuthenticationTextInput("auth.phone");
			AuthHelpText.Text = UiText.Instance.Get("auth.step.phone");
			break;
		case "authorizationStateWaitCode":
			ConfigureAuthenticationTextInput("auth.code");
			AuthHelpText.Text = UiText.Instance.Get("auth.step.code") + Environment.NewLine + UiText.Instance.Get("auth.codeHelp");
			break;
		case "authorizationStateWaitPassword":
			AuthHelpText.Text = UiText.Instance.Get("auth.step.password");
			AuthPasswordBox.Visibility = Visibility.Visible;
			AutomationProperties.SetName(AuthPasswordBox, UiText.Instance.Get("auth.password"));
			break;
		case "authorizationStateWaitEmailAddress":
			ConfigureAuthenticationTextInput("auth.email");
			AuthHelpText.Text = UiText.Instance.Get("auth.step.email");
			break;
		case "authorizationStateWaitEmailCode":
			ConfigureAuthenticationTextInput("auth.emailCode");
			AuthHelpText.Text = UiText.Instance.Get("auth.step.emailCode");
			break;
		case "authorizationStateWaitRegistration":
			ConfigureAuthenticationTextInput("auth.firstName");
			AuthHelpText.Text = UiText.Instance.Get("auth.step.registration");
			AuthLastNameLabel.Text = UiText.Instance.Get("auth.lastNameLabel");
			AuthLastNameLabel.Visibility = Visibility.Visible;
			AuthLastNameTextBox.Visibility = Visibility.Visible;
			AutomationProperties.SetName(AuthLastNameTextBox, UiText.Instance.Get("auth.lastNameLabel"));
			break;
		case "authorizationStateWaitOtherDeviceConfirmation":
			AuthContinueButton.Visibility = Visibility.Collapsed;
			break;
		}
		AuthErrorText.Text = error ?? string.Empty;
		AuthErrorText.Visibility = (string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible);
		if (AuthenticationFlowPanel.Visibility != Visibility.Visible)
		{
			_authenticationReturnFocus = Keyboard.FocusedElement;
		}
		AuthenticationFlowPanel.Visibility = Visibility.Visible;
		RefreshAuthenticationContinueAvailability();
		if (!(!preserveInput | flag2))
		{
			return;
		}
		Dispatcher.BeginInvoke((Func<bool>)(() =>
		{
			IInputElement inputElement;
			if (AuthInputTextBox.Visibility != Visibility.Visible)
			{
				if (AuthPasswordBox.Visibility != Visibility.Visible)
				{
					IInputElement authCancelButton = AuthCancelButton;
					inputElement = authCancelButton;
				}
				else
				{
					IInputElement authCancelButton = AuthPasswordBox;
					inputElement = authCancelButton;
				}
			}
			else
			{
				IInputElement authCancelButton = AuthInputTextBox;
				inputElement = authCancelButton;
			}
			return inputElement.Focus();
		}));
	}

	private void ConfigureAuthenticationTextInput(string key)
	{
		AuthInputTextBox.Visibility = Visibility.Visible;
		AutomationProperties.SetName(AuthInputTextBox, UiText.Instance.Get(key));
	}

	private static string DisplayAuthenticationError(string error)
	{
		if (error.Contains("PHONE_NUMBER_INVALID", StringComparison.OrdinalIgnoreCase))
		{
			return UiText.Instance.Get("auth.error.phone");
		}
		if (error.Contains("PHONE_CODE_INVALID", StringComparison.OrdinalIgnoreCase) || error.Contains("PHONE_CODE_EXPIRED", StringComparison.OrdinalIgnoreCase))
		{
			return UiText.Instance.Get("auth.error.code");
		}
		if (error.Contains("PASSWORD_HASH_INVALID", StringComparison.OrdinalIgnoreCase))
		{
			return UiText.Instance.Get("auth.error.password");
		}
		if (error.Contains("EMAIL_ADDRESS_INVALID", StringComparison.OrdinalIgnoreCase))
		{
			return UiText.Instance.Get("auth.error.email");
		}
		if (error.Contains("EMAIL_CODE_INVALID", StringComparison.OrdinalIgnoreCase) || error.Contains("EMAIL_CODE_EXPIRED", StringComparison.OrdinalIgnoreCase))
		{
			return UiText.Instance.Get("auth.error.emailCode");
		}
		return UiText.Instance.LocalizeMessage(error);
	}

	private void RefreshAuthenticationContinueAvailability()
	{
		if (AuthContinueButton != null)
		{
			string authenticationFlowState = _authenticationFlowState;
			bool num = ((authenticationFlowState == "authorizationStateWaitPassword") ? (!string.IsNullOrWhiteSpace(AuthPasswordBox.Password)) : (!(authenticationFlowState == "authorizationStateWaitOtherDeviceConfirmation") && !string.IsNullOrWhiteSpace(AuthInputTextBox.Text)));
			Button authContinueButton = AuthContinueButton;
			int num2;
			if (num)
			{
				var telegramSession = _telegramSession;
				num2 = ((telegramSession == null || !telegramSession.AuthenticationInputPending) ? 1 : 0);
			}
			else
			{
				num2 = 0;
			}
			authContinueButton.IsEnabled = (byte)num2 != 0;
		}
	}

	private void AuthenticationInput_TextChanged(object sender, TextChangedEventArgs e)
	{
		RefreshAuthenticationContinueAvailability();
	}

	private void AuthenticationPassword_Changed(object sender, RoutedEventArgs e)
	{
		RefreshAuthenticationContinueAvailability();
	}

	private void AuthenticationInput_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return && AuthContinueButton.IsEnabled)
		{
			AuthenticationContinue_Click(sender, new RoutedEventArgs());
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			AuthenticationCancel_Click(sender, new RoutedEventArgs());
			e.Handled = true;
		}
	}

	private void AuthenticationPassword_KeyDown(object sender, KeyEventArgs e)
	{
		AuthenticationInput_KeyDown(sender, e);
	}

	private void AuthenticationFlowPanel_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			AuthenticationCancel_Click(sender, new RoutedEventArgs());
			e.Handled = true;
		}
	}

	private void CloseAuthenticationFlowPanel()
	{
		AuthenticationFlowPanel.Visibility = Visibility.Collapsed;
		var returnFocus = _authenticationReturnFocus;
		_authenticationReturnFocus = null;
		Dispatcher.BeginInvoke((Action)(() =>
		{
			if (returnFocus == null || Keyboard.Focus(returnFocus) == null)
			{
				TelegramLoginButton.Focus();
			}
		}));
	}

	private void AuthenticationContinue_Click(object sender, RoutedEventArgs e)
	{
		var telegramSession = _telegramSession;
		if (telegramSession == null || telegramSession.AuthenticationInputPending)
		{
			return;
		}
		string text = AuthInputTextBox.Text.Trim();
		try
		{
			_deferredAuthorizationState = null;
			_authenticationInputError = null;
			AuthErrorText.Visibility = Visibility.Collapsed;
			switch (_authenticationFlowState)
			{
			default:
				return;
			case "authorizationStateWaitPhoneNumber":
				telegramSession.SubmitPhoneNumber(text);
				break;
			case "authorizationStateWaitCode":
				telegramSession.SubmitCode(text);
				break;
			case "authorizationStateWaitPassword":
				telegramSession.SubmitPassword(AuthPasswordBox.Password);
				AuthPasswordBox.Clear();
				break;
			case "authorizationStateWaitEmailAddress":
				telegramSession.SubmitEmailAddress(text);
				break;
			case "authorizationStateWaitEmailCode":
				telegramSession.SubmitEmailCode(text);
				break;
			case "authorizationStateWaitRegistration":
				telegramSession.RegisterUser(text, AuthLastNameTextBox.Text.Trim());
				AuthInputTextBox.Clear();
				AuthLastNameTextBox.Clear();
				break;
			}
			AuthInputTextBox.Clear();
			AuthContinueButton.IsEnabled = false;
		}
		catch (Exception ex)
		{
			AuthErrorText.Text = UiText.Instance.LocalizeMessage(ex.Message);
			AuthErrorText.Visibility = Visibility.Visible;
		}
	}

	private void AuthenticationCancel_Click(object sender, RoutedEventArgs e)
	{
		if (_telegramSession != null && !_telegramSession.AuthenticationInputPending)
		{
			StatusText.Text = UiText.Instance.Get("auth.canceling");
			AuthCancelButton.IsEnabled = false;
			_telegramSession.LogOut();
		}
	}

	private static string DescribeAuthorizationState(string state)
	{
		return state switch
		{
			"authorizationStateWaitPhoneNumber" => "Telegram sign-in: enter your phone number.", 
			"authorizationStateWaitCode" => "Telegram sign-in: enter the login code from your Telegram app.", 
			"authorizationStateWaitPassword" => "Telegram sign-in: enter your 2-step verification password.", 
			"authorizationStateWaitEmailAddress" => "Telegram sign-in: enter the requested email address.", 
			"authorizationStateWaitEmailCode" => "Telegram sign-in: enter the email verification code.", 
			"authorizationStateReady" => "Telegram authorization succeeded.", 
			_ => "Telegram authorization: " + state, 
		};
	}

	private void TelegramAuthorizationStateChanged(object? sender, JsonObject state)
	{
		Dispatcher.BeginInvoke((Action)(() =>
		{
			ApplyActionAvailability();
			var text = state["@type"]?.GetValue<string>();
			var telegramSession = _telegramSession;
			try
			{
				switch (text)
				{
				case "authorizationStateWaitRegistration":
				{
					string text3 = state["terms_of_service"]?["text"]?["text"]?.GetValue<string>() ?? "Telegram terms of service";
					if (MessageBox.Show(this, text3 + "\n\n" + UiText.Instance.Get("dialog.termsAccept"), UiText.Instance.LocalizeMessage("Telegram terms"), MessageBoxButton.YesNo, MessageBoxImage.Asterisk) != MessageBoxResult.Yes)
					{
						telegramSession?.LogOut();
					}
					else if (telegramSession != null)
					{
						PresentAuthenticationFlow("authorizationStateWaitRegistration");
					}
					break;
				}
				case "authorizationStateWaitOtherDeviceConfirmation":
				{
					string text2 = state["link"]?.GetValue<string>() ?? "";
					PresentAuthenticationFlow(text);
					AuthHelpText.Text = text2;
					break;
				}
				case "authorizationStateWaitEmailCode":
					break;
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, UiText.Instance.LocalizeMessage(ex.Message), UiText.Instance.Get("auth.telegramAuthorizationTitle"), MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}));
	}

	private void TelegramAccountReceived(object? sender, JsonObject account)
	{
		Dispatcher.BeginInvoke((Func<Task>)(async () =>
		{
			string first = account["first_name"]?.GetValue<string>() ?? "";
			string last = account["last_name"]?.GetValue<string>() ?? "";
			bool isPremium = account["is_premium"]?.GetValue<bool>() ?? false;
			var accountId = account["id"]?.GetValue<long>().ToString(CultureInfo.InvariantCulture);
			if (string.IsNullOrWhiteSpace(accountId))
			{
				_activeTelegramAccountId = null;
				_telegramAccountLoaded = false;
				_observedRemoteManifestIds.Clear();
				TelegramAccountText.Text = "Account identity unavailable";
				StatusText.Text = "TDLib did not provide an account ID. Account storage remains unavailable.";
				ApplyActionAvailability();
			}
			else
			{
				if (!string.Equals(_activeTelegramAccountId, accountId, StringComparison.Ordinal))
				{
					_legacyLocalDataView = false;
					_legacyViewReturnChannel = null;
					_pendingLegacyIsolationChannel = null;
					RestoreLegacyLocalViewControls();
					_storageChannel = null;
					_observedRemoteManifestIds.Clear();
					_currentFolderPath = string.Empty;
					UpdateLastSyncText(null);
					try
					{
						_vaultProfileSetupFailed = false;
						await ActivateAccountProfileAsync(accountId);
						await MoveTelegramSessionIntoAccountProfileAsync(accountId);
					}
					catch (Exception ex)
					{
						_vaultProfileSetupFailed = true;
						_telegramAccountLoaded = false;
						TelegramAccountText.Text = "Telegram account recognized; profile setup needs attention (ID " + accountId + ")";
						StatusText.Text = "The account profile could not be fully opened. Local records were preserved or copied for recovery: " + UiText.Instance.LocalizeMessage(ex.Message);
						ApplyActionAvailability();
						return;
					}
				}
				_activeTelegramAccountId = accountId;
				_vaultProfileSetupFailed = false;
				_telegramAccountLoaded = true;
				string text = new string((from namePart in new string[2] { first, last }
					where !string.IsNullOrWhiteSpace(namePart)
					select namePart.Trim()[0]).Take(2).ToArray()).ToUpperInvariant();
				if (!string.IsNullOrEmpty(text))
				{
					AccountInitial.Text = text;
					AccountInitial.Visibility = Visibility.Visible;
					AccountGlyph.Visibility = Visibility.Collapsed;
				}
				TelegramAccountText.Text = $"Connected: {first} {last} ({(isPremium ? "Premium" : "Standard")}; ID {accountId})".Trim();
				ApplyActionAvailability();
				StatusText.Text = ((_telegramSession?.CurrentAuthorizationState == "authorizationStateReady") ? string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.loginSucceeded"), 2000000000L) : "Reopening Telegram from this account's isolated session...");
				try
				{
					await RefreshManifestsAsync();
				}
				catch (Exception ex2)
				{
					StatusText.Text = "Telegram connected, but the account's local catalog could not be refreshed: " + ex2.Message;
				}
				await ConnectPrivateStorageAsync();
			}
		}));
	}

	private void TelegramLogout_Click(object sender, RoutedEventArgs e)
	{
		KeepLoginCheckBox.IsChecked = false;
		_explicitLogoutRequested = true;
		if (!_keepLogin && _activeTelegramAccountId != null)
		{
			TelegramSessionProfileStore.ClearCurrentSessionPointer(LocalDataRoot, _activeTelegramAccountId);
		}
		_telegramSession?.LogOut();
		TelegramAccountText.Text = "Logging out...";
	}

	private void UpdateLocalDatabaseSecurityStatus()
	{
		if (LocalDatabaseSecurityStatus != null)
		{
			TextBlock localDatabaseSecurityStatus = LocalDatabaseSecurityStatus;
			bool? localDatabaseEncryptionEnabled = _localDatabaseEncryptionEnabled;
			string text;
			if (localDatabaseEncryptionEnabled.HasValue)
			{
				text = ((localDatabaseEncryptionEnabled != true) ? UiText.Instance.Get("security.tdlibDatabaseLegacy") : UiText.Instance.Get("security.tdlibDatabaseProtected"));
			}
			else
			{
				text = UiText.Instance.Get("security.tdlibDatabaseUnavailable");
			}
			localDatabaseSecurityStatus.Text = text;
		}
	}

	private async Task DisposeTelegramSessionAsync()
	{
		ShowStorageConnectionFeedback(null);
		var telegramSession = _telegramSession;
		var sessionDirectory = _telegramSessionDirectory;
		var accountId = _activeTelegramAccountId;
		_telegramSession = null;
		_telegramAccountLoaded = false;
		_storageChannel = null;
		_observedRemoteManifestIds.Clear();
		_activeTelegramAccountId = null;
		_localDatabaseEncryptionEnabled = null;
		UpdateLocalDatabaseSecurityStatus();
		UseSharedWorkspace();
		ApplyActionAvailability();
		if (telegramSession != null)
		{
			await telegramSession.DisposeAsync();
		}
		if (sessionDirectory != null && (!_keepLogin || _explicitLogoutRequested))
		{
			try
			{
				TelegramSessionProfileStore.DiscardSessionDirectory(LocalDataRoot, accountId, sessionDirectory);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? true : false)
			{
				StatusText.Text = "The Telegram session could not be removed from this device: " + ex.Message;
			}
			_telegramSessionDirectory = null;
		}
		await Dispatcher.BeginInvoke((Action)(() =>
		{
			ApplyActionAvailability();
			TelegramAccountText.Text = "Not connected";
			AccountInitial.Visibility = Visibility.Collapsed;
			AccountGlyph.Visibility = Visibility.Visible;
		})).Task;
		if (!_shutdownStarted)
		{
			try
			{
				await RefreshManifestsAsync();
			}
			catch (Exception ex2)
			{
				StatusText.Text = "Could not refresh the local-only catalog after logout: " + ex2.Message;
			}
		}
	}

	private TelegramAppCredentials? RequestAppCredentials()
	{
		Window dialog = new Window
		{
			Title = UiText.Instance.Get("auth.apiSetupTitle"),
			Owner = this,
			Width = 440.0,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			Background = (Brush)FindResource("ContentBg"),
			Foreground = (Brush)FindResource("TextPrimary"),
			FontFamily = FontFamily
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(28.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = UiText.Instance.Get("auth.apiSetupTitle"),
			FontSize = 22.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = UiText.Instance.Get("auth.apiSetupDescription"),
			Foreground = (Brush)FindResource("TextSecondary"),
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 22.0)
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = UiText.Instance.Get("auth.apiId"),
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		});
		TextBox apiIdInput = new TextBox
		{
			MinHeight = 42.0,
			VerticalContentAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		};
		stackPanel.Children.Add(apiIdInput);
		stackPanel.Children.Add(new TextBlock
		{
			Text = UiText.Instance.Get("auth.apiHash"),
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		});
		PasswordBox apiHashInput = new PasswordBox
		{
			MinHeight = 42.0,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		stackPanel.Children.Add(apiHashInput);
		stackPanel.Children.Add(new TextBlock
		{
			Text = UiText.Instance.Get("auth.apiSetupStorageNote"),
			Foreground = (Brush)FindResource("TextSecondary"),
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 10.0, 0.0, 22.0)
		});
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		Button element = new Button
		{
			Content = UiText.Instance.Get("dialog.cancel"),
			IsCancel = true,
			MinWidth = 92.0,
			MinHeight = 40.0,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
			Style = (Style)FindResource("GhostButton")
		};
		Button continueButton = new Button
		{
			Content = UiText.Instance.Get("dialog.continue"),
			IsDefault = true,
			MinWidth = 112.0,
			MinHeight = 40.0,
			IsEnabled = false,
			Style = (Style)FindResource("PrimaryButton")
		};
		apiIdInput.TextChanged += UpdateContinueAvailability;
		apiHashInput.PasswordChanged += UpdateContinueAvailability;
		continueButton.Click += (object _, RoutedEventArgs _) =>
		{
			dialog.DialogResult = true;
		};
		stackPanel2.Children.Add(element);
		stackPanel2.Children.Add(continueButton);
		stackPanel.Children.Add(stackPanel2);
		dialog.Content = stackPanel;
		dialog.Loaded += (object _, RoutedEventArgs _) =>
		{
			apiIdInput.Focus();
		};
		if (dialog.ShowDialog() != true)
		{
			return null;
		}
		return new TelegramAppCredentials(int.Parse(apiIdInput.Text), apiHashInput.Password);
		void UpdateContinueAvailability(object? sender, RoutedEventArgs args)
		{
			continueButton.IsEnabled = int.TryParse(apiIdInput.Text, out var result) && result > 0 && !string.IsNullOrWhiteSpace(apiHashInput.Password);
		}
	}

	private string? Prompt(string message, string title, bool secret = false, bool allowEmpty = false, string? helperText = null)
	{
		Window dialog = new Window
		{
			Title = UiText.Instance.LocalizeMessage(title),
			Owner = this,
			Width = 440.0,
			SizeToContent = SizeToContent.Height,
            MaxHeight = 650,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ResizeMode = ResizeMode.NoResize
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(16.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = UiText.Instance.LocalizeMessage(message),
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		});
		var input = (secret ? null : new TextBox
		{
			Padding = new Thickness(6.0)
		});
		var password = (secret ? new PasswordBox
		{
			Padding = new Thickness(6.0)
		} : null);
		if (input != null)
		{
			stackPanel.Children.Add(input);
		}
		else if (password != null)
		{
			stackPanel.Children.Add(password);
		}
		if (helperText != null)
		{
			stackPanel.Children.Add(new TextBlock
			{
				Text = UiText.Instance.LocalizeMessage(helperText),
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
				Foreground = Brushes.DimGray
			});
		}
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
		};
		Button ok = new Button
		{
			Content = UiText.Instance.Get("dialog.continue"),
			IsDefault = true,
			MinWidth = 80.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		ok.IsEnabled = allowEmpty;
		if (input != null)
		{
			input.TextChanged += (object _, TextChangedEventArgs _) =>
			{
				ok.IsEnabled = allowEmpty || !string.IsNullOrWhiteSpace(input.Text);
			};
			dialog.Loaded += (object _, RoutedEventArgs _) =>
			{
				input.Focus();
			};
		}
		if (password != null)
		{
			password.PasswordChanged += (object _, RoutedEventArgs _) =>
			{
				ok.IsEnabled = allowEmpty || !string.IsNullOrWhiteSpace(password.Password);
			};
			dialog.Loaded += (object _, RoutedEventArgs _) =>
			{
				password.Focus();
			};
		}
		ok.Click += (object _, RoutedEventArgs _) =>
		{
			dialog.DialogResult = true;
		};
		Button element = new Button
		{
			Content = UiText.Instance.Get("dialog.cancel"),
			IsCancel = true,
			MinWidth = 80.0
		};
		stackPanel2.Children.Add(ok);
		stackPanel2.Children.Add(element);
		stackPanel.Children.Add(stackPanel2);
		dialog.Content = stackPanel;
		if (dialog.ShowDialog() != true)
		{
			return null;
		}
		var text = input?.Text;
		if (text == null)
		{
			var passwordBox = password;
			if (passwordBox == null)
			{
				return null;
			}
			text = passwordBox.Password;
		}
		return text;
	}

	private Task<string> RequestRecoveryPassphraseAsync(FileManifest manifest, CancellationToken cancellationToken)
	{
		return Dispatcher.InvokeAsync(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Prompt(UiText.Instance.Get("encryption.unlock.prompt"), UiText.Instance.Get("encryption.unlock.title"), secret: true, allowEmpty: false, UiText.Instance.Get("encryption.unlock.helper")) ?? throw new OperationCanceledException(cancellationToken);
		}).Task;
	}

	private void ChooseFile_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			CheckFileExists = true,
			Multiselect = true,
			Title = UiText.Instance.Get("upload.chooseFiles")
		};
		if (openFileDialog.ShowDialog(this) == true)
		{
			SetUploadSelection(openFileDialog.FileNames);
		}
	}

	private void UploadFolderButton_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		var dialog = new OpenFolderDialog { Title = UiText.Instance.Get("upload.chooseFolder"), Multiselect = false };
		if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
			SetUploadSelection([dialog.FolderName]);
	}

	private void SetUploadSelection(IEnumerable<string> paths)
	{
		_chosenUploadPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
			.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		_chosenFilePath = _chosenUploadPaths.FirstOrDefault(path => File.Exists(path)) ?? string.Empty;
		ChosenFileName.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("upload.selectionSummary"), _chosenUploadPaths.Length);
		ChosenFileName.ToolTip = string.Join(Environment.NewLine, _chosenUploadPaths.Take(50)) +
			(_chosenUploadPaths.Length > 50 ? Environment.NewLine + "…" : string.Empty);
		CloseFolderReview();
		UploadToolbar.Visibility = _chosenUploadPaths.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
	}

	private void CloseUploadSelection_Click(object sender, RoutedEventArgs e)
	{
		if (_operationBusy || _shutdownStarted) return;
		SetUploadSelection(Array.Empty<string>());
		ChosenFileName.Text = UiText.Instance.Get("upload.choose");
		ChosenFileName.ToolTip = null;
		UploadButton.Focus();
	}

	private static bool HasFileDrop(IDataObject? data)
	{
		return data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };
	}

	private void FilesPage_DragEnter(object sender, DragEventArgs e) => UpdateFilesDropState(e);

	private void FilesPage_DragOver(object sender, DragEventArgs e) => UpdateFilesDropState(e);

	private void FilesPage_DragLeave(object sender, DragEventArgs e)
	{
		FilesDropOverlay.Visibility = Visibility.Collapsed;
	}

	private void UpdateFilesDropState(DragEventArgs e)
	{
		bool canAccept = CanAcceptUploadDrop(e.Data);
		e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
		FilesDropOverlay.Visibility = canAccept ? Visibility.Visible : Visibility.Collapsed;
	}

	private bool CanAcceptUploadDrop(IDataObject data) => !_legacyLocalDataView && !_operationBusy && !_shutdownStarted && !_isTrashPage && HasFileDrop(data);

	private void FilesPage_Drop(object sender, DragEventArgs e)
	{
		FilesDropOverlay.Visibility = Visibility.Collapsed;
		e.Handled = true;
		if (RejectLegacyLocalDataMutation()) { e.Effects = DragDropEffects.None; return; }
		if (_operationBusy || _shutdownStarted || _isTrashPage || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
			return;
		try
		{
			SetUploadSelection(paths);
			TelegramUpload_Click(sender, new RoutedEventArgs());
		}
		catch (Exception exception)
		{
			StatusText.Text = UiText.Instance.LocalizeMessage("Could not use the dropped upload items: " + exception.Message);
		}
	}

	private async void Stage_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		if (string.IsNullOrWhiteSpace(_chosenFilePath) || !File.Exists(_chosenFilePath) || _chosenUploadPaths.Length > 1)
		{
			StatusText.Text = UiText.Instance.Get("upload.prepareSingleFile");
			return;
		}
		if (!int.TryParse(PartSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
		{
			StatusText.Text = "Part size must be a positive whole number of MiB.";
			return;
		}
		SetBusy(busy: true, "Preparing local parts and hashes...");
		checked
		{
			try
			{
				CancellationToken operationToken = OperationToken;
				long partSizeBytes = unchecked((long)result) * 1024L * 1024;
				FileManifest manifest = await _workflow.PrepareAsync(_chosenFilePath, _stagingRoot, partSizeBytes, operationToken);
				await RefreshManifestsAsync();
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.preparedParts"), manifest.Parts.Count, manifest.FileName, manifest.TotalSha256);
			}
			catch (OperationCanceledException)
			{
				StatusText.Text = "Local staging canceled. Incomplete staging data may remain in the local staging folder.";
			}
			catch (Exception ex2)
			{
				StatusText.Text = "Preparation failed: " + ex2.Message;
			}
			finally
			{
				SetBusy(busy: false);
			}
		}
	}

	private async void TelegramStorageChannel_Click(object sender, RoutedEventArgs e)
	{
		await ConnectPrivateStorageAsync(useSelectedVault: true);
	}

	private async Task ConnectPrivateStorageAsync(bool useSelectedVault = false)
	{
		var session = _telegramSession;
		var blockedReason = GetStorageConnectionBlockReason();
		if (blockedReason is not null)
		{
			if (useSelectedVault) ShowStorageConnectionFeedback(UiText.Instance.Get(blockedReason));
			return;
		}
		if (_storageChannel is not null) { await RunCatalogSyncAsync(false); return; }
		var selectedChatId = GetSelectedStorageConnectionChatId(useSelectedVault);
		_storageConnectionInProgress = true;
		_observedRemoteManifestIds.Clear();
		ShowStorageConnectionFeedback(UiText.Instance.Get("storage.connection.progress"));
		SetBusy(true, "Finding this account's private storage...", canCancel: false);
		try
		{
			var service = CreateStorageChannelService(session!);
            var account = _activeTelegramAccountId!;
            var registry = await CreateVaultRegistry(account).LoadAsync(OperationToken);
            var journal = CreateVaultCreationJournal(account);
            TelegramStorageChannelInfo? channel;
            if (journal.HasPending)
                channel = await new VaultCreationWorkflow(service, journal, account).ExecuteAsync(null, OpenRegisteredVaultAsync, OperationToken);
            else
            {
                var requestedChatId = selectedChatId ?? registry?.ActiveChatId;
                channel = requestedChatId is not null
                    ? await service.VerifyExistingAsync(requestedChatId.Value, account, OperationToken)
                    : await service.FindExistingAsync(OperationToken);
                if (channel is null)
                {
                    SetBusy(true, "Creating and verifying this account's private storage...", canCancel: false);
                    channel = await new VaultCreationWorkflow(service, journal, account).ExecuteAsync("TeleSelfCloud Storage", OpenRegisteredVaultAsync, OperationToken);
                }
                else await OpenRegisteredVaultAsync(channel, CancellationToken.None);
            }
			ApplyActionAvailability();
			EnableOperationCancellation();
			await RunCatalogSyncAsync(false);
			ShowStorageConnectionFeedback(null);
		}
		catch (OperationCanceledException) { ShowStorageConnectionFeedback(UiText.Instance.Get("status.storageConnectionCanceled")); }
		catch (Exception ex) { ShowStorageConnectionFeedback(UiText.Instance.LocalizeMessage("Storage channel setup failed: " + ex.Message)); }
		finally { _storageConnectionInProgress = false; SetBusy(false); ApplyActionAvailability(); }
	}
	private async void TelegramUpload_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		if (_operationBusy || _shutdownStarted)
		{
			return;
		}
		var session = _telegramSession;
		var channel = _storageChannel;
		if (session == null || channel is null || channel.AccountId != _activeTelegramAccountId)
		{
			StatusText.Text = "Log in and connect a private storage channel first.";
			return;
		}
		var selectedPaths = _chosenUploadPaths.Length == 0 && !string.IsNullOrWhiteSpace(_chosenFilePath)
			? [_chosenFilePath] : _chosenUploadPaths.ToArray();
		if (selectedPaths.Length == 0)
		{
			StatusText.Text = UiText.Instance.Get("upload.chooseFiles");
			return;
		}
		if (!int.TryParse(PartSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var mib) || mib <= 0)
		{
			StatusText.Text = "Part size must be a positive whole number of MiB.";
			return;
		}
		var stagingRoot = _stagingRoot; var uploadStore = _manifestStore; var folderStore = _folderStore;
		var uploadQueue = _transferQueueStore; var uploadFolder = _isTrashPage ? string.Empty : _currentFolderPath;
        var forceMultipart = ForceMultipartCheck.IsChecked == true;
        var useDedup = DeduplicateUploadCheck.IsChecked == true;
		string? recoveryPassphrase = null;
		if (EncryptContentCheck.IsChecked == true)
		{
			recoveryPassphrase = Prompt(UiText.Instance.Get("encryption.create.prompt"), UiText.Instance.Get("encryption.create.title"), secret: true, allowEmpty: false, UiText.Instance.Get("encryption.create.helper"));
			if (recoveryPassphrase == null)
			{
				return;
			}
			if (recoveryPassphrase.Length < 12)
			{
				StatusText.Text = UiText.Instance.Get("encryption.passphraseTooShort");
				return;
			}
			var text = Prompt(UiText.Instance.Get("encryption.confirm.prompt"), UiText.Instance.Get("encryption.confirm.title"), secret: true);
			if (text == null)
			{
				return;
			}
			if (!string.Equals(recoveryPassphrase, text, StringComparison.Ordinal))
			{
				StatusText.Text = UiText.Instance.Get("encryption.passphraseMismatch");
				return;
			}
		}
		if (_operationBusy || _shutdownStarted)
		{ StatusText.Text = UiText.Instance.Get("upload.scopeChanged"); return; }
		bool startQueueAfterEnqueue = false;
		SetBusy(busy: true, UiText.Instance.Get("upload.preparingBatch"));
		TelegramFileTransport? transport = null;
		EventHandler<string>? transferStatusHandler = null;
		int queuedFiles = 0;
		int failedFiles = 0;
		int preparedFolderCount = 0;
		var failureDetails = new List<string>();
		StatusText.ToolTip = null;
		try
		{
			CancellationToken cancellationToken = OperationToken;
			var selection = await Task.Run(() => UploadSourceSelector.Expand(selectedPaths, uploadFolder, cancellationToken), cancellationToken);
			if (selection.Files.Count == 0 && selection.Folders.Count == 0)
				throw new InvalidOperationException(UiText.Instance.Get("upload.selectionEmpty"));
			var scopeAnchor = selectedPaths[0];
			if (!UploadScopeMatches(session, channel, uploadStore, scopeAnchor, stagingRoot))
				throw new InvalidOperationException(UiText.Instance.Get("upload.scopeChanged"));
			bool queueWasIdle = (await uploadQueue.ListAsync(cancellationToken)).All(item => item.State is not (TransferQueueState.Pending or TransferQueueState.Running));
			transport = CreateTransport(session, channel);
			transferStatusHandler = (_, status) => Dispatcher.BeginInvoke((Action)(() => StatusText.Text = UiText.Instance.LocalizeMessage(status)));
			transport.TransferStatusChanged += transferStatusHandler;

			foreach (var folder in selection.Folders)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!UploadScopeMatches(session, channel, uploadStore, scopeAnchor, stagingRoot))
					throw new InvalidOperationException(UiText.Instance.Get("upload.scopeChanged"));
				await folderStore.CreateAsync(channel.AccountId, folder, cancellationToken);
				preparedFolderCount++;
			}
			if (selection.Folders.Count > 0)
			{
				try
				{
					await PublishFolderStateAsync(transport, channel.AccountId, cancellationToken);
					await RefreshManifestsAsync();
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception exception)
				{
					StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("upload.folderSyncFailed"), preparedFolderCount, UiText.Instance.LocalizeMessage(exception.Message));
					return;
				}
			}

			foreach (var source in selection.Files)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!UploadScopeMatches(session, channel, uploadStore, source.FullPath, stagingRoot))
					throw new InvalidOperationException(UiText.Instance.Get("upload.scopeChanged"));
				try
				{
					var manifest = await new UploadPipeline(new FileTransferCoordinator(), new TelegramUploadCapabilityProvider(session), transport,
						uploadStore, CreateManifestPublisher(transport, channel.AccountId), CreateUploadDeduplication(session, transport, channel.AccountId, useDedup), CreateStagingContentStore())
						.StageForUploadAsync(partSizeBytes: unchecked((long)mib) * 1024L * 1024, sourcePath: source.FullPath, stagingRoot: stagingRoot,
							cancellationToken: cancellationToken, forceChunking: forceMultipart, recoveryPassphrase: recoveryPassphrase);
					if (!UploadScopeMatches(session, channel, uploadStore, source.FullPath, stagingRoot))
						throw new InvalidOperationException(UiText.Instance.Get("upload.scopeChanged"));
					manifest = manifest with { FolderPath = source.DestinationFolder, UpdatedAtUtc = DateTimeOffset.UtcNow };
					await uploadStore.SaveAsync(manifest, CancellationToken.None);
					if (!UploadScopeMatches(session, channel, uploadStore, source.FullPath, stagingRoot))
						throw new InvalidOperationException(UiText.Instance.Get("upload.scopeChanged"));
					await uploadQueue.EnqueueAsync(manifest.FileId, manifest.FileName, manifest.TransferSize, CancellationToken.None);
					queuedFiles++;
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception exception)
				{
					failedFiles++;
					failureDetails.Add(Path.GetFileName(source.FullPath) + ": " + UiText.Instance.LocalizeMessage(exception.Message));
				}
			}
			startQueueAfterEnqueue = queueWasIdle && queuedFiles > 0;
			await RefreshManifestsAsync();
			await RefreshQueueAsync();
			if (failedFiles > 0)
			{
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("upload.batchResult"), queuedFiles, failedFiles, selection.Folders.Count);
				StatusText.ToolTip = string.Join(Environment.NewLine, failureDetails.Take(20));
			}
			else if (selection.Files.Count == 0)
			{
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("upload.foldersQueued"), selection.Folders.Count, queuedFiles);
			}
			else
			{
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("upload.batchResult"), queuedFiles, 0, selection.Folders.Count);
			}
		}
			catch (OperationCanceledException)
		{
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("upload.batchCancelled"), queuedFiles, failedFiles, preparedFolderCount);
		}
		catch (Exception exception)
		{
			StatusText.Text = preparedFolderCount > 0
				? string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("upload.folderPrepFailed"), preparedFolderCount, UiText.Instance.LocalizeMessage(exception.Message))
				: UiText.Instance.LocalizeMessage("Could not prepare the upload selection: " + exception.Message);
		}
		finally
		{
			if (transport != null && transferStatusHandler != null)
			{
				transport.TransferStatusChanged -= transferStatusHandler;
			}
			SetBusy(busy: false);
			if (startQueueAfterEnqueue && !_shutdownStarted)
			{
				await StartQueueAsync();
			}
		}
	}

	private async void StartQueue_Click(object sender, RoutedEventArgs e)
	{
		await StartQueueAsync();
	}

	private async Task StartQueueAsync(string? onlyTaskId = null)
	{
		if (RejectLegacyLocalDataMutation()) return;
		if (_operationBusy || _shutdownStarted)
		{
			return;
		}
		if (_telegramSession == null || _storageChannel is null || !_telegramAccountLoaded)
		{
			StatusText.Text = "Log in and connect private storage before starting queued transfers.";
			return;
		}
		SetBusy(busy: true, UiText.Instance.Get("queue.checking"), canCancel: false, "queue.pause");
		try
		{
			TransferQueueItem[] array = (from item in (await _transferQueueStore.ListAsync(OperationToken)).Where((TransferQueueItem item) =>
				{
					TransferQueueState state = item.State;
					return (state == TransferQueueState.Pending || state == TransferQueueState.Paused) ? true : false;
				})
				where _manifestItems.Any((ManifestItem manifest) => manifest.Manifest.FileId == item.FileId)
				where onlyTaskId == null || item.TaskId == onlyTaskId
				select item).ToArray();
			if (array.Length == 0)
			{
				StatusText.Text = ((onlyTaskId == null) ? "There are no pending transfers in the queue." : "The selected transfer is no longer pending.");
				return;
			}
			int num = Math.Clamp((ParallelTransfersBox?.SelectedIndex ?? 0) + 1, 1, 3);
			EnableOperationCancellation();
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("queue.starting"), array.Length, num);
			await new BoundedTransferQueueRunner().RunAsync(array, num, (TransferQueueItem item, CancellationToken _) => RunQueuedTransferAsync(item), OperationToken);
			OperationToken.ThrowIfCancellationRequested();
			await RefreshManifestsAsync();
			await RefreshQueueAsync();
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = OperationToken.IsCancellationRequested
				? "Queue start canceled. Queued items remain saved."
				: UiText.Instance.Get("queue.itemStopped");
			try
			{
				await RefreshQueueAsync();
			}
			catch
			{
			}
		}
		catch (Exception ex2)
		{
			StatusText.Text = "Could not start queued transfers: " + ex2.Message;
			try
			{
				await RefreshQueueAsync();
			}
			catch
			{
			}
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private async Task RunQueuedTransferAsync(TransferQueueItem item)
	{
		TelegramAuthSession session = _telegramSession ?? throw new InvalidOperationException("Log in and connect the private storage channel before starting queued transfers.");
		TelegramStorageChannelInfo channel = _storageChannel ?? throw new InvalidOperationException("Log in and connect the private storage channel before starting queued transfers.");
		using var itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(OperationToken);
		if (!_activeQueueItemCancellation.TryAdd(item.TaskId, itemCancellation))
			throw new InvalidOperationException("This queue item already has an active worker.");
		var itemToken = itemCancellation.Token;
		try
		{
			itemToken.ThrowIfCancellationRequested();
			await new TransferQueueStartGuard(_transferQueueStore).RunAsync(item.TaskId, RefreshQueueAsync, async () =>
			{
				itemToken.ThrowIfCancellationRequested();
				FileManifest manifest = (await _manifestStore.LoadAsync(item.FileId, CancellationToken.None)) ?? throw new FileNotFoundException("The queued transfer manifest is missing.");
				if (item.Direction == TransferDirection.Download)
				{
					if (!manifest.Committed)
					{
						throw new InvalidDataException("A download can only start from a committed remote manifest.");
					}
					if (string.IsNullOrWhiteSpace(item.DestinationPath))
					{
						throw new InvalidDataException("The queued download has no destination path.");
					}
					await _transferQueueStore.UpdateProgressAsync(item.TaskId, 0L, item.TotalBytes, CancellationToken.None);
					TelegramFileTransport transport = CreateTransport(session, channel);
					await _transferRetryPolicy.ExecuteAsync((CancellationToken token) => new FileTransferCoordinator(transport, RequestRecoveryPassphraseAsync).ReassembleAsync(manifest, item.DestinationPath, token, (TransferProgress progress) => ReportQueueProgressAsync(item, progress, itemToken)), itemToken, (int attempt, TimeSpan delay) => ReportRetryAsync(item, attempt, delay));
					await Dispatcher.InvokeAsync(() => StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.downloadedAndVerified"), manifest.FileName));
				}
				else if (!manifest.Committed)
				{
					TelegramFileTransport transport2 = CreateTransport(session, channel);
					EventHandler<string> statusHandler = (object? _, string status) =>
					{
						Dispatcher.BeginInvoke((Func<string>)(() => StatusText.Text = UiText.Instance.LocalizeMessage(status)));
					};
					transport2.TransferStatusChanged += statusHandler;
					try
					{
						UploadPipeline pipeline = new UploadPipeline(new FileTransferCoordinator(), new TelegramUploadCapabilityProvider(session), transport2, _manifestStore, CreateManifestPublisher(transport2, channel.AccountId), CreateUploadDeduplication(session, transport2, channel.AccountId, true), CreateStagingContentStore());
						FileManifest fileManifest = await _transferRetryPolicy.ExecuteAsync((CancellationToken token) => pipeline.ResumeAsync(item.FileId, token, (TransferProgress progress) => ReportQueueProgressAsync(item, progress, itemToken)), itemToken, (int attempt, TimeSpan delay) => ReportRetryAsync(item, attempt, delay));
						manifest = fileManifest;
					}
					finally
					{
						transport2.TransferStatusChanged -= statusHandler;
					}
					await Dispatcher.InvokeAsync(() => StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.uploadedAndVerified"), manifest.FileName, manifest.Parts.Count));
				}
			});
		}
		catch (OperationCanceledException)
		{
			if (_cancelQueueItemOnStop.TryRemove(item.TaskId, out _))
			{
				var current = (await _transferQueueStore.ListAsync(CancellationToken.None)).FirstOrDefault(queueItem => queueItem.TaskId == item.TaskId);
				if (current?.State == TransferQueueState.Paused)
					await _transferQueueStore.SetStateAsync(item.TaskId, TransferQueueState.Cancelled, null, CancellationToken.None);
				await RefreshQueueAsync();
				await Dispatcher.InvokeAsync(() => StatusText.Text = UiText.Instance.Get("queue.itemCancelled"));
			}
			else
				await Dispatcher.InvokeAsync(() => StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get((item.Direction == TransferDirection.Upload) ? "status.queueUploadPaused" : "status.queueDownloadPaused"), item.FileName));
			throw;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			await Dispatcher.InvokeAsync(() => StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.transferFailed"), UiText.Instance.Get((item.Direction == TransferDirection.Upload) ? "upload.upload" : "action.download"), item.FileName, UiText.Instance.LocalizeMessage(ex3.Message)));
		}
		finally
		{
			_activeQueueItemCancellation.TryRemove(item.TaskId, out _);
			_cancelQueueItemOnStop.TryRemove(item.TaskId, out _);
		}
	}

	private void ParallelTransfers_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_transferPreferences == null || ParallelTransfersBox.SelectedIndex < 0)
		{
			return;
		}
		try
		{
			_transferPreferences.SaveMaxConcurrentTransfers(ParallelTransfersBox.SelectedIndex + 1);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
			StatusText.Text = "Could not save transfer concurrency preference: " + ex.Message;
		}
	}

	private async void RetryQueue_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		object selectedItem = TransferList.SelectedItem;
		if (!(selectedItem is QueueItemView item) || _operationBusy || _shutdownStarted)
		{
			return;
		}
		bool retry = false;
		SetBusy(busy: true, UiText.Instance.Get("queue.retrying"), canCancel: false);
		try
		{
			if (item.Item.Direction == TransferDirection.Upload)
			{
				var fileManifest = await _manifestStore.LoadAsync(item.Item.FileId, CancellationToken.None);
				if (fileManifest is not null && !fileManifest.Committed && fileManifest.Parts.Any((PartRecord part) => !part.Confirmed && (string.IsNullOrWhiteSpace(part.StagingPath) || !File.Exists(part.StagingPath))))
				{
					if (fileManifest.Encryption is not null)
					{
						fileManifest = await MissingUploadPartRecovery.RestageEncryptedFromCachedPayloadAsync(fileManifest, _stagingRoot,
							OperationToken, CreateStagingContentStore());
					}
					else
					{
						OpenFileDialog openFileDialog = new OpenFileDialog
						{
							Title = UiText.Instance.Get("queue.relinkSource.prompt"),
							FileName = item.Item.FileName,
							CheckFileExists = true,
							Multiselect = false
						};
						if (openFileDialog.ShowDialog(this) != true)
						{
							return;
						}
						fileManifest = await MissingUploadPartRecovery.RestageAsync(fileManifest, openFileDialog.FileName, _stagingRoot,
							OperationToken, CreateStagingContentStore());
					}
					await _manifestStore.SaveAsync(fileManifest, CancellationToken.None);
				}
			}
			await _transferQueueStore.RequeueForRetryAsync(item.Item.TaskId, CancellationToken.None);
			await RefreshQueueAsync();
			retry = true;
		}
		catch (Exception ex)
		{
			StatusText.Text = "Could not retry the selected transfer: " + ex.Message;
		}
		finally
		{
			SetBusy(busy: false);
		}
		if (retry)
		{
			await StartQueueAsync(item.Item.TaskId);
		}
	}

	private async void RelinkUploadSource_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		object selectedItem = TransferList.SelectedItem;
		var selected = selectedItem as QueueItemView;
		if (selected is null) return;
		bool flag = _operationBusy || _shutdownStarted || selected.Item.Direction != TransferDirection.Upload;
		if (!flag)
		{
			TransferQueueState state = selected.Item.State;
			bool flag2 = ((state == TransferQueueState.Failed || state == TransferQueueState.Cancelled) ? true : false);
			flag = !flag2;
		}
		if (flag)
		{
			return;
		}
		OpenFileDialog dialog = new OpenFileDialog
		{
			Title = UiText.Instance.Get("queue.relinkSource.prompt"),
			FileName = selected.Item.FileName,
			CheckFileExists = true,
			Multiselect = false
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}
		SetBusy(busy: true, UiText.Instance.Get("queue.retrying"), canCancel: false);
		bool retry = false;
		try
		{
			FileManifest fileManifest = (await _manifestStore.LoadAsync(selected.Item.FileId, CancellationToken.None)) ?? throw new FileNotFoundException("The queued upload manifest is missing.");
			if (fileManifest.Committed || !string.Equals(fileManifest.AccountId, _activeTelegramAccountId, StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Switch to the owning Telegram account and select an uncommitted upload.");
			}
			if (fileManifest.Encryption is not null)
			{
				throw new InvalidOperationException(UiText.Instance.Get("queue.encryptedRelinkUnavailable"));
			}
			fileManifest = await MissingUploadPartRecovery.RestageAsync(fileManifest, dialog.FileName, _stagingRoot,
				OperationToken, CreateStagingContentStore());
			await _manifestStore.SaveAsync(fileManifest, CancellationToken.None);
			await _transferQueueStore.RequeueForRetryAsync(selected.Item.TaskId, CancellationToken.None);
			await RefreshQueueAsync();
			StatusText.Text = UiText.Instance.Get("queue.relinkSource.done");
			retry = true;
		}
		catch (Exception ex)
		{
			StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message);
		}
		finally
		{
			SetBusy(busy: false);
		}
		if (retry)
		{
			await StartQueueAsync(selected.Item.TaskId);
		}
	}

	private async Task ReportQueueProgressAsync(TransferQueueItem item, TransferProgress progress, CancellationToken cancellationToken = default)
	{
		await _transferQueueStore.UpdateProgressAsync(item.TaskId, progress.TransferredBytes, progress.TotalBytes, cancellationToken);
		await Dispatcher.InvokeAsync(() =>
		{
			if (_queueViews.TryGetValue(item.TaskId, out var value))
			{
				value?.UpdateProgress(progress);
			}
			int num = ((progress.TotalBytes == 0L) ? 100 : ((int)Math.Clamp((double)progress.TransferredBytes * 100.0 / (double)progress.TotalBytes, 0.0, 100.0)));
			LocalizedTextBlock statusText = StatusText;
			CultureInfo currentCulture = CultureInfo.CurrentCulture;
			string format = UiText.Instance.Get("status.transferProgress");
			InlineArray7<object?> buffer = default;
			buffer[0] = UiText.Instance.Get((item.Direction == TransferDirection.Upload) ? "upload.upload" : "action.download");
			buffer[1] = item.FileName;
			buffer[2] = num;
			buffer[3] = progress.TransferredBytes;
			buffer[4] = progress.TotalBytes;
			buffer[5] = progress.CompletedParts;
			buffer[6] = progress.PartCount;
			statusText.Text = string.Format((IFormatProvider?)currentCulture, format, (ReadOnlySpan<object?>)buffer);
		});
	}

	private async Task ReportRetryAsync(TransferQueueItem item, int failedAttempt, TimeSpan delay)
	{
		await Dispatcher.InvokeAsync(() =>
		{
			if (_queueViews.TryGetValue(item.TaskId, out var value))
			{
				value?.UpdateRetryStatus(failedAttempt + 1, 3, delay);
			}
			string text = UiText.Instance.Get((item.Direction == TransferDirection.Upload) ? "upload.upload" : "action.download").ToLowerInvariant();
			LocalizedTextBlock statusText = StatusText;
			CultureInfo currentCulture = CultureInfo.CurrentCulture;
			string format = UiText.Instance.Get("status.queueTemporaryError");
			InlineArray5<object?> buffer = default;
			buffer[0] = text;
			buffer[1] = item.FileName;
			buffer[2] = failedAttempt + 1;
			buffer[3] = 3;
			buffer[4] = delay.TotalSeconds.ToString("N0", CultureInfo.CurrentCulture);
			statusText.Text = string.Format((IFormatProvider?)currentCulture, format, (ReadOnlySpan<object?>)buffer);
		}).Task;
	}

	private async void CancelQueue_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		object selectedItem = TransferList.SelectedItem;
		var item = selectedItem as QueueItemView;
		if (item is null) return;
		bool flag = false;
		if (!flag)
		{
			TransferQueueState state = item.Item.State;
			bool flag2 = ((state == TransferQueueState.Pending || state == TransferQueueState.Paused) ? true : false);
			flag = !flag2;
		}
		if (flag)
		{
			return;
		}
		SetBusy(busy: true, UiText.Instance.Get("queue.cancelingItem"), canCancel: false);
		try
		{
			await _transferQueueStore.SetStateAsync(item.Item.TaskId, TransferQueueState.Cancelled, null, CancellationToken.None);
			await RefreshQueueAsync();
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.queueCanceled"), item.Item.FileName);
		}
		catch (Exception ex)
		{
			StatusText.Text = "Could not cancel the queued item: " + ex.Message;
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private void PauseQueueItem_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		if (_shutdownStarted || (sender as FrameworkElement)?.DataContext is not QueueItemView view ||
			view.Item.State != TransferQueueState.Running)
			return;
		if (_activeQueueItemCancellation.TryGetValue(view.Item.TaskId, out var cancellation))
		{
			try
			{
				cancellation.Cancel();
				StatusText.Text = UiText.Instance.Get("queue.itemPaused");
			}
			catch (ObjectDisposedException) { }
		}
		else
			StatusText.Text = UiText.Instance.Get("queue.itemUnavailable");
	}

	private async void CancelQueueItem_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		if (_shutdownStarted || (sender as FrameworkElement)?.DataContext is not QueueItemView view)
			return;
		var item = view.Item;
		if (_activeQueueItemCancellation.TryGetValue(item.TaskId, out var cancellation))
		{
			_cancelQueueItemOnStop[item.TaskId] = 0;
			try
			{
				cancellation.Cancel();
				StatusText.Text = UiText.Instance.Get("queue.cancelingItem");
			}
			catch (ObjectDisposedException)
			{
				_cancelQueueItemOnStop.TryRemove(item.TaskId, out _);
			}
			return;
		}
		if (item.State is not (TransferQueueState.Pending or TransferQueueState.Paused or TransferQueueState.Failed))
		{
			StatusText.Text = UiText.Instance.Get("queue.itemUnavailable");
			return;
		}
		try
		{
			await _transferQueueStore.SetStateAsync(item.TaskId, TransferQueueState.Cancelled, null, CancellationToken.None);
			await RefreshQueueAsync();
			StatusText.Text = UiText.Instance.Get("queue.itemCancelled");
		}
		catch (Exception ex)
		{
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.transferFailed"), UiText.Instance.Get("queue.cancelItem"), item.FileName, UiText.Instance.LocalizeMessage(ex.Message));
			await RefreshQueueAsync();
		}
	}

	private void TransferList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplyActionAvailability();
	}

	private void ManifestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if ((ManifestList?.SelectedItems.Count ?? 0) > 0) CloseFolderReview();
		ApplyActionAvailability();
	}

	private async void CreateFolder_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		var text = Prompt(UiText.Instance.Get("folder.create.prompt"), UiText.Instance.Get("folder.create.title"));
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		try
		{
			string path = (string.IsNullOrEmpty(_currentFolderPath) ? text.Trim() : (_currentFolderPath + "/" + text.Trim()));
			LocalFolder folder = await CreateFolderAsync(path);
			if (_telegramSession == null || _storageChannel is null || _activeTelegramAccountId != folder.AccountId)
			{
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.create.localOnly"), folder.Path);
				return;
			}
			SetBusy(busy: true, UiText.Instance.Get("folder.create.syncing"));
			try
			{
				await PublishFolderStateAsync(CreateTransport(_telegramSession, _storageChannel), folder.AccountId, OperationToken);
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.create.done"), folder.Path);
			}
			catch (OperationCanceledException)
			{
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.create.localOnly"), folder.Path);
			}
			catch (Exception ex2)
			{
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.create.syncFailed"), folder.Path, UiText.Instance.LocalizeMessage(ex2.Message));
			}
			finally
			{
				SetBusy(busy: false);
			}
		}
		catch (Exception ex3)
		{
			StatusText.Text = UiText.Instance.Get("folder.create.title") + ": " + UiText.Instance.LocalizeMessage(ex3.Message);
		}
	}

	private async void BulkMove_Click(object sender, RoutedEventArgs e)
	{
		ManifestItem[] selectedBulkItems = GetSelectedBulkItems();
		if (selectedBulkItems.Length != 0)
		{
			var text = Prompt("Enter a folder path relative to the storage root. Use / between folders; leave empty for the root:", UiText.Instance.Get("action.move"), secret: false, allowEmpty: true, UiText.Instance.Get("folder.pathExample"));
			if (text != null)
			{
				await RunBulkFileActionAsync(selectedBulkItems, BulkFileAction.Move, text);
			}
		}
	}

	private async void BulkTrash_Click(object sender, RoutedEventArgs e)
	{
		ManifestItem[] selectedBulkItems = GetSelectedBulkItems();
		if (selectedBulkItems.Length != 0)
		{
			string messageBoxText = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("bulk.trash.confirm"), selectedBulkItems.Length);
			if (MessageBox.Show(this, messageBoxText, UiText.Instance.Get("action.moveToTrash"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
			{
				await RunBulkFileActionAsync(selectedBulkItems, BulkFileAction.MoveToTrash);
			}
		}
	}

	private ManifestItem[] GetSelectedBulkItems()
	{
		return (from ManifestItem item in ManifestList.SelectedItems
			where item.Manifest.Committed && !item.Manifest.IsInTrash
			select item).ToArray();
	}

	private async Task RunBulkFileActionAsync(ManifestItem[] selected, BulkFileAction action, string? destination = null)
	{
		SetBusy(busy: true, UiText.Instance.Get("bulk.working"));
		try
		{
			IReadOnlyList<BulkFileActionResult> readOnlyList = await ExecuteBulkFileActionsAsync(selected.Select((ManifestItem item) => item.Manifest.FileId), action, destination, OperationToken);
			int num = readOnlyList.Count((BulkFileActionResult result) => result.Succeeded);
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("bulk.done"), num, readOnlyList.Count);
			BulkFileActionResult[] array = readOnlyList.Where((BulkFileActionResult result) => !result.Succeeded).ToArray();
			if (array.Length == 0)
			{
				return;
			}
			string messageBoxText = string.Join(Environment.NewLine, array.Select((BulkFileActionResult result) => selected.First((ManifestItem item) => item.Manifest.FileId == result.FileId).Manifest.FileName + ": " + UiText.Instance.LocalizeMessage(result.Error ?? string.Empty)));
			MessageBox.Show(this, messageBoxText, UiText.Instance.Get("bulk.failures.title"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		catch (OperationCanceledException) when (OperationToken.IsCancellationRequested)
		{
			StatusText.Text = UiText.Instance.Get("bulk.partial.cancel");
		}
		catch (Exception ex2)
		{
			StatusText.Text = UiText.Instance.Get("bulk.done") + " " + UiText.Instance.LocalizeMessage(ex2.Message);
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private void TrashManifestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplyActionAvailability();
	}

	private async void DeleteForever_Click(object sender, RoutedEventArgs e)
	{
        if (!_telegramAccountLoaded || _telegramSession is not { CurrentAuthorizationState: "authorizationStateReady" }) return;
        var selected = TrashManifestList.SelectedItems.Cast<ManifestItem>().Where(item => item.Manifest.Committed && item.Manifest.IsInTrash).ToArray();
        try
        {
            using var scope = CapturePermanentDeletionScope(selected.Select(item => item.Manifest).ToArray());
            if (scope is null) { StatusText.Text = UiText.Instance.Get("trash.deleteForever.scopeChanged"); return; }
            var prompt = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("trash.deleteForever.confirm"), selected.Length);
            if (MessageBox.Show(this, prompt, UiText.Instance.Get("action.deleteForever"), MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes) return;
            var currentIds = TrashManifestList.SelectedItems.Cast<ManifestItem>().Select(item => item.Manifest.FileId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (!currentIds.SequenceEqual(scope.Manifests.Select(m => m.FileId).OrderBy(id => id, StringComparer.Ordinal)))
            { StatusText.Text = UiText.Instance.Get("trash.deleteForever.scopeChanged"); return; }
            var results = await RunPermanentDeletionAsync(scope, (key, token) =>
            {
                var transport = CreateTransport(scope.Session!, scope.Channel);
                var deleter = new TelegramRemoteFileDeleter(scope.Session!, transport, scope.Channel.ChatId, scope.Channel.AccountId, key);
                return new PermanentFileDeletion(scope.Store, scope.Queue, deleter).ExecuteAsync(scope.Manifests.Select(m => m.FileId), scope.Channel.AccountId, token);
            });
            if (results is null) return;
            var failures = results.Where(result => !result.Succeeded).ToArray();
            if (failures.Length == 0) return;
            var details = string.Join(Environment.NewLine, failures.Select(result => string.Concat(selected.First(item => item.Manifest.FileId == result.FileId).Manifest.FileName, ": ", UiText.Instance.LocalizeMessage(result.Error ?? string.Empty), result.RemoteDeletionCompleted ? UiText.Instance.LocalizeMessage(" Telegram deletion completed; local cleanup failed.") : string.Empty)));
            MessageBox.Show(this, details, UiText.Instance.Get("trash.deleteForever.details"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
        }
        catch (Exception) { StatusText.Text = UiText.Instance.Get("trash.deleteForever.failed"); }
	}
	private async void TelegramResume_Click(object sender, RoutedEventArgs e)
	{
		var session = _telegramSession;
		var channel = _storageChannel;
		if (session != null && channel is not null && channel.AccountId == _activeTelegramAccountId)
		{
			var item = SelectedManifestItem;
			if (item is not null)
			{
				if (item.Manifest.Committed)
				{
					StatusText.Text = "This upload is already committed in Telegram.";
					return;
				}
				SetBusy(busy: true, UiText.Instance.Get("status.resumingConfirmedParts"));
				TelegramFileTransport? transport = null;
				EventHandler<string>? transferStatusHandler = null;
				try
				{
					try
					{
						CancellationToken cancellationToken = OperationToken;
						await _transferQueueStore.EnsureAsync(item.Manifest.FileId, item.Manifest.FileName, item.Manifest.TransferSize, CancellationToken.None);
						TransferQueueItem queuedItem = (await _transferQueueStore.ListAsync(CancellationToken.None)).Single((TransferQueueItem queueItem) => queueItem.FileId == item.Manifest.FileId);
						TransferQueueState state = queuedItem.State;
						if (((state == TransferQueueState.Failed || state == TransferQueueState.Cancelled) ? 1 : 0) != 0)
						{
							await _transferQueueStore.EnqueueAsync(item.Manifest.FileId, item.Manifest.FileName, item.Manifest.TransferSize, CancellationToken.None);
						}
						await _transferQueueStore.SetStateAsync(item.Manifest.FileId, TransferQueueState.Running, null, CancellationToken.None);
						transport = CreateTransport(session, channel);
						transferStatusHandler = (object? _, string status) =>
						{
							Dispatcher.BeginInvoke((Func<string>)(() => StatusText.Text = UiText.Instance.LocalizeMessage(status)));
						};
						transport.TransferStatusChanged += transferStatusHandler;
						FileManifest manifest = await new UploadPipeline(new FileTransferCoordinator(), new TelegramUploadCapabilityProvider(session), transport, _manifestStore, CreateManifestPublisher(transport, channel.AccountId), CreateUploadDeduplication(session, transport, channel.AccountId, true), CreateStagingContentStore()).ResumeAsync(item.Manifest.FileId, cancellationToken, (TransferProgress progress) => ReportQueueProgressAsync(queuedItem, progress));
						if (manifest.Committed)
						{
							_observedRemoteManifestIds.Add(manifest.FileId);
						}
						await _transferQueueStore.SetStateAsync(item.Manifest.FileId, TransferQueueState.Completed, null, CancellationToken.None);
						await RefreshManifestsAsync();
						await RefreshQueueAsync();
						StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get(manifest.Committed ? "status.uploadComplete" : "status.uploadCheckpointSaved"), manifest.FileName);
					}
					catch (OperationCanceledException)
					{
						var selectedManifestItem = SelectedManifestItem;
						if (selectedManifestItem is not null)
						{
							await _transferQueueStore.SetStateAsync(selectedManifestItem.Manifest.FileId, TransferQueueState.Paused, null, CancellationToken.None);
						}
						await RefreshQueueAsync();
						StatusText.Text = UiText.Instance.Get("status.resumeCanceled");
					}
					catch (Exception ex2)
					{
						var selectedManifestItem2 = SelectedManifestItem;
						if (selectedManifestItem2 is not null)
						{
							try
							{
								await _transferQueueStore.SetStateAsync(selectedManifestItem2.Manifest.FileId, TransferQueueState.Failed, SafeFailure.Describe(ex2), CancellationToken.None);
							}
							catch
							{
							}
						}
						await RefreshQueueAsync();
						StatusText.Text = "Could not resume upload: " + ex2.Message;
					}
					return;
				}
				finally
				{
					if (transport != null && transferStatusHandler != null)
					{
						transport.TransferStatusChanged -= transferStatusHandler;
					}
					SetBusy(busy: false);
				}
			}
		}
		StatusText.Text = "Log in, connect storage, and select a transfer manifest first.";
	}

	private async void TelegramImport_Click(object sender, RoutedEventArgs e)
	{
		if (_operationBusy || _shutdownStarted) return;
		await RunCatalogSyncAsync(sender == FullCatalogSyncButton, sender == RetryCatalogSyncButton);
	}
	private async void TelegramRemoteRestore_Click(object sender, RoutedEventArgs e)
	{
		if (_operationBusy || _shutdownStarted)
		{
			return;
		}
		if (_telegramSession != null && _storageChannel is not null)
		{
			var item = SelectedManifestItem;
			if (item is not null)
			{
				if (!item.Manifest.Committed)
				{
					StatusText.Text = "The selected manifest is not a committed Telegram upload.";
					return;
				}
				SaveFileDialog dialog = new SaveFileDialog
				{
					FileName = item.Manifest.FileName,
					OverwritePrompt = true
				};
				if (dialog.ShowDialog(this) != true)
				{
					return;
				}
				bool startQueueAfterEnqueue = false;
				string? queuedTaskId = null;
				SetBusy(busy: true, UiText.Instance.Get("status.addingDownloadToQueue"), canCancel: false);
				try
				{
					startQueueAfterEnqueue = (await _transferQueueStore.ListAsync(OperationToken)).All((TransferQueueItem queued) =>
					{
						TransferQueueState state = queued.State;
						bool flag = (uint)(state - 1) <= 1u;
						return !flag;
					});
					queuedTaskId = (await _transferQueueStore.EnqueueDownloadAsync(item.Manifest.FileId, item.Manifest.FileName, dialog.FileName, item.Manifest.TransferSize, OperationToken)).TaskId;
					await RefreshQueueAsync();
					string key = (startQueueAfterEnqueue ? "queue.downloadStarted" : "queue.waitingPaused");
					StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get(key), item.Manifest.FileName);
				}
				catch (OperationCanceledException)
				{
					StatusText.Text = "Adding the download was canceled.";
				}
				catch (Exception ex2)
				{
					StatusText.Text = ((queuedTaskId == null) ? ("Could not add the download to the queue: " + ex2.Message) : ("Download was queued, but the transfer list could not refresh: " + ex2.Message));
				}
				finally
				{
					SetBusy(busy: false);
				}
				if (startQueueAfterEnqueue && queuedTaskId != null && !_shutdownStarted)
				{
					await StartQueueAsync();
				}
				return;
			}
		}
		StatusText.Text = "Log in, connect storage, and select a remote manifest first.";
	}

	private async void RenameFile_Click(object sender, RoutedEventArgs e)
	{
		var selectedManifestItem = SelectedManifestItem;
		if (selectedManifestItem is null || !selectedManifestItem.Manifest.Committed || selectedManifestItem.Manifest.IsInTrash)
		{
			return;
		}
		var text = Prompt("Enter the new file name:", "Rename file");
		if (text == null || text == selectedManifestItem.Manifest.FileName)
		{
			return;
		}
		try
		{
			FileManifest revised = FileManifestMetadata.Rename(selectedManifestItem.Manifest, text);
			await PublishMetadataRevisionAsync(revised, string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.fileRenamed"), text));
		}
		catch (Exception ex)
		{
			StatusText.Text = "Rename failed: " + ex.Message;
		}
	}

	private async void MoveFile_Click(object sender, RoutedEventArgs e)
	{
		var selectedManifestItem = SelectedManifestItem;
		if (selectedManifestItem is null || !selectedManifestItem.Manifest.Committed || selectedManifestItem.Manifest.IsInTrash)
		{
			return;
		}
		var text = Prompt("Enter a folder path relative to the storage root. Use / between folders; leave empty for the root:", "Move file", secret: false, allowEmpty: true, "Example: Documents/Reports");
		if (text == null)
		{
			return;
		}
		try
		{
			FileManifest fileManifest = FileManifestMetadata.Move(selectedManifestItem.Manifest, text);
			if (!(fileManifest.FolderPath == selectedManifestItem.Manifest.FolderPath))
			{
				await PublishMetadataRevisionAsync(fileManifest, (fileManifest.FolderPath.Length == 0) ? UiText.Instance.Get("status.fileMovedToRoot") : string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.fileMoved"), fileManifest.FolderPath));
			}
		}
		catch (Exception ex)
		{
			StatusText.Text = "Move failed: " + ex.Message;
		}
	}

	private async void TrashFile_Click(object sender, RoutedEventArgs e)
	{
		var selectedManifestItem = SelectedManifestItem;
		if (selectedManifestItem is null || !selectedManifestItem.Manifest.Committed)
		{
			return;
		}
		bool isInTrash = selectedManifestItem.Manifest.IsInTrash;
		if (!isInTrash && MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("trash.move.confirm"), selectedManifestItem.Manifest.FileName), UiText.Instance.Get("action.moveToTrash"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		try
		{
			FileManifest revised = FileManifestMetadata.SetTrashed(selectedManifestItem.Manifest, !isInTrash);
			await PublishMetadataRevisionAsync(revised, string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get(isInTrash ? "status.fileRestoredFromTrash" : "status.fileMovedToTrash"), selectedManifestItem.Manifest.FileName));
		}
		catch (Exception ex)
		{
			StatusText.Text = "Trash update failed: " + ex.Message;
		}
	}

	private async void RemoveLocalEntries_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		ManifestItem[] selected = ((sender == TrashRemoveLocalEntriesButton) ? TrashManifestList.SelectedItems : ManifestList.SelectedItems).Cast<ManifestItem>().ToArray();
		if (selected.Length == 0 || selected.Any((ManifestItem item) => !CanRemoveLocalEntry(item)))
		{
			return;
		}
		var accountId = selected[0].Manifest.AccountId;
		if (string.IsNullOrWhiteSpace(accountId) || selected.Any((ManifestItem item) => item.Manifest.AccountId != accountId))
		{
			return;
		}
		string messageBoxText = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("dialog.removeLocalEntries.confirm"), selected.Length);
		if (MessageBox.Show(this, messageBoxText, UiText.Instance.Get("action.removeLocalEntries"), MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		SetBusy(busy: true, UiText.Instance.Get("bulk.working"), canCancel: false);
		try
		{
			await new LocalManifestRemoval(_manifestStore, _transferQueueStore, _localCacheVerificationStore).RemoveAsync(selected.Select((ManifestItem item) => item.Manifest.FileId), accountId, CancellationToken.None);
			await RefreshManifestsAsync();
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.localEntriesRemoved"), selected.Length);
		}
		catch (Exception ex)
		{
			StatusText.Text = UiText.Instance.Get("action.removeLocalEntry") + ": " + UiText.Instance.LocalizeMessage(ex.Message);
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private bool CanRemoveLocalEntry(ManifestItem item)
	{
		if (item.Manifest.Committed && item.RemoteStatusUnknown)
		{
			return !string.IsNullOrWhiteSpace(item.Manifest.AccountId);
		}
		return false;
	}

	private bool HasActiveTransfer(string fileId)
	{
		return _queueViews.Values.Any((QueueItemView view) =>
		{
			bool flag = view.Item.FileId == fileId;
			if (flag)
			{
				TransferQueueState state = view.Item.State;
				bool flag2 = (uint)state <= 2u;
				flag = flag2;
			}
			return flag;
		});
	}

	private async Task PublishMetadataRevisionAsync(FileManifest revised, string successMessage)
	{
		if (_telegramSession == null || _storageChannel is null)
		{
			throw new InvalidOperationException("Connect the private storage channel before changing remote metadata.");
		}
		SetBusy(busy: true, UiText.Instance.Get("status.publishingManifestRevision"));
		try
		{
			TelegramFileTransport transport = CreateTransport(_telegramSession, _storageChannel);
			await CreateManifestPublisher(transport, _storageChannel.AccountId).PublishCommittedAsync(revised, OperationToken);
			_observedRemoteManifestIds.Add(revised.FileId);
			await _manifestStore.SaveAsync(revised, CancellationToken.None);
			await RefreshManifestsAsync();
			StatusText.Text = successMessage;
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private async Task ActivateAccountProfileAsync(string accountId)
	{
		SetBusy(busy: true, UiText.Instance.Get("status.preparingAccountStorage"));
		try
		{
			var registry = CreateVaultRegistry(accountId);
			var registeredVaults = await registry.LoadAsync(CancellationToken.None);
			string profileRoot = registeredVaults is null ? GetAccountDataDirectory(accountId)
				: registry.GetDataDirectory(registeredVaults, registeredVaults.PrimaryChatId);
			Directory.CreateDirectory(profileRoot);
			var profileStores = CreateProtectedVaultStores(profileRoot);
			if (registeredVaults is not null)
				await profileStores.PrepareAsync(accountId, CancellationToken.None, registeredVaults.PrimaryChatId);
			var obj = _sharedManifestStore as SqliteManifestStore;
			if (obj == null || !(_sharedTransferQueueStore is SqliteTransferQueueStore sharedQueueStore) || !(_sharedSyncCheckpointStore is SqliteRemoteSyncCheckpointStore sharedCheckpointStore))
			{
				throw new InvalidOperationException("Account profile migration requires the SQLite stores.");
			}
			var sharedStagingContent = DatabaseProfileLease is null ? null : CreateStagingContentStore(LocalDataRoot);
			var accountStagingContent = DatabaseProfileLease is null ? null : CreateStagingContentStore(profileRoot);
			AccountProfileMigrationResult accountProfileMigrationResult = await new AccountProfileDataMigrator(obj, profileStores.Manifests,
				sharedQueueStore, profileStores.Queue, sharedCheckpointStore, profileStores.Checkpoints, _sharedLocalCacheVerificationStore,
				Path.Combine(LocalDataRoot, "staging"), Path.Combine(profileRoot, "staging"), sharedStagingContent,
				accountStagingContent).MigrateAsync(accountId, CancellationToken.None, registeredVaults?.PrimaryChatId);
			var externalStagingCount = 0;
			if (LocalDatabaseProtection.IsConfigured(profileRoot))
			{
				var stagingMigration = await LocalStagingProtectionMigrator.MigrateAsync(Path.Combine(profileRoot, "staging"), profileStores.Manifests,
					accountStagingContent!, CancellationToken.None, (_, _) => StatusText.Text = UiText.Instance.Get("localdb.stage.StagingEncrypting"));
				externalStagingCount = stagingMigration.OutsideCatalogCount;
			}
			if (DatabaseProfileLease is { } lease)
			{
				await StagingOrphanReconciler.ReconcileAsync(LocalDataRoot, Path.Combine(LocalDataRoot, "staging"), _sharedManifestStore, lease, DateTimeOffset.UtcNow, CancellationToken.None);
				await StagingOrphanReconciler.ReconcileAsync(LocalDataRoot, Path.Combine(profileRoot, "staging"), profileStores.Manifests, lease, DateTimeOffset.UtcNow, CancellationToken.None);
			}
			_vaultRegistry = registeredVaults;
			UseVaultStores(profileStores);
			if (accountProfileMigrationResult.MigratedManifestCount > 0)
			{
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.accountRecordsMigrated"), accountProfileMigrationResult.MigratedManifestCount);
			}
			if (accountProfileMigrationResult.RetainedManifestCount > 0)
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.sharedRecordsRetained"), accountProfileMigrationResult.RetainedManifestCount);
			if (registeredVaults is not null)
			{
				var stores = registeredVaults.ActiveChatId == registeredVaults.PrimaryChatId ? profileStores
					: CreateProtectedVaultStores(registry.GetDataDirectory(registeredVaults, registeredVaults.ActiveChatId));
				if (!ReferenceEquals(stores, profileStores))
					await stores.PrepareAsync(accountId, CancellationToken.None, registeredVaults.ActiveChatId);
				if (DatabaseProfileLease is { } activeLease)
					await StagingOrphanReconciler.ReconcileAsync(LocalDataRoot, stores.StagingRoot, stores.Manifests, activeLease, DateTimeOffset.UtcNow, CancellationToken.None);
				UseVaultStores(stores);
				LoadMetadataKey(stores.Root, accountId, registeredVaults.ActiveChatId);
			}
			ReportExternalStagingPaths(externalStagingCount);
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private void UseSharedWorkspace()
	{
		_vaultProfileSetupFailed = false;
		_legacyLocalDataView = false;
		_legacyViewReturnChannel = null;
		_pendingLegacyIsolationChannel = null;
		RestoreLegacyLocalViewControls();
        _metadataKeyGeneration++;
        _metadataKey?.Dispose(); _metadataKey = null; _metadataKeyReadFailed = false;
		_vaultRegistry = null;
		_manifestStore = _sharedManifestStore;
		_transferQueueStore = _sharedTransferQueueStore;
		_syncCheckpointStore = _sharedSyncCheckpointStore;
		_folderStore = _sharedFolderStore;
		_stagingRoot = Path.Combine(LocalDataRoot, "staging");
		var stagingContent = DatabaseProfileLease is null ? null : CreateStagingContentStore();
		_workflow = new LocalFileWorkflow(new FileTransferCoordinator(), _sharedManifestStore,
			new StagedPartAssembler(stagingContent), stagingContent);
		_localCacheVerificationStore = _sharedLocalCacheVerificationStore;
		_queueViews.Clear();
	}

	private TelegramStorageChannelService CreateStorageChannelService(TelegramAuthSession session)
	{
		string accountId = _activeTelegramAccountId ?? throw new InvalidOperationException("TDLib account identity is required before loading storage settings.");
		string accountRoot = GetAccountDataDirectory(accountId);
		Func<LocalRecordCipher>? settingsCipher = LocalDatabaseProtection.IsConfigured(accountRoot)
			? () => LocalDatabaseProtection.RecordCipher(accountRoot,
				$"account:{accountId}:storage-channel-settings", "account-storage-settings")
			: null;
		return new TelegramStorageChannelService(session, GetAccountStorageSettingsPath(accountId), settingsCipher);
	}

	private TelegramFileTransport CreateTransport(TelegramAuthSession session, TelegramStorageChannelInfo channel)
	{
		string accountDataDirectory = GetVaultDataDirectory(channel);
		return new TelegramFileTransport(session, channel.ChatId, Path.Combine(accountDataDirectory, "downloads"));
	}

	private string GetAccountDataDirectory(string accountId)
	{
		return TelegramAccountProfileStore.GetDirectory(LocalDataRoot, accountId);
	}

	private string GetAccountStorageSettingsPath(string accountId)
	{
		return TelegramAccountProfileStore.GetStorageChannelSettingsPath(LocalDataRoot, accountId, Path.Combine(LocalDataRoot, "telegram-account", "storage-channel.json"));
	}

	private TelegramManifestPublisher CreateManifestPublisher(TelegramFileTransport transport, string accountId)
	{
		var channel = _storageChannel;
		if (channel is null || channel.AccountId != accountId) throw new InvalidOperationException("The selected vault is not registered for this account.");
		return new TelegramManifestPublisher(transport, Path.Combine(GetVaultDataDirectory(channel), "remote-manifests"), MetadataKeyForPublishing());
	}

	private Task PublishFolderStateAsync(TelegramFileTransport transport, string accountId, CancellationToken cancellationToken)
	{
		return new TelegramRemoteFolderStatePublisher(_folderStore, transport, Path.Combine(_stagingRoot, "folder-state-temp"), MetadataKeyForPublishing()).PublishAsync(accountId, cancellationToken);
	}

	private async void Restore_Click(object sender, RoutedEventArgs e)
	{
		var item = SelectedManifestItem;
		if (item is null)
		{
			StatusText.Text = "Select a local manifest first.";
			return;
		}
		if (!item.HasAllPartFiles || item.Manifest.IsInTrash)
		{
			StatusText.Text = UiText.Instance.Get("local.restore.unavailable");
			return;
		}
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			FileName = item.Manifest.FileName,
			OverwritePrompt = true,
			CheckPathExists = true
		};
		if (saveFileDialog.ShowDialog(this) != true)
		{
			return;
		}
		SetBusy(busy: true, UiText.Instance.Get("status.restoringStagedParts"));
		try
		{
			CancellationToken operationToken = OperationToken;
			if (item.Manifest.Encryption is null)
			{
				await _workflow.RestoreAsync(item.Manifest.FileId, saveFileDialog.FileName, operationToken);
			}
			else
			{
				await RestoreEncryptedCachedPartsAsync(item.Manifest, saveFileDialog.FileName, operationToken);
			}
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.restoredAndVerified"), item.Manifest.FileName, item.Manifest.LogicalSize, item.Manifest.TotalSha256);
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = UiText.Instance.Get("local.restore.canceled");
		}
		catch (Exception ex2)
		{
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.restoreFailedPartial"), UiText.Instance.LocalizeMessage(ex2.Message));
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private async Task RestoreEncryptedCachedPartsAsync(FileManifest manifest, string destinationPath, CancellationToken cancellationToken)
	{
		if (manifest.Encryption is null) throw new InvalidOperationException("The selected manifest does not contain an encrypted payload.");
		string passphrase = await RequestRecoveryPassphraseAsync(manifest, cancellationToken);
		using var destinationLease = DownloadWorkspace.AcquireDestinationLease(destinationPath);
		string payloadPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!, ".tsc-cache-" + Guid.NewGuid().ToString("N") + ".cipher");
		bool ownsPayload = false;
		try
		{
			await StagedEncryptedPayloadAssembler.AssembleAsync(manifest, payloadPath, cancellationToken, CreateStagingContentStore());
			ownsPayload = true;
			await EncryptedPayloadRestorer.RestoreAsync(manifest, payloadPath, destinationPath, passphrase, cancellationToken);
		}
		finally
		{
			try
			{
				if (ownsPayload && File.Exists(payloadPath))
				{
					File.Delete(payloadPath);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	private async void VerifyCache_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		var item = SelectedManifestItem;
		if (item is null)
		{
			StatusText.Text = "Select a file first.";
			return;
		}
		SetBusy(busy: true, string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.checkingFileParts"), item.Manifest.FileName));
		try
		{
			LocalCacheVerification result = await _localCacheVerificationStore.VerifyAndSaveAsync(item.Manifest, OperationToken,
				CreateStagingContentStore());
			await RefreshManifestsAsync();
			LocalizedTextBlock statusText = StatusText;
			statusText.Text = result.State switch
			{
				LocalCacheIntegrityState.AvailableOffline => $"{item.Manifest.FileName} is available offline from verified local parts. SHA-256 was checked at {result.VerifiedAtUtc.ToLocalTime():g}.", 
				LocalCacheIntegrityState.Partial => $"Only {result.ValidParts}/{result.PartCount} local parts passed verification for {item.Manifest.FileName}; remote download is still required.", 
				LocalCacheIntegrityState.Missing => "No local part files are available for " + item.Manifest.FileName + "; remote download is required.", 
				_ => "Local cache integrity check failed for " + item.Manifest.FileName + ". Restore will recheck part hashes before writing the destination.", 
			};
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = "Local cache verification canceled; no verification result was saved.";
		}
		catch (Exception ex2)
		{
			StatusText.Text = "Could not verify local cache: " + ex2.Message;
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private Task RefreshManifestsAsync() => RefreshManifestsCoreAsync();

	private Task RefreshManifestsReadOnlyAsync() => RefreshManifestsCoreAsync();

	private async Task RefreshManifestsCoreAsync()
	{
		IReadOnlyList<FileManifest> allManifests = await _workflow.ListAsync(CancellationToken.None);
		_localFolderPaths = (await _folderStore.ListAsync(_activeTelegramAccountId ?? "local", CancellationToken.None)).Select((LocalFolder folder) => folder.Path).ToArray();
		if (_activeTelegramAccountId != null)
		{
			await _sharedManifestStore.ListAsync(CancellationToken.None);
		}
		else
		{
			Array.Empty<FileManifest>();
		}
		FileManifest[] manifests = allManifests.Where(IsVisibleToActiveAccount).ToArray();
		// Refresh observes existing work. Only an explicit upload/resume may enqueue a draft:
		// rebuilding missing tasks here resurrects cancelled uploads after Clear finished.
		HashSet<string> visibleFileIds = manifests.Select((FileManifest manifest) => manifest.FileId).ToHashSet(StringComparer.Ordinal);
		TransferQueueItem[] source = (await _transferQueueStore.ListAsync(CancellationToken.None)).Where((TransferQueueItem item) => visibleFileIds.Contains(item.FileId)).ToArray();
		Dictionary<string, TransferQueueItem> queueByFileId = source.Where((TransferQueueItem item) => item.Direction == TransferDirection.Upload).ToDictionary((TransferQueueItem item) => item.FileId, StringComparer.Ordinal);
		IReadOnlyDictionary<string, LocalCacheVerification> cacheVerifications = await _localCacheVerificationStore.LoadAllAsync(CancellationToken.None);
		_manifestItems = manifests.Select((FileManifest manifest) => new ManifestItem(manifest, queueByFileId.TryGetValue(manifest.FileId, out var value) ? new TransferQueueState?(value.State) : ((TransferQueueState?)null), cacheVerifications.GetValueOrDefault(manifest.FileId), RemoteManifestStatus.IsUnknown(manifest, _observedRemoteManifestIds))).ToArray();
		BuildFolderTree(manifests);
		await RefreshPendingFolderChangeAsync();
		ApplyManifestFilterAndSort();
		await RefreshQueueAsync();
	}

	public async Task<LocalFolder> CreateFolderAsync(string path, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (_legacyLocalDataView) throw new InvalidOperationException(UiText.Instance.Get("vault.viewLegacyOpened"));
		LocalFolder folder = await _folderStore.CreateAsync(_activeTelegramAccountId ?? "local", path, cancellationToken);
		await RefreshManifestsAsync();
		return folder;
	}

	private FolderManagementService CreateFolderManagementService(TelegramFileTransport transport, TelegramStorageChannelInfo channel)
	{
		return new FolderManagementService(_folderStore, _manifestStore, _transferQueueStore, (FileManifest manifest, CancellationToken token) => CreateManifestPublisher(transport, channel.AccountId).PublishCommittedAsync(manifest, token), (CancellationToken token) => PublishFolderStateAsync(transport, channel.AccountId, token), CreateFolderOperationJournal(channel));
	}

	private async Task<FolderOperationResult> RenameFolderAsync(string path, string name, CancellationToken cancellationToken)
	{
		if (_telegramSession == null || _storageChannel is null || _activeTelegramAccountId != _storageChannel.AccountId)
		{
			throw new InvalidOperationException("Connect the folder's Telegram storage account before renaming it.");
		}
		TelegramFileTransport transport = CreateTransport(_telegramSession, _storageChannel);
		return await CreateFolderManagementService(transport, _storageChannel).RenameAsync(_storageChannel.AccountId, path, name, cancellationToken);
	}

	private async Task<FolderOperationResult> DeleteFolderAsync(string path, CancellationToken cancellationToken)
	{
		if (_telegramSession == null || _storageChannel is null || _activeTelegramAccountId != _storageChannel.AccountId)
		{
			throw new InvalidOperationException("Connect the folder's Telegram storage account before deleting it.");
		}
		TelegramFileTransport transport = CreateTransport(_telegramSession, _storageChannel);
		return await CreateFolderManagementService(transport, _storageChannel).DeleteAsync(_storageChannel.AccountId, path, cancellationToken);
	}

	private async void FolderRename_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		if (!(sender is MenuItem { Tag: var tag }))
		{
			return;
		}
		var path = tag as string;
		if (path == null || string.IsNullOrWhiteSpace(path) || _operationBusy || _shutdownStarted)
		{
			return;
		}
		var name = Prompt(UiText.Instance.Get("folder.rename.prompt"), UiText.Instance.Get("folder.rename.title"));
		if (!string.IsNullOrWhiteSpace(name))
		{
			await RunFolderOperationAsync(() => RenameFolderAsync(path, name, OperationToken));
		}
	}

	private async void FolderDelete_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		if (!(sender is MenuItem { Tag: var tag }))
		{
			return;
		}
		var path = tag as string;
		if (path == null || string.IsNullOrWhiteSpace(path) || _operationBusy || _shutdownStarted)
		{
			return;
		}
		string messageBoxText = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.delete.confirm"), path);
		if (MessageBox.Show(this, messageBoxText, UiText.Instance.Get("folder.delete.title"), MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			await RunFolderOperationAsync(() => DeleteFolderAsync(path, OperationToken));
		}
	}

	private async Task RunFolderOperationAsync(Func<Task<FolderOperationResult>> operation)
	{
		if (RejectLegacyLocalDataMutation()) return;
		SetBusy(busy: true, UiText.Instance.Get("bulk.working"));
		try
		{
			FolderOperationResult result = await operation();
			await RefreshManifestsAsync();
			StatusText.Text = result.KeptCurrentState
				? (result.FolderStatePublished ? UiText.Instance.Get("folder.stop.done") : string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.stop.syncPending"), UiText.Instance.LocalizeMessage(result.SyncError ?? string.Empty)))
				: (result.FolderStatePublished ? string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.operation.done"), result.Path, result.UpdatedFiles) : string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.operation.syncPending"), result.Path, UiText.Instance.LocalizeMessage(result.SyncError ?? string.Empty)));
		}
		catch (FolderOperationException ex)
		{
			await RefreshManifestsAsync();
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.operation.partial"), ex.PublishedFileCount);
		}
		catch (Exception ex2)
		{
			await RefreshManifestsAsync();
			StatusText.Text = UiText.Instance.LocalizeMessage(ex2.Message);
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	public async Task<IReadOnlyList<BulkFileActionResult>> ExecuteBulkFileActionsAsync(IEnumerable<string> fileIds, BulkFileAction action, string? destinationFolder = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (_telegramSession == null || _storageChannel is null || _activeTelegramAccountId == null)
		{
			throw new InvalidOperationException("Connect the active Telegram storage account before applying bulk file actions.");
		}
		TelegramFileTransport transport = CreateTransport(_telegramSession, _storageChannel);
		ManifestBulkActions manifestBulkActions = new ManifestBulkActions(_manifestStore, (FileManifest manifest, CancellationToken token) => CreateManifestPublisher(transport, _storageChannel.AccountId).PublishCommittedAsync(manifest, token));
		IReadOnlyList<BulkFileActionResult> result;
		try
		{
			result = await manifestBulkActions.ExecuteAsync(fileIds, action, destinationFolder, _activeTelegramAccountId, cancellationToken);
		}
		finally
		{
			await RefreshManifestsAsync();
		}
		return result;
	}

	private void FileSearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		ApplyManifestFilterAndSort();
	}

	private void FileSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplyManifestFilterAndSort();
	}

	private void FileAvailabilityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplyManifestFilterAndSort();
	}

	private void FilesScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplyManifestFilterAndSort();
	}

	private async void FavoriteFile_Click(object sender, RoutedEventArgs e)
	{
		await UpdateSelectedMetadataAsync((FileManifest manifest) => FileManifestMetadata.SetFavorite(manifest, !manifest.IsFavorite), (FileManifest manifest) => string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get(manifest.IsFavorite ? "action.favoriteAdded" : "action.favoriteRemoved"), manifest.FileName));
	}

	private async void ArchiveFile_Click(object sender, RoutedEventArgs e)
	{
		await UpdateSelectedMetadataAsync((FileManifest manifest) => FileManifestMetadata.SetArchived(manifest, !manifest.IsArchived), (FileManifest manifest) => string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get(manifest.IsArchived ? "action.archived" : "action.unarchived"), manifest.FileName));
	}

	private async void HideFile_Click(object sender, RoutedEventArgs e)
	{
		await UpdateSelectedMetadataAsync((FileManifest manifest) => FileManifestMetadata.SetHidden(manifest, !manifest.IsHidden), (FileManifest manifest) => string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get(manifest.IsHidden ? "action.hidden" : "action.unhidden"), manifest.FileName));
	}

	private async Task UpdateSelectedMetadataAsync(Func<FileManifest, FileManifest> update, Func<FileManifest, string> successText)
	{
		var selectedManifestItem = SelectedManifestItem;
		if (_operationBusy || selectedManifestItem is null || !selectedManifestItem.Manifest.Committed || selectedManifestItem.Manifest.IsInTrash)
		{
			return;
		}
		try
		{
			FileManifest fileManifest = update(selectedManifestItem.Manifest);
			await PublishMetadataRevisionAsync(fileManifest, successText(fileManifest));
		}
		catch (Exception ex)
		{
			StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message);
		}
	}

	private void ApplyManifestFilterAndSort()
	{
		string search;
		if (ManifestList != null && TrashManifestList != null)
		{
			search = SearchBox?.Text?.Trim() ?? string.Empty;
			int scope = FilesScopeBox?.SelectedIndex ?? 0;
			bool hasActiveFilter = !string.IsNullOrWhiteSpace(search) || (FilterBox?.SelectedIndex ?? 0) > 0 ||
				(TypeFilterBox?.SelectedIndex ?? 0) > 0 || (!_isTrashPage && scope > 0);
			ManifestItem[] array = FilterAndSort(from item in _manifestItems
				where !item.Manifest.IsInTrash
				where string.IsNullOrEmpty(_currentFolderPath) || string.Equals(item.Manifest.FolderPath, _currentFolderPath, StringComparison.OrdinalIgnoreCase)
				where scope switch
				{
					1 => item.Manifest.IsFavorite && !item.Manifest.IsHidden, 
					2 => item.Manifest.IsArchived && !item.Manifest.IsHidden, 
					3 => item.Manifest.IsHidden, 
					_ => !item.Manifest.IsArchived && !item.Manifest.IsHidden, 
				}
				select item).ToArray();
			ManifestItem[] array2 = FilterAndSort(_manifestItems.Where((ManifestItem item) => item.Manifest.IsInTrash)).ToArray();
			ManifestList.ItemsSource = array;
			TrashManifestList.ItemsSource = array2;
			string emptyTextKey;
			if (array.Length != 0)
			{
				emptyTextKey = "files.empty";
			}
			else if (!hasActiveFilter)
			{
				emptyTextKey = (_manifestItems.Any((ManifestItem item) => !item.Manifest.IsInTrash) ? "files.folderEmpty" : "files.empty");
			}
			else
			{
				emptyTextKey = "files.searchEmpty";
			}
			if (FilesEmptyState != null)
			{
				FilesEmptyState.Visibility = ((array.Length != 0) ? Visibility.Collapsed : Visibility.Visible);
			}
			if (FilesEmptyText != null)
			{
				FilesEmptyText.Text = UiText.Instance.Get(emptyTextKey);
			}
			if (TrashEmptyState != null)
			{
				TrashEmptyState.Visibility = ((array2.Length != 0) ? Visibility.Collapsed : Visibility.Visible);
			}
			if (TrashEmptyText != null) TrashEmptyText.Text = hasActiveFilter ? UiText.Instance.Get("files.searchEmpty") : UiText.Instance.Get("empty.trash");
			bool isTrashPage = _isTrashPage;
			TextBlock breadcrumbFolder = BreadcrumbFolder;
			string text = (breadcrumbFolder.Text = ((!isTrashPage) ? ((_currentFolderPath.Length == 0) ? string.Empty : (" › " + _currentFolderPath.Replace("/", " › ", StringComparison.Ordinal))) : string.Empty));
			breadcrumbFolder.Visibility = ((text.Length <= 0) ? Visibility.Collapsed : Visibility.Visible);
			int num;
			if (!isTrashPage)
			{
				Grid filesPage = FilesPage;
				num = ((filesPage != null && filesPage.Visibility == Visibility.Visible) ? 1 : 0);
			}
			else
			{
				num = 0;
			}
			bool flag = (byte)num != 0;
			if (FolderTreeArea != null)
			{
				FolderTreeArea.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
			}
			if (FolderTree != null)
			{
				FolderTree.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
			}
			ApplyActionAvailability();
		}
		IEnumerable<ManifestItem> FilterAndSort(IEnumerable<ManifestItem> source)
		{
			if (!string.IsNullOrEmpty(search))
			{
				source = source.Where((ManifestItem item) => item.Manifest.FileName.Contains(search, StringComparison.OrdinalIgnoreCase));
			}
			source = FilterBox?.SelectedIndex switch
			{
				1 => source.Where((ManifestItem item) => item.HasAllPartFiles), 
				2 => source.Where((ManifestItem item) => item.LocalPartFilesPresent > 0 && !item.HasAllPartFiles), 
				3 => source.Where((ManifestItem item) => item.LocalPartFilesPresent == 0), 
				4 => source.Where((ManifestItem item) => item.IsVerifiedOffline), 
				5 => source.Where((ManifestItem item) => !item.IsVerifiedOffline), 
				_ => source, 
			};
			int typeFilter = TypeFilterBox?.SelectedIndex ?? 0;
			if (typeFilter > 0) source = source.Where(item => ExplorerFileTypeFilter.Matches(item.Manifest.FileName, typeFilter));
			return SortBox?.SelectedIndex switch
			{
				1 => (IEnumerable<ManifestItem>)source.OrderByDescending((ManifestItem item) => item.Manifest.FileName, StringComparer.OrdinalIgnoreCase), 
				2 => source.OrderByDescending((ManifestItem item) => item.Manifest.LogicalSize), 
				3 => source.OrderBy((ManifestItem item) => item.Manifest.LogicalSize), 
				4 => source.OrderByDescending((ManifestItem item) => item.Manifest.FileModifiedAtUtc ?? item.Manifest.UpdatedAtUtc), 
				5 => source.OrderBy((ManifestItem item) => item.Manifest.FileModifiedAtUtc ?? item.Manifest.UpdatedAtUtc), 
				_ => source.OrderBy((ManifestItem item) => item.Manifest.FileName, StringComparer.OrdinalIgnoreCase), 
			};
		}
	}

	private void BuildFolderTree(IReadOnlyList<FileManifest> manifests)
	{
		_buildingFolderTree = true;
		try
		{
			FolderTree.Items.Clear();
			TreeViewItem treeViewItem = new TreeViewItem
			{
				Header = UiText.Instance.Get("folder.all"),
				Tag = string.Empty,
				IsExpanded = true
			};
			FolderTree.Items.Add(treeViewItem);
			Dictionary<string, TreeViewItem> dictionary = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = treeViewItem };
			foreach (string item in _localFolderPaths.Concat(from manifest in manifests
				where !manifest.IsInTrash
				select manifest.FolderPath into path
				where !string.IsNullOrWhiteSpace(path)
				select path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase))
			{
				string text = string.Empty;
				string[] array = item.Split('/');
				foreach (string text2 in array)
				{
					string text3 = ((text.Length == 0) ? text2 : (text + "/" + text2));
					if (!dictionary.ContainsKey(text3))
					{
						TreeViewItem treeViewItem2 = new TreeViewItem
						{
							Header = text2,
							Tag = text3
						};
						TreeViewItem treeViewItem3 = treeViewItem2;
						dictionary[text3] = treeViewItem2;
						TreeViewItem treeViewItem4 = treeViewItem3;
						ContextMenu contextMenu = new ContextMenu();
						MenuItem menuItem = new MenuItem
						{
							Header = UiText.Instance.Get("folder.rename"),
							Tag = text3
						};
						menuItem.Click += FolderRename_Click;
						MenuItem menuItem2 = new MenuItem
						{
							Header = UiText.Instance.Get("folder.delete"),
							Tag = text3
						};
						menuItem2.Click += FolderDelete_Click;
						contextMenu.Items.Add(menuItem);
						contextMenu.Items.Add(menuItem2);
						treeViewItem4.ContextMenu = contextMenu;
						dictionary[text].Items.Add(treeViewItem4);
					}
					text = text3;
				}
			}
			if (!dictionary.TryGetValue(_currentFolderPath, out var value))
			{
				value = treeViewItem;
			}
			value.IsSelected = true;
			value.BringIntoView();
			_currentFolderPath = (value.Tag as string) ?? string.Empty;
		}
		finally
		{
			_buildingFolderTree = false;
		}
	}

	private void FolderTree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
	{
		if (!_buildingFolderTree && !_isTrashPage && e.NewValue is TreeViewItem treeViewItem)
		{
			_currentFolderPath = (treeViewItem.Tag as string) ?? string.Empty;
			ApplyManifestFilterAndSort();
		}
	}

	private void ShowTrash_Changed(object sender, RoutedEventArgs e)
	{
		_currentFolderPath = string.Empty;
		ApplyManifestFilterAndSort();
	}

	private void UpFolder_Click(object sender, RoutedEventArgs e)
	{
		if (_currentFolderPath.Length != 0)
		{
			int num = _currentFolderPath.LastIndexOf('/');
			string path = ((num < 0) ? string.Empty : _currentFolderPath.Substring(0, num));
			var treeViewItem = FindFolderNode(FolderTree.Items, path);
			if (treeViewItem != null)
			{
				treeViewItem.IsSelected = true;
			}
		}
	}

	private static TreeViewItem? FindFolderNode(ItemCollection items, string path)
	{
		foreach (TreeViewItem item in items.OfType<TreeViewItem>())
		{
			if (string.Equals(item.Tag as string, path, StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
			var treeViewItem = FindFolderNode(item.Items, path);
			if (treeViewItem != null)
			{
				return treeViewItem;
			}
		}
		return null;
	}

	private async Task RefreshQueueAsync()
	{
		var selectedId = (TransferList.SelectedItem as QueueItemView)?.Item.TaskId;
		HashSet<string> visibleFileIds = _manifestItems.Select((ManifestItem item) => item.Manifest.FileId).ToHashSet(StringComparer.Ordinal);
		TransferQueueItem[] source = (await _transferQueueStore.ListAsync(CancellationToken.None)).Where((TransferQueueItem item) => visibleFileIds.Contains(item.FileId)).ToArray();
		HashSet<string> currentIds = source.Select((TransferQueueItem item) => item.TaskId).ToHashSet(StringComparer.Ordinal);
		string[] array = _queueViews.Keys.Where((string id) => !currentIds.Contains(id)).ToArray();
		foreach (string key in array)
		{
			_queueViews.Remove(key);
		}
		QueueItemView[] array2 = source.Select((TransferQueueItem item) =>
		{
			if (_queueViews.TryGetValue(item.TaskId, out var value) && value != null)
			{
				value.UpdateItem(item);
				return value;
			}
			QueueItemView queueItemView = new QueueItemView(item);
			_queueViews[item.TaskId] = queueItemView;
			return queueItemView;
		}).ToArray();
		ICollectionView defaultView = CollectionViewSource.GetDefaultView(array2);
		defaultView.GroupDescriptions.Clear();
		defaultView.GroupDescriptions.Add(new PropertyGroupDescription("GroupLabel"));
		TransferList.ItemsSource = defaultView;
		TransfersEmptyState.Visibility = ((array2.Length != 0) ? Visibility.Collapsed : Visibility.Visible);
		if (selectedId != null)
		{
			TransferList.SelectedItem = array2.FirstOrDefault((QueueItemView item) => item.Item.TaskId == selectedId);
		}
		ApplyActionAvailability();
	}

	private bool IsVisibleToActiveAccount(FileManifest manifest)
	{
		return ManifestAccountScope.IsVisible(manifest, _activeTelegramAccountId);
	}

	private void UpdateLastSyncText(DateTimeOffset? syncedAtUtc)
	{
		_lastSyncAtUtc = syncedAtUtc;
		LocalizedTextBlock lastSyncText = LastSyncText;
		string text;
		if (syncedAtUtc.HasValue)
		{
			DateTimeOffset valueOrDefault = syncedAtUtc.GetValueOrDefault();
			text = UiText.Instance.Get("status.lastSyncPrefix") + valueOrDefault.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
		}
		else
		{
			text = UiText.Instance.Get("status.lastSync");
		}
		lastSyncText.Text = text;
	}

	private void SetBusy(bool busy, string? message = null, bool canCancel = true, string cancelButtonTextKey = "status.cancel")
	{
		if (busy && !_operationBusy)
		{
			_operationCanCancel = canCancel;
			_operationCancellation = new CancellationTokenSource();
			_operationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}
		else if (!busy && _operationBusy)
		{
			_operationCanCancel = false;
			_operationCancellation?.Dispose();
			_operationCancellation = null;
			_operationFinished?.TrySetResult();
			_operationFinished = null;
		}
		_operationBusy = busy;
		_cancelButtonTextKey = cancelButtonTextKey;
		CancelOperationButton.Content = UiText.Instance.Get(cancelButtonTextKey);
		if (UploadButton != null)
		{
			UploadButton.IsEnabled = !busy && !_shutdownStarted;
		}
		PartSizeBox.IsEnabled = !busy && !_shutdownStarted;
		ForceMultipartCheck.IsEnabled = !busy && !_shutdownStarted;
        ApplyDedupAvailability();
		ManifestList.IsEnabled = !busy && !_shutdownStarted;
		TrashManifestList.IsEnabled = !busy && !_shutdownStarted;
		TransferList.IsEnabled = !busy && !_shutdownStarted;
		FolderTree.IsEnabled = !busy && !_shutdownStarted;
		SearchBox.IsEnabled = !busy && !_shutdownStarted;
		SortBox.IsEnabled = !busy && !_shutdownStarted;
		FilterBox.IsEnabled = !busy && !_shutdownStarted;
		TypeFilterBox.IsEnabled = !busy && !_shutdownStarted;
		VerifyButton.IsEnabled = !busy && !_shutdownStarted && SelectedManifestItem is not null;
		ParallelTransfersBox.IsEnabled = !busy && !_shutdownStarted;
		CancelOperationButton.Visibility = ((!busy || !_operationCanCancel || _shutdownStarted) ? Visibility.Collapsed : Visibility.Visible);
		CancelOperationButton.IsEnabled = busy && _operationCanCancel && !_shutdownStarted;
		ApplyActionAvailability();
		if (message != null)
		{
			StatusText.Text = message;
		}
	}

	private void EnableOperationCancellation()
	{
		_operationCanCancel = true;
		if (_shutdownStarted)
		{
			_operationCancellation?.Cancel();
		}
		else if (_operationBusy)
		{
			CancelOperationButton.Visibility = Visibility.Visible;
			CancelOperationButton.IsEnabled = true;
		}
	}

	private void ApplyActionAvailability()
	{
		if (!_legacyLocalDataView) RestoreLegacyLocalViewControls();
		ApplyCacheActionAvailability();
		ApplyFolderRecoveryActionAvailability();
		ApplyMetadataActionAvailability();
		ApplySyncActionAvailability();
		ApplyVaultActionAvailability();
        ApplyMetadataEncryptionAvailability();
        ApplyLocalDatabaseAvailability();
		var telegramSession = _telegramSession;
		var storageChannel = _storageChannel;
		bool flag = telegramSession != null;
		bool flag2 = telegramSession?.CurrentAuthorizationState == "authorizationStateReady";
		bool flag3 = flag;
		if (flag3)
		{
			bool flag4;
			switch (telegramSession?.CurrentAuthorizationState)
			{
			case "authorizationStateWaitPhoneNumber":
			case "authorizationStateWaitCode":
			case "authorizationStateWaitPassword":
			case "authorizationStateWaitEmailAddress":
			case "authorizationStateWaitEmailCode":
				flag4 = true;
				break;
			default:
				flag4 = false;
				break;
			}
			flag3 = flag4;
		}
		bool flag5 = flag3;
		bool flag6 = telegramSession?.AuthenticationInputPending ?? false;
		var flag7 = flag2 && _telegramAccountLoaded && storageChannel is not null;
		bool flag8 = !_operationBusy && !_shutdownStarted;
		Button telegramLoginButton = TelegramLoginButton;
		UiText instance = UiText.Instance;
		string key;
		if (flag6)
		{
			key = "auth.waiting";
		}
		else
		{
			key = (flag ? "auth.continue" : "auth.login");
		}
		string value = (string)(telegramLoginButton.ToolTip = instance.Get(key));
		AutomationProperties.SetName(telegramLoginButton, value);
		TelegramLoginButton.IsEnabled = flag8 && !flag6 && (!flag | flag5);
		TelegramLogoutButton.IsEnabled = flag8 & flag2;
		var flag9 = storageChannel is not null;
		TelegramStorageChannelButton.Visibility = ((flag9 || _storageConnectionInProgress) ? Visibility.Collapsed : Visibility.Visible);
		TelegramStorageChannelButton.IsEnabled = (flag8 & flag2) && _telegramAccountLoaded && !_storageConnectionInProgress;
		LocalizedTextBlock telegramStorageConnectedText = TelegramStorageConnectedText;
		string text2;
		if (storageChannel is null)
		{
			text2 = (_storageConnectionInProgress ? "Connecting private storage..." : string.Empty);
		}
		else
		{
			text2 = "Connected to private storage · " + storageChannel.Title;
		}
		telegramStorageConnectedText.Text = text2;
		TelegramStorageConnectedText.Visibility = ((!flag9 && !_storageConnectionInProgress) ? Visibility.Collapsed : Visibility.Visible);
		UploadButton.IsEnabled = flag8;
		PrepareFileButton.IsEnabled = flag8;
		EnqueueUploadButton.IsEnabled = flag8 & flag7;
		CloseUploadSelectionButton.IsEnabled = flag8;
		var selectedManifestItem = SelectedManifestItem;
		ManifestItem[] array = TrashManifestList?.SelectedItems.Cast<ManifestItem>().Where((ManifestItem item) => item.Manifest.Committed && item.Manifest.IsInTrash).ToArray() ?? Array.Empty<ManifestItem>();
		ManifestItem[] array2 = ManifestList?.SelectedItems.Cast<ManifestItem>().Where((ManifestItem item) => item.Manifest.Committed && !item.Manifest.IsInTrash).ToArray() ?? Array.Empty<ManifestItem>();
		if (BulkSelectionCount != null)
		{
			BulkSelectionCount.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("selection.count"), ManifestList?.SelectedItems.Count ?? 0);
		}
		if (BulkActionsPanel != null)
		{
			BulkActionsPanel.Visibility = ((array2.Length <= 1) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (BulkMoveButton != null)
		{
			BulkMoveButton.IsEnabled = (flag8 & flag7) && array2.Length > 1;
		}
		if (BulkTrashButton != null)
		{
			BulkTrashButton.IsEnabled = (flag8 & flag7) && array2.Length > 1;
		}
		bool flag10 = array2.Length != 0 && array2.All(CanRemoveLocalEntry);
		if (BulkRemoveLocalEntriesButton != null)
		{
			BulkRemoveLocalEntriesButton.Visibility = ((!((array2.Length > 1) & flag10)) ? Visibility.Collapsed : Visibility.Visible);
			BulkRemoveLocalEntriesButton.IsEnabled = (flag8 & flag10) && !array2.Any((ManifestItem item) => HasActiveTransfer(item.Manifest.FileId));
		}
		ManifestItem[] array3 = ManifestList?.SelectedItems.Cast<ManifestItem>().ToArray() ?? Array.Empty<ManifestItem>();
		bool flag11 = array3.Length == 1 && CanRemoveLocalEntry(array3[0]);
		if (RemoveLocalEntryButton != null)
		{
			RemoveLocalEntryButton.Visibility = ((!flag11) ? Visibility.Collapsed : Visibility.Visible);
			RemoveLocalEntryButton.IsEnabled = (flag8 & flag11) && !HasActiveTransfer(array3[0].Manifest.FileId);
		}
		bool flag12 = array.Length != 0 && array.All(CanRemoveLocalEntry);
		if (TrashRemoveLocalEntriesButton != null)
		{
			TrashRemoveLocalEntriesButton.Visibility = ((!flag12) ? Visibility.Collapsed : Visibility.Visible);
			TrashRemoveLocalEntriesButton.IsEnabled = (flag8 & flag12) && !array.Any((ManifestItem item) => HasActiveTransfer(item.Manifest.FileId));
		}
		if (CreateFolderButton != null)
		{
			CreateFolderButton.IsEnabled = flag8;
		}
		Button restoreButton = RestoreButton;
		int num;
		if (flag8)
		{
			if (selectedManifestItem is not null && selectedManifestItem.HasAllPartFiles)
			{
				FileManifest manifest = selectedManifestItem.Manifest;
				if (manifest is not null)
				{
					num = ((!manifest.IsInTrash) ? 1 : 0);
					goto IL_04f7;
				}
			}
			num = 0;
		}
		else
		{
			num = 0;
		}
		goto IL_04f7;
		IL_09de:
		TransferQueueItem? transferQueueItem;
		if (TransferDetailPanel != null && transferQueueItem != null)
		{
			TransferDetailPanel.Visibility = Visibility.Visible;
		}
		flag3 = flag8;
		if (flag3)
		{
			switch (transferQueueItem?.State)
			{
			case TransferQueueState.Pending:
			case TransferQueueState.Paused:
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
		}
		if (CancelQueueButton != null)
		{
			CancelQueueButton.IsEnabled = flag3;
		}
		if (QueuePauseButton != null)
		{
			QueuePauseButton.IsEnabled = _operationBusy && _operationCanCancel;
		}
		if (QueueClearDoneButton != null)
		{
			QueueClearDoneButton.IsEnabled = flag8 && _queueViews.Values.Any((QueueItemView view) =>
			{
				TransferQueueState state = view.Item.State;
				return (uint)(state - 4) <= 1u;
			});
		}
        ApplyCopyFallbackAvailability();
		ApplyLegacyLocalDataViewRestrictions();
		return;
		IL_09d9:
		Button relinkUploadSourceButton;
		int isEnabled;
		relinkUploadSourceButton.IsEnabled = (byte)isEnabled != 0;
		goto IL_09de;
		IL_04f7:
		restoreButton.IsEnabled = (byte)num != 0;
		if (PermanentDeleteButton != null)
		{
			PermanentDeleteButton.IsEnabled = (flag8 & flag7) && array.Length != 0;
		}
		VerifyButton.IsEnabled = flag8 && selectedManifestItem is not null && selectedManifestItem.LocalPartFilesPresent > 0;
		var fileManifest = selectedManifestItem?.Manifest;
		if (FileDetailPanel != null)
		{
			FileDetailPanel.Visibility = (((ManifestList?.SelectedItems.Count ?? 0) > 1 || !(selectedManifestItem != null)) ? Visibility.Collapsed : Visibility.Visible);
			if (selectedManifestItem is not null)
			{
				DetailFileName.Text = selectedManifestItem.Manifest.FileName;
				DetailMeta.Text = selectedManifestItem.FormattedSize + " • " + selectedManifestItem.FormattedDate;
				DetailCacheStatus.Text = selectedManifestItem.StatusLabel;
			}
		}
		PreviewButton.IsEnabled = flag8 && selectedManifestItem != null && (selectedManifestItem.HasAllPartFiles || (selectedManifestItem.Manifest.Committed & flag7));
		if (RestoreLocalButton != null)
		{
			var canRestoreLocal = flag8 && selectedManifestItem is not null && selectedManifestItem.HasAllPartFiles && !selectedManifestItem.Manifest.IsInTrash;
			RestoreLocalButton.Visibility = canRestoreLocal ? Visibility.Visible : Visibility.Collapsed;
			RestoreLocalButton.IsEnabled = canRestoreLocal;
		}
		DownloadButton.IsEnabled = (flag8 & flag7) && (fileManifest?.Committed ?? false);
		QueueStartButton.IsEnabled = (flag8 & flag7) && _queueViews.Values.Any((QueueItemView view) =>
		{
			TransferQueueState state = view.Item.State;
			return (state == TransferQueueState.Pending || state == TransferQueueState.Paused) ? true : false;
		});
		RenameButton.IsEnabled = (flag8 & flag7) && fileManifest is not null && fileManifest.Committed && !fileManifest.IsInTrash;
		MoveButton.IsEnabled = (flag8 & flag7) && fileManifest is not null && fileManifest.Committed && !fileManifest.IsInTrash;
		FavoriteButton.IsEnabled = (flag8 & flag7) && fileManifest is not null && fileManifest.Committed && !fileManifest.IsInTrash && !_isTrashPage;
		FavoriteButton.Content = UiText.Instance.Get((fileManifest is not null && fileManifest.IsFavorite) ? "action.removeFavorite" : "action.addFavorite");
		ArchiveButton.IsEnabled = (flag8 & flag7) && fileManifest is not null && fileManifest.Committed && !fileManifest.IsInTrash && !_isTrashPage;
		ArchiveButton.Content = UiText.Instance.Get((fileManifest is not null && fileManifest.IsArchived) ? "action.unarchive" : "action.archive");
		HiddenButton.IsEnabled = (flag8 & flag7) && fileManifest is not null && fileManifest.Committed && !fileManifest.IsInTrash && !_isTrashPage;
		HiddenButton.Content = UiText.Instance.Get((fileManifest is not null && fileManifest.IsHidden) ? "action.unhide" : "action.hide");
		TrashButton.IsEnabled = (flag8 & flag7) && (fileManifest?.Committed ?? false);
		TrashButton.Content = UiText.Instance.Get((fileManifest is not null && fileManifest.IsInTrash) ? "trash.restore" : "action.moveToTrash");
		TrashButton.Visibility = (_isTrashPage ? Visibility.Collapsed : Visibility.Visible);
		RestoreButton.IsEnabled = (flag8 & flag7) && fileManifest is not null && fileManifest.Committed && fileManifest.IsInTrash;
		transferQueueItem = (TransferList.SelectedItem as QueueItemView)?.Item;
		var fileManifest2 = ((transferQueueItem is null) ? null : _manifestItems.FirstOrDefault((ManifestItem item) => string.Equals(item.Manifest.FileId, transferQueueItem.FileId, StringComparison.Ordinal))?.Manifest);
		if (RetryQueueButton != null)
		{
			RetryQueueButton.IsEnabled = false;
		}
		if (RelinkUploadSourceButton != null)
		{
			RelinkUploadSourceButton.IsEnabled = false;
		}
		if (CancelQueueButton != null)
		{
			CancelQueueButton.IsEnabled = false;
		}
		if (TransferDetailPanel != null)
		{
			TransferDetailPanel.Visibility = Visibility.Collapsed;
		}
		flag3 = flag8 & flag7;
		if (flag3)
		{
			bool flag13;
			switch (transferQueueItem?.State)
			{
			case TransferQueueState.Failed:
			case TransferQueueState.Cancelled:
				flag13 = true;
				break;
			default:
				flag13 = false;
				break;
			}
			flag3 = flag13;
		}
		if (RetryQueueButton != null)
		{
			RetryQueueButton.IsEnabled = flag3;
		}
		if (RelinkUploadSourceButton != null)
		{
			relinkUploadSourceButton = RelinkUploadSourceButton;
			if (flag3)
			{
				var transferQueueItem2 = transferQueueItem;
				if (transferQueueItem2 is not null && transferQueueItem2.Direction == TransferDirection.Upload)
				{
					isEnabled = ((fileManifest2 is not null && fileManifest2.Encryption is null) ? 1 : 0);
					goto IL_09d9;
				}
			}
			isEnabled = 0;
			goto IL_09d9;
		}
		goto IL_09de;
	}

	private void ApplyLegacyLocalDataViewRestrictions()
	{
		if (!_legacyLocalDataView) return;
		_legacyLocalViewControlStates ??= new Dictionary<DependencyObject, bool>();
		var pending = new Stack<DependencyObject>();
		pending.Push(this);
		while (pending.Count != 0)
		{
			var current = pending.Pop();
			if (current is System.Windows.Controls.Primitives.ButtonBase button && button is not RadioButton &&
				button != RestoreLocalButton && button != ReturnToVaultButton && button != TelegramLogoutButton &&
				button != AppLockNowButton && button != CancelOperationButton && !AppLockOverlay.IsAncestorOf(button))
			{
				_legacyLocalViewControlStates.TryAdd(button, button.IsEnabled);
				button.IsEnabled = false;
			}
			if (current is MenuItem menuItem)
			{
				_legacyLocalViewControlStates.TryAdd(menuItem, menuItem.IsEnabled);
				menuItem.IsEnabled = false;
			}
			foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Push(child);
		}
	}

	private void RestoreLegacyLocalViewControls()
	{
		if (_legacyLocalViewControlStates is null) return;
		foreach (var pair in _legacyLocalViewControlStates)
		{
			if (pair.Key is System.Windows.Controls.Primitives.ButtonBase button) button.IsEnabled = pair.Value;
			else if (pair.Key is MenuItem menuItem) menuItem.IsEnabled = pair.Value;
		}
		_legacyLocalViewControlStates = null;
	}

	private bool RejectLegacyLocalDataMutation()
	{
		if (!_legacyLocalDataView) return false;
		StatusText.Text = UiText.Instance.Get("vault.viewLegacyOpened");
		return true;
	}

	private void CancelOperation_Click(object sender, RoutedEventArgs e)
	{
		if (_operationBusy && _operationCanCancel)
		{
			CancelOperationButton.IsEnabled = false;
			StatusText.Text = UiText.Instance.Get((_cancelButtonTextKey == "queue.pause") ? "queue.pauseRequested" : "status.cancelRequested");
			_operationCancellation?.Cancel();
		}
	}

	private async void MainWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (_allowClose)
		{
			return;
		}
		e.Cancel = true;
		if (_shutdownStarted)
		{
			return;
		}
		_shutdownStarted = true;
		CancelOperationButton.IsEnabled = false;
		CancelOperationButton.Visibility = Visibility.Collapsed;
		if (_operationBusy && _operationCanCancel)
		{
			StatusText.Text = "Stopping the current operation before closing...";
			_operationCancellation?.Cancel();
		}
		else if (_operationBusy)
		{
			StatusText.Text = "Finishing storage setup before closing...";
		}
		else
		{
			StatusText.Text = "Closing Telegram session safely...";
		}
		try
		{
			if (_operationBusy)
			{
				var operationFinished = _operationFinished;
				if (operationFinished != null)
				{
					await operationFinished.Task;
				}
			}
			await DisposeTelegramSessionAsync();
		}
		finally
		{
			_allowClose = true;
			// Closing was canceled above. Defer the second close until the original Closing event unwinds;
			// session disposal may complete synchronously and WPF rejects a nested Close call.
			_ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
		}
	}

	private void NavFiles_Click(object sender, RoutedEventArgs e)
	{
		NavigateToPage("files");
	}

	private void NavTransfers_Click(object sender, RoutedEventArgs e)
	{
		NavigateToPage("transfers");
	}

	private void NavTrash_Click(object sender, RoutedEventArgs e)
	{
		NavigateToPage("trash");
	}

	private void NavSettings_Click(object sender, RoutedEventArgs e)
	{
		NavigateToPage("settings");
	}

	private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (_applicationLocked)
		{
			if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2 or Key.D3 or Key.NumPad3 or Key.D4 or Key.NumPad4)
				e.Handled = true;
			return;
		}
		if (Keyboard.Modifiers == ModifierKeys.Control && AuthenticationFlowPanel.Visibility != Visibility.Visible)
		{
			string? text;
			switch (e.Key)
			{
			case Key.D1:
			case Key.NumPad1:
				text = "files";
				break;
			case Key.D2:
			case Key.NumPad2:
				text = "transfers";
				break;
			case Key.D3:
			case Key.NumPad3:
				text = "trash";
				break;
			case Key.D4:
			case Key.NumPad4:
				text = "settings";
				break;
			default:
				text = null;
				break;
			}
			var text2 = text;
			if (text2 != null)
			{
				NavigateToPage(text2);
				(text2 switch
				{
					"files" => NavFiles, 
					"transfers" => NavTransfers, 
					"trash" => NavTrash, 
					"settings" => NavSettings, 
					_ => null, 
				})?.Focus();
				e.Handled = true;
			}
		}
	}

	private void ParallelTransfersBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ParallelTransfers_SelectionChanged(sender, e);
	}

	private void NavigateToPage(string page)
	{
		if (NavFiles != null)
		{
			NavFiles.IsChecked = page == "files";
		}
		if (NavTransfers != null)
		{
			NavTransfers.IsChecked = page == "transfers";
		}
		if (NavTrash != null)
		{
			NavTrash.IsChecked = page == "trash";
		}
		if (NavSettings != null)
		{
			NavSettings.IsChecked = page == "settings";
		}
		_isTrashPage = page == "trash";
		if (FilesPage != null)
		{
			FilesPage.Visibility = ((!(page == "files")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (TransfersPage != null)
		{
			TransfersPage.Visibility = ((!(page == "transfers")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (TrashPage != null)
		{
			TrashPage.Visibility = ((!(page == "trash")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (SettingsPage != null)
		{
			SettingsPage.Visibility = ((!(page == "settings")) ? Visibility.Collapsed : Visibility.Visible);
		}
		UpdatePageTitle(page);
		if (SearchBox != null)
		{
			SearchBox.Visibility = ((!(page == "files") && !(page == "trash")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (SortBox != null)
		{
			SortBox.Visibility = ((!(page == "files") && !(page == "trash")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (FilterBox != null)
		{
			FilterBox.Visibility = ((!(page == "files") && !(page == "trash")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (TypeFilterBox != null)
		{
			TypeFilterBox.Visibility = ((!(page == "files") && !(page == "trash")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (FilesScopeBox != null)
		{
			FilesScopeBox.Visibility = ((!(page == "files")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (TopActions != null)
		{
			TopActions.Visibility = ((!(page == "files") && !(page == "trash")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (CreateFolderButton != null)
		{
			CreateFolderButton.Visibility = ((!(page == "files")) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (UploadButton != null)
		{
			UploadButton.Visibility = ((!(page == "files")) ? Visibility.Collapsed : Visibility.Visible);
		}
		ApplyActionAvailability();
		ApplyManifestFilterAndSort();
	}

	private void UpdatePageTitle(string? page = null)
	{
		if (PageTitle == null)
		{
			return;
		}
		if (page == null)
		{
			RadioButton navFiles = NavFiles;
			object obj;
			if (navFiles == null || navFiles.IsChecked != true)
			{
				RadioButton navTransfers = NavTransfers;
				if (navTransfers == null || navTransfers.IsChecked != true)
				{
					RadioButton navTrash = NavTrash;
					if (navTrash == null || navTrash.IsChecked != true)
					{
						RadioButton navSettings = NavSettings;
						obj = ((navSettings != null && navSettings.IsChecked == true) ? "settings" : string.Empty);
					}
					else
					{
						obj = "trash";
					}
				}
				else
				{
					obj = "transfers";
				}
			}
			else
			{
				obj = "files";
			}
			page = (string?)obj;
		}
		TextBlock pageTitle = PageTitle;
		pageTitle.Text = page switch
		{
			"files" => UiText.Instance.Get("nav.files"), 
			"transfers" => UiText.Instance.Get("nav.transfers"), 
			"trash" => UiText.Instance.Get("nav.trash"), 
			"settings" => UiText.Instance.Get("nav.settings"), 
			_ => string.Empty, 
		};
	}

	private void EnqueueUpload_Click(object sender, RoutedEventArgs e)
	{
		TelegramUpload_Click(sender, e);
	}

	private async void PreviewFile_Click(object sender, RoutedEventArgs e)
	{
		var item = SelectedManifestItem;
		if (item == null || _operationBusy || _shutdownStarted)
		{
			return;
		}
		if (FilePreviewWindow.SupportsStreamingPreview(item.Manifest.FileName))
		{
			var manifest = item.Manifest;
			if (!manifest.Committed || manifest.IsInTrash)
			{
				MessageBox.Show(this, UiText.Instance.Get("preview.media.uncommitted"), UiText.Instance.Get("preview.title"),
					MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			var session = _telegramSession;
			var channel = _storageChannel;
			if (session is null || channel is null || (manifest.AccountId is not null && !string.Equals(manifest.AccountId, channel.AccountId, StringComparison.Ordinal)))
			{
				MessageBox.Show(this, UiText.Instance.Get("preview.media.connect"), UiText.Instance.Get("preview.title"),
					MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			byte[]? fileKey = null;
			if (manifest.Encryption is { } encryption)
			{
				SetBusy(true, UiText.Instance.Get("encryption.unlock.title"));
				try
				{
					var passphrase = await RequestRecoveryPassphraseAsync(manifest, OperationToken);
					var keyToken = OperationToken;
					fileKey = await Task.Run(() => AesGcmFileCipher.UnwrapFileKey(encryption.RecoveryKey, passphrase), keyToken);
					keyToken.ThrowIfCancellationRequested();
				}
				catch (OperationCanceledException)
				{
					if (fileKey is not null) CryptographicOperations.ZeroMemory(fileKey);
					return;
				}
				catch (Exception exception) when (exception is CryptographicException or InvalidDataException or ArgumentException)
				{
					MessageBox.Show(this, UiText.Instance.Get("preview.media.unlockFailed"), UiText.Instance.Get("encryption.unlock.title"),
						MessageBoxButton.OK, MessageBoxImage.Warning);
					return;
				}
				finally { SetBusy(false); }
			}
			if (_shutdownStarted || _operationBusy || !ReferenceEquals(_telegramSession, session) || _storageChannel?.ChatId != channel.ChatId ||
				!string.Equals(_storageChannel?.AccountId, channel.AccountId, StringComparison.Ordinal) ||
				!string.Equals(_activeTelegramAccountId, channel.AccountId, StringComparison.Ordinal))
			{
				if (fileKey is not null) CryptographicOperations.ZeroMemory(fileKey);
				MessageBox.Show(this, UiText.Instance.Get("upload.scopeChanged"), UiText.Instance.Get("preview.title"),
					MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			MediaStreamServer? mediaServer = null;
			TelegramManifestMediaByteSource? byteSource = null;
			string? streamWorkspace = null;
			var streamLease = DatabaseProfileLease;
			bool sourceOwnedByServer = false;
			try
			{
				if (streamLease is null)
					throw new InvalidOperationException(UiText.Instance.Get("status.profileNotReady"));
				streamWorkspace = LocalPreviewWorkspace.Create(LocalDataRoot, streamLease);
				byteSource = new TelegramManifestMediaByteSource(session, channel.ChatId, channel.AccountId, manifest, fileKey);
				if (fileKey is not null)
				{
					CryptographicOperations.ZeroMemory(fileKey);
					fileKey = null;
				}
				mediaServer = new MediaStreamServer(_workflow, streamWorkspace);
				var streamUrl = mediaServer.StartServer(manifest.FileName, byteSource);
				sourceOwnedByServer = true;
				var mediaWindow = FilePreviewWindow.CreateStreaming(manifest.FileName, streamUrl, mediaServer, _darkMode);
				mediaWindow.Owner = this;
				var ownedStreamWorkspace = streamWorkspace;
				mediaWindow.Closed += (_, _) => LocalPreviewWorkspace.Delete(LocalDataRoot, ownedStreamWorkspace, streamLease);
				mediaWindow.Show();
			}
			catch (Exception exception)
			{
				if (fileKey is not null) CryptographicOperations.ZeroMemory(fileKey);
				mediaServer?.Dispose();
				if (!sourceOwnedByServer && byteSource is not null) byteSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
				if (streamWorkspace is not null && streamLease is not null)
					LocalPreviewWorkspace.Delete(LocalDataRoot, streamWorkspace, streamLease);
				StatusText.Text = UiText.Instance.LocalizeMessage(exception.Message);
				MessageBox.Show(this, UiText.Instance.LocalizeMessage(exception.Message), UiText.Instance.Get("preview.title"),
					MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
			return;
		}
		bool flag = FilePreviewWindow.SupportsPreview(item.Manifest.FileName);
		if (item.Manifest.LogicalSize > 104857600)
		{
			StatusText.Text = UiText.Instance.Get("preview.tooLarge");
			return;
		}
		if (!flag)
		{
			FilePreviewWindow filePreviewWindow = new FilePreviewWindow(item.Manifest.FileName, null, UiText.Instance.Get("preview.unavailable"), _darkMode);
			filePreviewWindow.Owner = this;
			filePreviewWindow.Show();
			return;
		}
		var previewLease = DatabaseProfileLease;
		if (previewLease is null)
		{
			StatusText.Text = UiText.Instance.Get("status.profileNotReady");
			return;
		}
		string text2 = (flag ? Path.GetExtension(item.Manifest.FileName) : string.Empty);
		string? previewDirectory = null;
		bool previewWindowOwnsFile = false;
		SetBusy(busy: true, UiText.Instance.Get("preview.loading"));
		try
		{
			previewDirectory = LocalPreviewWorkspace.Create(LocalDataRoot, previewLease);
			var temporaryPath = Path.Combine(previewDirectory, "preview" + text2);
			if (item.Manifest.Committed && _telegramSession != null && _storageChannel != null)
			{
				await new FileTransferCoordinator(CreateTransport(_telegramSession, _storageChannel), RequestRecoveryPassphraseAsync).ReassembleAsync(item.Manifest, temporaryPath, OperationToken);
			}
			else
			{
				if (!item.HasAllPartFiles)
				{
					throw new InvalidOperationException("Connect private storage or download all file parts before previewing.");
				}
				if (item.Manifest.Encryption is null)
				{
					await _workflow.RestoreAsync(item.Manifest.FileId, temporaryPath, OperationToken);
				}
				else
				{
					await RestoreEncryptedCachedPartsAsync(item.Manifest, temporaryPath, OperationToken);
				}
			}
			FilePreviewWindow filePreviewWindow2 = await FilePreviewWindow.CreateAsync(item.Manifest.FileName, temporaryPath, _darkMode);
			filePreviewWindow2.Owner = this;
			var ownedPreviewDirectory = previewDirectory;
			filePreviewWindow2.Closed += (_, _) => LocalPreviewWorkspace.Delete(LocalDataRoot, ownedPreviewDirectory, previewLease);
			filePreviewWindow2.Show();
			previewWindowOwnsFile = true;
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = "Preview canceled.";
		}
		catch (Exception ex2)
		{
			StatusText.Text = "Could not preview " + item.Manifest.FileName + ": " + UiText.Instance.LocalizeMessage(ex2.Message);
			MessageBox.Show(this, UiText.Instance.LocalizeMessage(ex2.Message), UiText.Instance.Get("preview.title"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		finally
		{
			if (!previewWindowOwnsFile && previewDirectory is not null)
				LocalPreviewWorkspace.Delete(LocalDataRoot, previewDirectory, previewLease);
			SetBusy(busy: false);
		}
	}

	private async void ManifestList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (!(sender is ListView itemsControl) || !(e.OriginalSource is DependencyObject element) || !(ItemsControl.ContainerFromElement(itemsControl, element) is ListViewItem { DataContext: var dataContext }) || !(dataContext is ManifestItem item))
		{
			return;
		}
		e.Handled = true;
		if (_operationBusy || _shutdownStarted)
		{
			return;
		}
		var openLease = DatabaseProfileLease;
		if (openLease is null)
		{
			StatusText.Text = UiText.Instance.Get("status.profileNotReady");
			return;
		}
		LocalOpenedFileWorkspace.Workspace? openWorkspace = null;
		bool needsAppChoice = Path.GetExtension(item.Manifest.FileName).Equals(".bin", StringComparison.OrdinalIgnoreCase);
		bool opened = false;
		SetBusy(busy: true, string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get(needsAppChoice ? "status.chooseAppForFile" : "status.prepareFileForDefaultApp"), item.Manifest.FileName));
		try
		{
			openWorkspace = LocalOpenedFileWorkspace.Create(LocalDataRoot, openLease, item.Manifest.FileName);
			var openPath = openWorkspace.FilePath;
			if (item.Manifest.Committed && _telegramSession != null && _storageChannel != null)
			{
				await new FileTransferCoordinator(CreateTransport(_telegramSession, _storageChannel), RequestRecoveryPassphraseAsync).ReassembleAsync(item.Manifest, openPath, OperationToken);
			}
			else
			{
				if (!item.HasAllPartFiles)
				{
					throw new InvalidOperationException("Connect private storage or download all file parts before opening this file.");
				}
				if (item.Manifest.Encryption is null)
				{
					await _workflow.RestoreAsync(item.Manifest.FileId, openPath, OperationToken);
				}
				else
				{
					await RestoreEncryptedCachedPartsAsync(item.Manifest, openPath, OperationToken);
				}
			}
			bool flag;
			if (needsAppChoice)
			{
				flag = ShowOpenWithDialog(openPath);
			}
			else
			{
				try
				{
					Process.Start(new ProcessStartInfo
					{
						FileName = openPath,
						UseShellExecute = true
					});
					flag = true;
				}
				catch (Win32Exception ex) when (ex.NativeErrorCode == 1155)
				{
					flag = ShowOpenWithDialog(openPath);
				}
			}
			if (!flag)
			{
				StatusText.Text = "Open canceled.";
				return;
			}
			opened = true;
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("status.openedWithDefaultApp"), item.Manifest.FileName);
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = "Open canceled.";
		}
		catch (Exception ex3)
		{
			StatusText.Text = "Could not open " + item.Manifest.FileName + ": " + UiText.Instance.LocalizeMessage(ex3.Message);
			MessageBox.Show(this, UiText.Instance.LocalizeMessage(ex3.Message), Title, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		finally
		{
			if (!opened)
				if (openWorkspace is not null) LocalOpenedFileWorkspace.Delete(LocalDataRoot, openWorkspace, openLease);
			SetBusy(busy: false);
		}
	}

	private bool ShowOpenWithDialog(string path)
	{
		OpenAsInfo info = new OpenAsInfo
		{
			FilePath = path,
			ClassName = null,
			Flags = 5u
		};
		int num = SHOpenWithDialog(new WindowInteropHelper(this).Handle, ref info);
		if (num >= 0)
		{
			return true;
		}
		if (num == -2147023673)
		{
			return false;
		}
		Marshal.ThrowExceptionForHR(num);
		return false;
	}

	private void DownloadFile_Click(object sender, RoutedEventArgs e)
	{
		TelegramRemoteRestore_Click(sender, e);
	}

	private void RestoreTrashFile_Click(object sender, RoutedEventArgs e)
	{
		Restore_Click(sender, e);
	}

	private void VerifyLocalCache_Click(object sender, RoutedEventArgs e)
	{
		VerifyCache_Click(sender, e);
	}

	private void PrepareFile_Click(object sender, RoutedEventArgs e)
	{
		Stage_Click(sender, e);
	}

	private void UploadButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button button && button.ContextMenu is ContextMenu menu)
		{
			menu.PlacementTarget = button;
			menu.IsOpen = true;
		}
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		ApplyManifestFilterAndSort();
	}

	private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplyManifestFilterAndSort();
	}

	private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplyManifestFilterAndSort();
	}

	private void TypeFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplyManifestFilterAndSort();
	}

	private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
	{
		FolderTree_SelectionChanged(sender, e);
	}

	private void PauseQueue_Click(object sender, RoutedEventArgs e)
	{
		if (_operationBusy && _operationCanCancel)
		{
			if (QueuePauseButton != null)
			{
				QueuePauseButton.IsEnabled = false;
			}
			if (StatusText != null)
			{
				StatusText.Text = UiText.Instance.Get("queue.pauseRequested") ?? "Pausing queue...";
			}
			_operationCancellation?.Cancel();
		}
	}

	private async void ClearDoneQueue_Click(object sender, RoutedEventArgs e)
	{
		if (RejectLegacyLocalDataMutation()) return;
		string[] doneTaskIds = (from view in _queueViews.Values.Where((QueueItemView view) =>
			{
				TransferQueueState state = view.Item.State;
				return (uint)(state - 4) <= 1u;
			})
			select view.Item.TaskId).ToArray();
		if (doneTaskIds.Length == 0 || _operationBusy || _shutdownStarted)
		{
			return;
		}
		SetBusy(busy: true, UiText.Instance.Get("queue.clearing"), canCancel: false);
		try
		{
			await _transferQueueStore.DeleteTasksAsync(doneTaskIds, CancellationToken.None);
			await RefreshQueueAsync();
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("queue.clearedCount"), doneTaskIds.Length);
		}
		catch (Exception ex)
		{
			StatusText.Text = "Could not clear finished transfers: " + ex.Message;
		}
		finally
		{
			SetBusy(busy: false);
		}
	}
}
