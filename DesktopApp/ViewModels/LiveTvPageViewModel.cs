using CommunityToolkit.Mvvm.ComponentModel;
using DesktopApp.Models;
using DesktopApp.Services;
using System.ComponentModel;
using System.Windows.Data;

namespace DesktopApp.ViewModels;

public partial class LiveTvPageViewModel : ObservableObject
{
    public BulkObservableCollection<Category> Categories { get; } = new();
    public BulkObservableCollection<Channel> Channels { get; } = new();
    public BulkObservableCollection<EpgEntry> UpcomingPrograms { get; } = new();
    public ICollectionView CategoriesView => CollectionViewSource.GetDefaultView(Categories);
    public ICollectionView ChannelsView => CollectionViewSource.GetDefaultView(Channels);
    internal LatestRequestLoader CategoryRequests { get; } = new();
    internal LatestRequestLoader SearchRequests { get; } = new();
    [ObservableProperty] private Channel? _selectedChannel;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _searchAllChannels;
}
