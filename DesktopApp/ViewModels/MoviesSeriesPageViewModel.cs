using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;
using System.ComponentModel;
using System.Windows.Data;

namespace DesktopApp.ViewModels;

public partial class MoviesSeriesPageViewModel : ObservableObject
{
    public MoviesSeriesPageViewModel(IVodService vodService)
    {
        Details = new MediaDetailsViewModel(vodService);
        Details.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Details.Content)) return;
            OnPropertyChanged(nameof(SelectedMovie));
            OnPropertyChanged(nameof(SelectedSeries));
        };
    }

    public MediaDetailsViewModel Details { get; }
    public VodContent? SelectedMovie => Details.Content as VodContent;
    public SeriesContent? SelectedSeries => Details.Content as SeriesContent;
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
    [ObservableProperty] private CatalogContentType _contentType = CatalogContentType.Movies;
    [ObservableProperty] private string _selectedMovieCategoryId = string.Empty;
    [ObservableProperty] private string _selectedSeriesCategoryId = string.Empty;
    [ObservableProperty] private bool _isLoadingMovies;
    [ObservableProperty] private bool _isLoadingSeries;

    partial void OnContentTypeChanged(CatalogContentType value) => Details.Clear();

    [RelayCommand]
    private void ShowMovies() => ContentType = CatalogContentType.Movies;

    [RelayCommand]
    private void ShowSeries() => ContentType = CatalogContentType.Series;
}
