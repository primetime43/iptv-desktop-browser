using CommunityToolkit.Mvvm.ComponentModel;
using DesktopApp.Models;
using DesktopApp.Services;
using System.ComponentModel;
using System.Windows.Data;

namespace DesktopApp.ViewModels;

public partial class MoviesSeriesPageViewModel : ObservableObject
{
    public BulkObservableCollection<VodCategory> MovieCategories { get; } = new();
    public BulkObservableCollection<VodContent> Movies { get; } = new();
    public BulkObservableCollection<SeriesCategory> SeriesCategories { get; } = new();
    public BulkObservableCollection<SeriesContent> Series { get; } = new();
    public ICollectionView MoviesView => CollectionViewSource.GetDefaultView(Movies);
    public ICollectionView MovieCategoriesView => CollectionViewSource.GetDefaultView(MovieCategories);
    public ICollectionView SeriesView => CollectionViewSource.GetDefaultView(Series);
    public ICollectionView SeriesCategoriesView => CollectionViewSource.GetDefaultView(SeriesCategories);
    internal LatestRequestLoader MovieRequests { get; } = new();
    internal LatestRequestLoader SeriesRequests { get; } = new();
    internal SelectedDetailsLoader DetailRequests { get; } = new();
    [ObservableProperty] private CatalogContentType _contentType = CatalogContentType.Movies;
    [ObservableProperty] private string _selectedMovieCategoryId = string.Empty;
    [ObservableProperty] private string _selectedSeriesCategoryId = string.Empty;
    [ObservableProperty] private VodContent? _selectedMovie;
    [ObservableProperty] private SeriesContent? _selectedSeries;
    [ObservableProperty] private bool _isLoadingMovies;
    [ObservableProperty] private bool _isLoadingSeries;
}
