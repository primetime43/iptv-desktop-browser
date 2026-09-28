using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopApp.Views.Dashboard;

// Interaction forwarding is retained while page behavior migrates into its view model.
public partial class MoviesSeriesPageView : UserControl
{
    public MoviesSeriesPageView() => InitializeComponent();
    private void VodCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.VodCategoryCombo_SelectionChanged(sender, e);
    private void VodGridView_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.VodGridView_Click(sender, e);
    private void VodListView_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.VodListView_Click(sender, e);
    private void TileSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.TileSize_SelectionChanged(sender, e);
    private void VodContent_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.VodContent_Click(sender, e);
    private void SeriesContent_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SeriesContent_Click(sender, e);
}
