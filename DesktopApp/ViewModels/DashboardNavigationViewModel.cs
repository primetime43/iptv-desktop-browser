using CommunityToolkit.Mvvm.ComponentModel;

namespace DesktopApp.ViewModels;

public enum DashboardPage { LiveTv, Favorites, Vod, Recording, Scheduler, Profile, Settings, Logs }
public enum CatalogContentType { Movies, Series }

public partial class DashboardNavigationViewModel : ObservableObject
{
    [ObservableProperty] private DashboardPage _activePage = DashboardPage.LiveTv;
}
