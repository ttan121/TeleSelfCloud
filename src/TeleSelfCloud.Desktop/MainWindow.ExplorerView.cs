using System.Windows;
using System.Windows.Controls;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private bool _explorerIcons;

    private void ExplorerView_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingUiPreferences || ManifestList is null) return;
        _explorerIcons = sender == IconsViewButton;
        ApplyExplorerView();
        SaveUiPreferences();
    }

    private void ApplyExplorerView()
    {
        // The same selector and ItemsSource own search, sorting, selection and file actions.
        // Changing only presentation avoids re-running filters or replacing selected objects.
        ManifestList.ItemTemplate = (DataTemplate)Resources[_explorerIcons ? "ExplorerIconTemplate" : "ExplorerDetailsTemplate"];
        ManifestList.ItemsPanel = (ItemsPanelTemplate)Resources[_explorerIcons ? "ExplorerIconPanel" : "ExplorerDetailsPanel"];
        var loading = _loadingUiPreferences;
        _loadingUiPreferences = true;
        DetailsViewButton.IsChecked = !_explorerIcons;
        IconsViewButton.IsChecked = _explorerIcons;
        _loadingUiPreferences = loading;
    }
}
