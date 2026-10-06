using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class MetadataVersionsPanel : UserControl
{
    public sealed record VersionRow(string Fingerprint, string FileName, string Location, string Organization, string Revision)
    {
        public string AccessibleSummary => $"{FileName}; {Location}; {Organization}; {Revision}";
    }

    private MetadataConflictHistory? _history;
    private FileManifest? _current;
    private bool _canApply;
    private bool _listening;
    public event EventHandler? ApplyRequested;
    public event EventHandler? PrepareRequested;
    public event EventHandler? CloseRequested;
    public string? SelectedFingerprint => (VersionsList.SelectedItem as VersionRow)?.Fingerprint;

    public MetadataVersionsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_listening) { UiText.Instance.LanguageChanged += LanguageChanged; _listening = true; } RebuildRows(); };
        Unloaded += (_, _) => { if (_listening) { UiText.Instance.LanguageChanged -= LanguageChanged; _listening = false; } };
    }

    public void ShowHistory(MetadataConflictHistory? history, FileManifest current)
    {
        _history = history;
        _current = current;
        VersionsList.SelectedItem = null;
        RebuildRows();
    }

    public void SetCanApply(bool canApply)
    {
        _canApply = canApply;
        UpdateApply();
    }

    private void LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) RebuildRows();
        else Dispatcher.BeginInvoke((Action)(() => { if (_listening) RebuildRows(); }));
    }

    private void RebuildRows()
    {
        if (_current is null) return;
        var text = UiText.Instance;
        CurrentSummary.Text = string.Format(CultureInfo.CurrentCulture, text.Get("metadata.current"), _current.FileName,
            Location(_current), _current.Revision);
        var selection = SelectedFingerprint ?? _history?.Pending?.SelectedFingerprint ?? ManifestRevisionSelector.PortableFingerprint(_current);
        VersionsList.ItemsSource = _history?.Versions.OrderByDescending(version => version.Revision).ThenByDescending(version => version.UpdatedAtUtc)
            .Select(version => new VersionRow(ManifestRevisionSelector.PortableFingerprint(version), version.FileName, Location(version),
                string.Join(" · ", new[] { text.Get(version.IsInTrash ? "metadata.trashed" : "metadata.active"),
                    text.Get(version.IsFavorite ? "metadata.favorite" : "metadata.notFavorite"),
                    text.Get(version.IsArchived ? "metadata.archived" : "metadata.notArchived"),
                    text.Get(version.IsHidden ? "metadata.hidden" : "metadata.visible") }),
                string.Format(CultureInfo.CurrentCulture, text.Get("metadata.revision"), version.Revision,
                    version.UpdatedAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? text.Get("metadata.unknownTime"))))
            .ToArray() ?? [];
        VersionsList.SelectedItem = VersionsList.Items.Cast<VersionRow>().FirstOrDefault(row => row.Fingerprint == selection);
        HistoryStatus.Text = text.Get(_history is null ? "metadata.empty" : _history.Pending is null ? "metadata.choose" : "metadata.pending");
        UpdateApply();
    }

    private static string Location(FileManifest manifest) => string.IsNullOrEmpty(manifest.FolderPath) ? UiText.Instance.Get("metadata.root") : manifest.FolderPath;
    private void UpdateApply()
    {
        if (ApplyVersionButton is null) return;
        ApplyVersionButton.IsEnabled = _canApply && SelectedFingerprint is not null &&
            (_history?.Pending is null || SelectedFingerprint == _history.Pending.SelectedFingerprint);
        if (PrepareVersionButton is null) return;
        PrepareVersionButton.Visibility = _history?.Pending is null ? Visibility.Collapsed : Visibility.Visible;
        var currentFingerprint = _current is null ? null : ManifestRevisionSelector.PortableFingerprint(_current);
        var changed = _history?.Pending is { } pending && currentFingerprint != ManifestRevisionSelector.PortableFingerprint(pending.Before) &&
            currentFingerprint != ManifestRevisionSelector.PortableFingerprint(pending.After);
        ApplyVersionButton.IsEnabled = ApplyVersionButton.IsEnabled && !changed;
        PrepareVersionButton.IsEnabled = _canApply && _history?.Pending is not null && SelectedFingerprint is not null &&
            (changed || SelectedFingerprint != _history.Pending.SelectedFingerprint);
    }
    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateApply();
    private void Apply_Click(object sender, RoutedEventArgs e) { if (ApplyVersionButton.IsEnabled) ApplyRequested?.Invoke(this, EventArgs.Empty); }
    private void Prepare_Click(object sender, RoutedEventArgs e) { if (PrepareVersionButton.IsEnabled) PrepareRequested?.Invoke(this, EventArgs.Empty); }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
