using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class FolderRecoveryPanel : UserControl
{
    public sealed record RecoveryRow(string FileName, string State, string Location)
    {
        public string Summary => $"{FileName}; {State}; {Location}";
    }
    private FolderOperationReview? _review;
    private bool _listening;
    private bool _canAct;
    public FolderOperationReview? Review => _review;
    public event EventHandler? RetryRequested;
    public event EventHandler? KeepStateRequested;
    public event EventHandler? CloseRequested;
    public FolderRecoveryPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_listening) { UiText.Instance.LanguageChanged += LanguageChanged; _listening = true; } Rebuild(); };
        Unloaded += (_, _) => { if (_listening) { UiText.Instance.LanguageChanged -= LanguageChanged; _listening = false; } };
    }
    public void ShowReview(FolderOperationReview review) { _review = review; Rebuild(); }
    public void SetCanAct(bool canAct) { _canAct = canAct; UpdateActions(); }
    private void LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) Rebuild();
        else Dispatcher.BeginInvoke((Action)(() => { if (_listening) Rebuild(); }));
    }
    private void Rebuild()
    {
        if (_review is null) return;
        var text = UiText.Instance;
        var plan = _review.Operation;
        PlanSummary.Text = string.Format(CultureInfo.CurrentCulture, text.Get(plan.Delete ? "folder.review.delete" : "folder.review.rename"),
            plan.Source, string.IsNullOrEmpty(plan.Destination) ? text.Get("metadata.root") : plan.Destination, plan.AppliedFiles, plan.Files.Count, _review.NewFiles);
        if (plan.StopRequestedAtUtc is not null) PlanSummary.Text += "\n" + text.Get("folder.stop.pending");
        RecoveryFiles.ItemsSource = _review.Files.Select(file => new RecoveryRow(file.FileName,
            text.Get("folder.review.state." + file.State),
            file.CurrentFolder is null ? text.Get("folder.review.noLocalDetails") : string.Format(CultureInfo.CurrentCulture,
                text.Get("folder.review.location"), file.CurrentFolder.Length == 0 ? text.Get("metadata.root") : file.CurrentFolder, file.CurrentRevision))).ToArray();
        UpdateActions();
    }
    private void UpdateActions()
    {
        RetryPlanButton.IsEnabled = _canAct && _review is not null;
        RetryPlanButton.Content = UiText.Instance.Get(_review?.Operation.StopRequestedAtUtc is null ? "folder.resume" : "folder.stop.finish");
        KeepStateButton.IsEnabled = _canAct && _review is not null && _review.Operation.StopRequestedAtUtc is null;
    }
    private void Retry_Click(object sender, RoutedEventArgs e) { if (RetryPlanButton.IsEnabled) RetryRequested?.Invoke(this, EventArgs.Empty); }
    private void Keep_Click(object sender, RoutedEventArgs e) { if (KeepStateButton.IsEnabled) KeepStateRequested?.Invoke(this, EventArgs.Empty); }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
