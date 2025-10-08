using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Extensions.Logging;

namespace DesktopApp.ViewModels;

public partial class DashboardViewModel : BaseViewModel
{
    private readonly ISessionService _sessionService;
    private readonly IChannelService _channelService;
    private readonly IVodService _vodService;
    private readonly ILogger<DashboardViewModel> _logger;

    public DashboardViewModel(
        ISessionService sessionService,
        IChannelService channelService,
        IVodService vodService,
        ILogger<DashboardViewModel> logger)
    {
        _sessionService = sessionService;
        _channelService = channelService;
        _vodService = vodService;
        _logger = logger;

        InitializeCollectionViews();
    }

    #region Observable Properties

    [ObservableProperty]
    private ViewMode _channelsViewMode = ViewMode.Grid;

    [ObservableProperty]
    private ViewMode _vodViewMode = ViewMode.Grid;

    [ObservableProperty]
    private TileSize _currentTileSize = TileSize.Medium;

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private Channel? _selectedChannel;

    [ObservableProperty]
    private VodContent? _selectedVodContent;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _lastEpgUpdateText = string.Empty;

    #endregion

    #region Collections

    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<Channel> Channels { get; } = new();
    public ObservableCollection<VodContent> VodContent { get; } = new();
    public ObservableCollection<VodCategory> VodCategories { get; } = new();
    public ObservableCollection<SeriesContent> SeriesContent { get; } = new();
    public ObservableCollection<SeriesCategory> SeriesCategories { get; } = new();

    public ICollectionView CategoriesCollectionView { get; private set; } = null!;
    public ICollectionView ChannelsCollectionView { get; private set; } = null!;
    public ICollectionView VodContentCollectionView { get; private set; } = null!;
    public ICollectionView VodCategoriesCollectionView { get; private set; } = null!;
    public ICollectionView SeriesContentCollectionView { get; private set; } = null!;
    public ICollectionView SeriesCategoriesCollectionView { get; private set; } = null!;

    #endregion

    #region Computed Properties

    public bool IsChannelsGridView => ChannelsViewMode == ViewMode.Grid;
    public bool IsChannelsListView => ChannelsViewMode == ViewMode.List;
    public bool IsVodGridView => VodViewMode == ViewMode.Grid;
    public bool IsVodListView => VodViewMode == ViewMode.List;

    public double TileWidth => GetResponsiveTileWidth();
    public double TileHeight => GetResponsiveTileHeight();
    public double VodTileHeight => GetResponsiveVodTileHeight();

    #endregion

    #region Commands

    [RelayCommand]
    private async Task LoadCategoriesAsync()
    {
        await ExecuteAsync(async cancellationToken =>
        {
            var categories = await _channelService.LoadCategoriesAsync(cancellationToken);
            Categories.Clear();
            foreach (var category in categories)
            {
                Categories.Add(category);
            }
        }, "Loading categories...");
    }

    [RelayCommand]
    private async Task LoadChannelsForCategoryAsync(Category? category)
    {
        if (category == null) return;

        await ExecuteAsync(async cancellationToken =>
        {
            var channels = await _channelService.LoadChannelsForCategoryAsync(category, cancellationToken);
            Channels.Clear();

            // Assign channel numbers based on position
            int channelNumber = 1;
            foreach (var channel in channels)
            {
                channel.Number = channelNumber++;
                Channels.Add(channel);
            }

            // Load EPG data for channels
            await _channelService.LoadEpgForChannelsAsync(channels, cancellationToken);
        }, "Loading channels...");
    }

    [RelayCommand]
    private async Task LoadVodCategoriesAsync()
    {
        await ExecuteAsync(async cancellationToken =>
        {
            var categories = await _vodService.LoadVodCategoriesAsync(cancellationToken);
            VodCategories.Clear();
            foreach (var category in categories)
            {
                VodCategories.Add(category);
            }
        }, "Loading VOD categories...");
    }

