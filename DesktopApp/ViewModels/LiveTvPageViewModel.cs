using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;
using System.ComponentModel;
using System.Windows.Data;

namespace DesktopApp.ViewModels;

public partial class LiveTvPageViewModel : ObservableObject, IDisposable
{
    private readonly ILiveCatalogSource _source;
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _clock;
    private readonly System.Windows.Threading.DispatcherTimer _recentTimer;
    private readonly LatestRequestLoader _categoriesRequest = new();
    private readonly LatestRequestLoader _contentRequest = new();
    private CancellationTokenSource? _activation;
    private Task<List<Channel>>? _indexTask;
    private List<Channel>? _categoryChannels;
    private Category? _loadedCategory;
    private bool _categoriesLoaded;
    private bool _disposed;
    private bool _loadingGlobal;

    public LiveTvPageViewModel(ILiveCatalogSource source, TimeSpan? searchDebounce = null, TimeProvider? clock = null)
    {
        _source = source;
        _debounce = searchDebounce ?? TimeSpan.FromSeconds(3);
        _clock = clock ?? TimeProvider.System;
        _recentTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _recentTimer.Tick += (_, _) => RefreshRecentChannels();
        ChannelsView.Filter = MatchesChannel;
    }

    public BulkObservableCollection<Category> Categories { get; } = new();
    public BulkObservableCollection<Channel> Channels { get; } = new();
    public BulkObservableCollection<EpgEntry> UpcomingPrograms { get; } = new();
    public ICollectionView CategoriesView => CollectionViewSource.GetDefaultView(Categories);
    public ICollectionView ChannelsView => CollectionViewSource.GetDefaultView(Channels);
    public Task LoadTask { get; private set; } = Task.CompletedTask;
    public Task CategoriesLoadTask { get; private set; } = Task.CompletedTask;
    public event Action? ResourceDemandChanged;
    public event Action? ChannelsLoaded;
    public event Action<Exception>? LoadFailed;
    public bool IsActive => _activation != null && !_disposed;
    public bool IsGlobalSearchActive => SearchAllChannels && (ShowRecentlyAddedOnly || !string.IsNullOrWhiteSpace(SearchQuery));
    public bool IsSearchLoading => IsGlobalSearchActive && IsLoadingChannels;
    public bool HasError => ErrorMessage.Length > 0 || CategoriesError.Length > 0;
    public string CategoriesCountText => $"{Categories.Count} categories";
    public string ChannelsCountText => $"{ChannelsView.Cast<object>().Count()} {(SelectedCategory?.Id == LiveCatalogSource.FavoritesId && !IsGlobalSearchActive ? "favorite channels" : "channels")}";
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private Channel? _selectedChannel;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _searchAllChannels;
    [ObservableProperty] private bool _showRecentlyAddedOnly;
    [ObservableProperty] private bool _isLoadingCategories;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSearchLoading))] private bool _isLoadingChannels;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _errorMessage = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _categoriesError = string.Empty;

    public Task ActivateAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (IsActive) return Task.WhenAll(CategoriesLoadTask, LoadTask);
        _activation = new CancellationTokenSource();
        _recentTimer.Start();
        if (!_categoriesLoaded) CategoriesLoadTask = LoadCategoriesAsync();
        LoadTask = LoadContentAsync(false);
        return Task.WhenAll(CategoriesLoadTask, LoadTask);
    }

    public void Deactivate()
    {
        _recentTimer.Stop();
        _categoriesRequest.Cancel(); _contentRequest.Cancel();
        _activation?.Cancel(); _activation?.Dispose(); _activation = null;
        if (_indexTask?.IsCompletedSuccessfully != true) _indexTask = null;
        IsLoadingChannels = IsLoadingCategories = false;
        ResourceDemandChanged?.Invoke();
    }

    private async Task LoadCategoriesAsync()
    {
        if (!IsActive) return;
        IsLoadingCategories = true; CategoriesError = string.Empty;
        await _categoriesRequest.LoadAsync(_source.LoadCategoriesAsync, categories =>
        {
            Categories.ReplaceAll(categories); _categoriesLoaded = true;
            OnPropertyChanged(nameof(CategoriesCountText));
        }, error => { CategoriesError = "Categories could not be loaded. Retry to try again."; LoadFailed?.Invoke(error); },
        () => IsLoadingCategories = false, () => IsActive, _activation!.Token);
    }

    partial void OnSelectedCategoryChanged(Category? value) => LoadTask = LoadContentAsync(false);
    partial void OnSearchQueryChanged(string value) => SearchChanged();
    partial void OnSearchAllChannelsChanged(bool value) => SearchChanged();
    partial void OnShowRecentlyAddedOnlyChanged(bool value) => SearchChanged();
    private void SearchChanged()
    {
        OnPropertyChanged(nameof(IsSearchLoading));
        // Local text changes do not invalidate the category being downloaded.
        if (!IsGlobalSearchActive && IsLoadingChannels && !_loadingGlobal) return;
        LoadTask = LoadContentAsync(IsGlobalSearchActive);
    }

    private async Task LoadContentAsync(bool debounce, bool force = false)
    {
        _contentRequest.Cancel();
        IsLoadingChannels = false;
        if (!IsActive) return;
        ErrorMessage = string.Empty;
        var category = SelectedCategory;
        var global = IsGlobalSearchActive;
        var query = SearchQuery.Trim();
        var recentOnly = ShowRecentlyAddedOnly;
        var now = _clock.GetUtcNow();
        if (!global && ReferenceEquals(_loadedCategory, category) && _categoryChannels != null && !force)
        {
            ApplyChannels(_categoryChannels);
            return;
        }
        SelectedChannel = null;
        UpcomingPrograms.Clear();
        Channels.Clear();
        RefreshCount();
        ResourceDemandChanged?.Invoke();
        if (!global && category == null) return;
        _loadingGlobal = global;
        IsLoadingChannels = true;
        await _contentRequest.LoadAsync(async token =>
        {
            List<Channel> result;
            if (global)
            {
                if (debounce) await Task.Delay(_debounce, token);
                if (_indexTask == null || _indexTask.IsFaulted || _indexTask.IsCanceled)
                    _indexTask = _source.LoadAllChannelsAsync(_activation!.Token);
                var index = await _indexTask.WaitAsync(token);
                result = await Task.Run(() =>
                {
                    var numeric = int.TryParse(query, out var number);
                    return index.Where(ch =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (recentOnly && !ch.IsRecentAt(now)) return false;
                        return numeric ? ch.Number == number : ch.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            ch.NowTitle?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
                    }).Take(1000).ToList();
                }, token);
            }
            else result = await _source.LoadChannelsAsync(category!, token);
            var favorites = _source.GetFavoriteIds();
            return await Task.Run(() => result.Select((ch, i) =>
            {
                token.ThrowIfCancellationRequested();
                return new Channel { Id = ch.Id, Number = global ? ch.Number : i + 1, Name = ch.Name, Logo = ch.Logo,
                    EpgChannelId = ch.EpgChannelId, NowTitle = ch.NowTitle, NowDescription = ch.NowDescription,
                    NowTimeRange = ch.NowTimeRange, EpgSchedule = ch.EpgSchedule, IsFavorite = favorites.Contains(ch.Id),
                    AddedUtc = ch.AddedUtc, DiscoveredUtc = ch.DiscoveredUtc };
            }).ToList(), token);
        }, result =>
        {
            if (!global) { _loadedCategory = category; _categoryChannels = result; }
            ApplyChannels(result);
        }, error => { ErrorMessage = "Channels could not be loaded. Retry to try again."; LoadFailed?.Invoke(error); },
        () => { IsLoadingChannels = false; ResourceDemandChanged?.Invoke(); },
        () => IsActive && global == IsGlobalSearchActive && (global ? SearchQuery.Trim() == query && recentOnly == ShowRecentlyAddedOnly : ReferenceEquals(SelectedCategory, category)), _activation!.Token);
    }

    private bool MatchesChannel(object value)
    {
        if (value is not Channel ch) return false;
        if (ShowRecentlyAddedOnly && !ch.IsRecentAt(_clock.GetUtcNow())) return false;
        if (IsGlobalSearchActive || string.IsNullOrWhiteSpace(SearchQuery)) return true;
        return int.TryParse(SearchQuery.Trim(), out var number) && ch.Number == number ||
            ch.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
            ch.NowTitle?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void ApplyChannels(List<Channel> channels)
    {
        foreach (var channel in channels) channel.RefreshRecentlyAdded(_clock.GetUtcNow());
        if (!Channels.SequenceEqual(channels)) { SelectedChannel = null; Channels.ReplaceAll(channels); }
        else ChannelsView.Refresh();
        if (SelectedChannel != null && !ChannelsView.Contains(SelectedChannel)) SelectedChannel = null;
        UpdateFavoriteFlags();
        RefreshCount();
        ChannelsLoaded?.Invoke();
        ResourceDemandChanged?.Invoke();
    }
    private void RefreshCount() => OnPropertyChanged(nameof(ChannelsCountText));

    public void RefreshRecentChannels()
    {
        if (!IsActive) return;
        var now = _clock.GetUtcNow();
        var changed = false;
        foreach (var channel in Channels) changed |= channel.RefreshRecentlyAdded(now);
        if (!changed) return;
        ChannelsView.Refresh();
        if (SelectedChannel != null && !ChannelsView.Contains(SelectedChannel)) SelectedChannel = null;
        RefreshCount();
        ResourceDemandChanged?.Invoke();
    }
    public void RefreshFavorites()
    {
        if (_disposed) return;
        UpdateFavoriteFlags();

        // Favorites is a dynamic catalog. A cached list (including an empty one)
        // becomes obsolete when membership changes, even while another page or
        // global search is visible. Other categories only need their stars updated.
        if (_loadedCategory?.Id == LiveCatalogSource.FavoritesId)
        {
            _loadedCategory = null;
            _categoryChannels = null;
        }
        if (IsActive && !IsGlobalSearchActive && SelectedCategory?.Id == LiveCatalogSource.FavoritesId)
            LoadTask = LoadContentAsync(false, true);
    }

    private void UpdateFavoriteFlags()
    {
        var ids = _source.GetFavoriteIds();
        foreach (var channel in Channels) channel.IsFavorite = ids.Contains(channel.Id);
        if (_categoryChannels != null)
            foreach (var channel in _categoryChannels) channel.IsFavorite = ids.Contains(channel.Id);
    }
    [RelayCommand]
    private Task RetryAsync()
    {
        if (!IsActive) return Task.CompletedTask;
        if (!_categoriesLoaded) CategoriesLoadTask = LoadCategoriesAsync();
        LoadTask = LoadContentAsync(false, true);
        return Task.WhenAll(CategoriesLoadTask, LoadTask);
    }
    public void Dispose() { Deactivate(); _disposed = true; }
}
