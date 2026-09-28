using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;
using System.ComponentModel;
using System.Windows.Data;

namespace DesktopApp.ViewModels;

public partial class MoviesSeriesPageViewModel : ObservableObject, IDisposable
{
    private readonly IVodService _service;
    private readonly LatestRequestLoader _movieCategoriesRequest = new();
    private readonly LatestRequestLoader _seriesCategoriesRequest = new();
    private readonly LatestRequestLoader _movieRequest = new();
    private readonly LatestRequestLoader _seriesRequest = new();
    private CancellationTokenSource? _activation;
    private bool _moviesInitialized, _seriesInitialized, _disposed;
    private string? _loadedMovieCategory, _loadedSeriesCategory;
    public MoviesSeriesPageViewModel(IVodService vodService)
    {
        _service = vodService;
        Details = new MediaDetailsViewModel(vodService);
        MoviesView.Filter = value => value is VodContent movie &&
            (string.IsNullOrEmpty(SelectedMovieCategoryId) || movie.CategoryId == SelectedMovieCategoryId) && Matches(movie.Name, movie.Genre, movie.Plot);
        SeriesView.Filter = value => value is SeriesContent series &&
            (string.IsNullOrEmpty(SelectedSeriesCategoryId) || series.CategoryId == SelectedSeriesCategoryId) && Matches(series.Name, series.Genre, series.Plot);
        Details.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Details.Content)) return;
            OnPropertyChanged(nameof(SelectedMovie)); OnPropertyChanged(nameof(SelectedSeries));
            ResourceDemandChanged?.Invoke();
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
    public Task CategoriesLoadTask { get; private set; } = Task.CompletedTask;
    public Task LoadTask { get; private set; } = Task.CompletedTask;
    public event Action? ResourceDemandChanged;
    public event Action<Exception>? LoadFailed;
    public bool IsActive => _activation != null && !_disposed;
    public bool IsMovies => ContentType == CatalogContentType.Movies;
    public bool IsSeries => !IsMovies;
    public bool HasError => ErrorMessage.Length > 0 || MovieCategoriesError.Length > 0 || SeriesCategoriesError.Length > 0;
    public string MovieCountText => $"{MoviesView.Cast<object>().Count()} movies";
    public string SeriesCountText => $"{SeriesView.Cast<object>().Count()} series";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMovies)), NotifyPropertyChangedFor(nameof(IsSeries))]
    private CatalogContentType _contentType = CatalogContentType.Movies;
    [ObservableProperty] private string _selectedMovieCategoryId = string.Empty;
    [ObservableProperty] private string _selectedSeriesCategoryId = string.Empty;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isLoadingMovies;
    [ObservableProperty] private bool _isLoadingSeries;
    [ObservableProperty] private bool _isLoadingCategories;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _movieCategoriesError = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _seriesCategoriesError = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _errorMessage = string.Empty;

    public Task ActivateAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (IsActive) return Task.WhenAll(CategoriesLoadTask, LoadTask);
        _activation = new CancellationTokenSource();
        CategoriesLoadTask = LoadCategoriesAsync();
        LoadTask = LoadCurrentAsync();
        return Task.WhenAll(CategoriesLoadTask, LoadTask);
    }

    public void Deactivate()
    {
        _movieCategoriesRequest.Cancel(); _seriesCategoriesRequest.Cancel();
        CancelContent();
        _activation?.Cancel(); _activation?.Dispose(); _activation = null;
        IsLoadingCategories = false;
    }
    private void CancelContent()
    {
        _movieRequest.Cancel(); _seriesRequest.Cancel();
        IsLoadingMovies = IsLoadingSeries = false;
        Details.Clear(); ResourceDemandChanged?.Invoke();
    }

    private async Task LoadCategoriesAsync()
    {
        if (!IsActive) return;
        var activation = _activation;
        IsLoadingCategories = true;
        await Task.WhenAll(LoadMovieCategoriesAsync(), LoadSeriesCategoriesAsync());
        if (ReferenceEquals(activation, _activation)) IsLoadingCategories = false;
    }
    private Task LoadMovieCategoriesAsync()
    {
        if (_moviesInitialized) return Task.CompletedTask;
        MovieCategoriesError = string.Empty;
        return _movieCategoriesRequest.LoadAsync(_service.LoadVodCategoriesAsync, categories =>
        {
            MovieCategories.ReplaceAll(categories); _moviesInitialized = true;
        }, error => { MovieCategoriesError = "Movie categories could not be loaded."; LoadFailed?.Invoke(error); }, () => { }, () => IsActive, _activation!.Token);
    }
    private Task LoadSeriesCategoriesAsync()
    {
        if (_seriesInitialized) return Task.CompletedTask;
        SeriesCategoriesError = string.Empty;
        return _seriesCategoriesRequest.LoadAsync(_service.LoadSeriesCategoriesAsync, categories =>
        {
            SeriesCategories.ReplaceAll(categories); _seriesInitialized = true;
        }, error => { SeriesCategoriesError = "Series categories could not be loaded."; LoadFailed?.Invoke(error); }, () => { }, () => IsActive, _activation!.Token);
    }

    partial void OnContentTypeChanged(CatalogContentType value)
    {
        CancelContent(); ErrorMessage = string.Empty;
        LoadTask = LoadCurrentAsync();
    }
    partial void OnSelectedMovieCategoryIdChanged(string value)
    {
        _movieRequest.Cancel(); _loadedMovieCategory = null; Movies.Clear(); RefreshCounts();
        if (IsMovies) { Details.Clear(); LoadTask = LoadCurrentAsync(); }
    }
    partial void OnSelectedSeriesCategoryIdChanged(string value)
    {
        _seriesRequest.Cancel(); _loadedSeriesCategory = null; Series.Clear(); RefreshCounts();
        if (IsSeries) { Details.Clear(); LoadTask = LoadCurrentAsync(); }
    }
    partial void OnSearchQueryChanged(string value)
    {
        MoviesView.Refresh(); SeriesView.Refresh();
        if (SelectedMovie != null && !MoviesView.Contains(SelectedMovie) ||
            SelectedSeries != null && !SeriesView.Contains(SelectedSeries)) Details.Clear();
        RefreshCounts(); ResourceDemandChanged?.Invoke();
    }
    private bool Matches(params string?[] fields) => string.IsNullOrWhiteSpace(SearchQuery) ||
        fields.Any(field => field?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) == true);
    private void RefreshCounts() { OnPropertyChanged(nameof(MovieCountText)); OnPropertyChanged(nameof(SeriesCountText)); }

    private async Task LoadCurrentAsync(bool force = false)
    {
        if (!IsActive) return;
        ErrorMessage = string.Empty;
        if (IsMovies)
        {
            var id = SelectedMovieCategoryId;
            IsLoadingMovies = false;
            if (string.IsNullOrEmpty(id) || !force && _loadedMovieCategory == id) { ResourceDemandChanged?.Invoke(); return; }
            IsLoadingMovies = true; ResourceDemandChanged?.Invoke();
            await _movieRequest.LoadAsync(token => _service.LoadVodContentAsync(id, token), movies =>
            {
                Movies.ReplaceAll(movies); _loadedMovieCategory = id; RefreshCounts();
            }, error => { ErrorMessage = "Movies could not be loaded. Retry to try again."; LoadFailed?.Invoke(error); },
            () => { IsLoadingMovies = false; ResourceDemandChanged?.Invoke(); },
            () => IsActive && IsMovies && SelectedMovieCategoryId == id, _activation!.Token);
        }
        else
        {
            var id = SelectedSeriesCategoryId;
            IsLoadingSeries = false;
            if (string.IsNullOrEmpty(id) || !force && _loadedSeriesCategory == id) { ResourceDemandChanged?.Invoke(); return; }
            IsLoadingSeries = true; ResourceDemandChanged?.Invoke();
            await _seriesRequest.LoadAsync(token => _service.LoadSeriesContentAsync(id, token), series =>
            {
                Series.ReplaceAll(series); _loadedSeriesCategory = id; RefreshCounts();
            }, error => { ErrorMessage = "Series could not be loaded. Retry to try again."; LoadFailed?.Invoke(error); },
            () => { IsLoadingSeries = false; ResourceDemandChanged?.Invoke(); },
            () => IsActive && IsSeries && SelectedSeriesCategoryId == id, _activation!.Token);
        }
    }

    public Task SelectMovieAsync(VodContent movie) => IsActive && IsMovies && !IsLoadingMovies && Movies.Contains(movie)
        ? Details.SelectAsync(movie, _activation!.Token) : Task.CompletedTask;
    public Task SelectSeriesAsync(SeriesContent series) => IsActive && IsSeries && !IsLoadingSeries && Series.Contains(series)
        ? Details.SelectAsync(series, _activation!.Token) : Task.CompletedTask;
    [RelayCommand] private void ShowMovies() => ContentType = CatalogContentType.Movies;
    [RelayCommand] private void ShowSeries() => ContentType = CatalogContentType.Series;
    [RelayCommand]
    private Task RetryAsync()
    {
        if (!IsActive) return Task.CompletedTask;
        CategoriesLoadTask = LoadCategoriesAsync();
        LoadTask = LoadCurrentAsync(true);
        return Task.WhenAll(CategoriesLoadTask, LoadTask);
    }
    public void Dispose() { Deactivate(); _disposed = true; }
}
