using System.Windows;

namespace TeleSelfCloud.Desktop
{
    public enum OrphanedVaultAction { Wipe, Reupload, Relink, Cancel }

    public partial class OrphanedVaultDialog : Window
    {
        public OrphanedVaultAction SelectedAction { get; private set; } = OrphanedVaultAction.Cancel;

        public OrphanedVaultDialog()
        {
            InitializeComponent();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = OrphanedVaultAction.Cancel;
            DialogResult = false;
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            if (RadioWipe.IsChecked == true) SelectedAction = OrphanedVaultAction.Wipe;
            else if (RadioReupload.IsChecked == true) SelectedAction = OrphanedVaultAction.Reupload;
            else if (RadioRelink.IsChecked == true) SelectedAction = OrphanedVaultAction.Relink;
            DialogResult = true;
        }
    }
}