    [RelayCommand]
    private async Task LoadVodContentAsync(string categoryId)
    {
        await ExecuteAsync(async cancellationToken =>
        {
            var content = await _vodService.LoadVodContentAsync(categoryId, cancellationToken);
            VodContent.Clear();
            foreach (var item in content)
            {
                VodContent.Add(item);
            }

            // Load posters
            var loadPosterTasks = content.Select(c => _vodService.LoadVodPosterAsync(c, cancellationToken));
            await Task.WhenAll(loadPosterTasks);
        }, "Loading VOD content...");
    }

    [RelayCommand]
    private void SetChannelsViewMode(ViewMode viewMode)
    {
        ChannelsViewMode = viewMode;
        OnPropertyChanged(nameof(IsChannelsGridView));
        OnPropertyChanged(nameof(IsChannelsListView));
    }

    [RelayCommand]
    private void SetVodViewMode(ViewMode viewMode)
    {
        VodViewMode = viewMode;
        OnPropertyChanged(nameof(IsVodGridView));
        OnPropertyChanged(nameof(IsVodListView));
    }

    [RelayCommand]
    private void SetTileSize(TileSize tileSize)
    {
        CurrentTileSize = tileSize;
        OnPropertyChanged(nameof(TileWidth));
        OnPropertyChanged(nameof(TileHeight));
        OnPropertyChanged(nameof(VodTileHeight));
    }

    #endregion

    #region Private Methods

    private void InitializeCollectionViews()
    {
        CategoriesCollectionView = CollectionViewSource.GetDefaultView(Categories);
        ChannelsCollectionView = CollectionViewSource.GetDefaultView(Channels);
        VodContentCollectionView = CollectionViewSource.GetDefaultView(VodContent);
        VodCategoriesCollectionView = CollectionViewSource.GetDefaultView(VodCategories);
        SeriesContentCollectionView = CollectionViewSource.GetDefaultView(SeriesContent);
        SeriesCategoriesCollectionView = CollectionViewSource.GetDefaultView(SeriesCategories);

        // Set up filters
        CategoriesCollectionView.Filter = CategoriesFilter;
        ChannelsCollectionView.Filter = ChannelsFilter;
        VodContentCollectionView.Filter = VodContentFilter;
        SeriesContentCollectionView.Filter = SeriesContentFilter;
    }

    private bool CategoriesFilter(object item)
    {
        if (item is not Category category) return false;
        return true; // Don't filter categories based on search text
    }

    private bool ChannelsFilter(object item)
    {
        if (item is not Channel channel) return false;
        return string.IsNullOrEmpty(SearchText) ||
               channel.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool VodContentFilter(object item)
    {
        if (item is not VodContent content) return false;
        return string.IsNullOrEmpty(SearchText) ||
               content.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool SeriesContentFilter(object item)
    {
        if (item is not SeriesContent series) return false;
        return string.IsNullOrEmpty(SearchText) ||
               series.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private double GetResponsiveTileWidth()
    {
        var baseWidth = CurrentTileSize switch
        {
            TileSize.Small => 150,
            TileSize.Medium => 200,
            TileSize.Large => 250,
            _ => 200
        };

        // Add responsive scaling logic here
        return baseWidth;
    }

    private double GetResponsiveTileHeight()
    {
        var baseHeight = CurrentTileSize switch
        {
            TileSize.Small => 120,
            TileSize.Medium => 160,
            TileSize.Large => 200,
            _ => 160
        };

        return baseHeight;
    }

    private double GetResponsiveVodTileHeight()
    {
        var baseHeight = CurrentTileSize switch
        {
            TileSize.Small => 180,
            TileSize.Medium => 240,
            TileSize.Large => 300,
            _ => 240
        };

        return baseHeight;
    }

    #endregion

    partial void OnSearchTextChanged(string value)
    {
        ChannelsCollectionView.Refresh();
        VodContentCollectionView.Refresh();
        SeriesContentCollectionView.Refresh();
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        if (value != null)
        {
            _ = LoadChannelsForCategoryAsync(value);
        }
    }
}