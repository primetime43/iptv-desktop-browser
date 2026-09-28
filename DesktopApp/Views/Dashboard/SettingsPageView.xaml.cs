using System.Windows;
using System.Windows.Controls;

namespace DesktopApp.Views.Dashboard;

public partial class SettingsPageView : UserControl
{
    public SettingsPageView() => InitializeComponent();

    // These actions affect the wider application; the host chooses how to handle them.
    public event RoutedEventHandler? CacheInspectorRequested;
    public event RoutedEventHandler? ExportFavoritesRequested;
    public event RoutedEventHandler? ImportFavoritesRequested;

    private void SettingsCacheInspector_Click(object sender, RoutedEventArgs e) => CacheInspectorRequested?.Invoke(this, e);
    private void ExportFavorites_Click(object sender, RoutedEventArgs e) => ExportFavoritesRequested?.Invoke(this, e);
    private void ImportFavorites_Click(object sender, RoutedEventArgs e) => ImportFavoritesRequested?.Invoke(this, e);
}
