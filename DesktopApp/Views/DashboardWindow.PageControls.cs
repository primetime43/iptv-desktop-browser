using System.Windows;
using System.Windows.Controls;

namespace DesktopApp.Views;

public partial class DashboardWindow
{
    // Namescope adapter for behavior being migrated from the window into page view models.
    private object? FindDashboardElement(string name) => base.FindName(name)
        ?? LiveTvPage?.FindName(name) ?? VodPage?.FindName(name)
        ?? SchedulerPage?.FindName(name);

    private ItemsControl MoviesGridView => (ItemsControl)VodPage.FindName("MoviesGridView");
    private ItemsControl MoviesListView => (ItemsControl)VodPage.FindName("MoviesListView");
    private ItemsControl SeriesGridView => (ItemsControl)VodPage.FindName("SeriesGridView");
    private ItemsControl SeriesListView => (ItemsControl)VodPage.FindName("SeriesListView");
    private ComboBox CategoryCombo => (ComboBox)LiveTvPage.FindName("CategoryCombo");
    private ItemsControl ChannelsGridView => (ItemsControl)LiveTvPage.FindName("ChannelsGridView");
    private ItemsControl ChannelsListView => (ItemsControl)LiveTvPage.FindName("ChannelsListView");
}
