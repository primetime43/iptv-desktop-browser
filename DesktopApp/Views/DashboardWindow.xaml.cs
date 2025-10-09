using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DesktopApp.Models;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using DesktopApp.Services;

namespace DesktopApp.Views
{
    public partial class DashboardWindow : Window, INotifyPropertyChanged
    {
        private RecordingStatusWindow? _recordingWindow;
        private VodWindow? _vodWindow; // new separate VOD window
        // NOTE: Duplicate recording fields and OnClosed removed. This is the consolidated file.
        // Collections / state
        private readonly HttpClient _http = new();
        private readonly IChannelService _channelService;
        private readonly IVodService _vodService;
        private readonly ICacheService _cacheService;
        private readonly ObservableCollection<Category> _categories = new(); public ObservableCollection<Category> Categories => _categories;
        private readonly ObservableCollection<Channel> _channels = new(); public ObservableCollection<Channel> Channels => _channels;
        private readonly Dictionary<string, BitmapImage> _logoCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _logoSemaphore = new(6);
        private readonly ObservableCollection<EpgEntry> _upcomingEntries = new(); public ObservableCollection<EpgEntry> UpcomingEntries => _upcomingEntries;

        // VOD collections
        private readonly ObservableCollection<VodCategory> _vodCategories = new(); public ObservableCollection<VodCategory> VodCategories => _vodCategories;
        private readonly ObservableCollection<VodContent> _vodContent = new(); public ObservableCollection<VodContent> VodContent => _vodContent;
        private bool _hasVodAccess = false;
        public bool HasVodAccess
        {
            get => _hasVodAccess;
            set
            {
                if (value != _hasVodAccess)
                {
                    _hasVodAccess = value;
                    OnPropertyChanged();
                }
            }
        }

        // Recording state
        private Process? _recordProcess;
        private string? _currentRecordingFile;
        private bool _recordStopping;

        // All channels index (for efficient global search)
        private List<Channel>? _allChannelsIndex;
        private bool _allChannelsIndexLoading;
        private bool _allChannelsIndexLoaded => _allChannelsIndex != null;

        public bool IsSearchLoading => _allChannelsIndexLoading;

        public ICollectionView CategoriesCollectionView { get; }
        public ICollectionView ChannelsCollectionView { get; }
        public ICollectionView VodContentCollectionView { get; }
        public ICollectionView VodCategoriesCollectionView { get; }
        public ICollectionView SeriesContentCollectionView { get; }
        public ICollectionView SeriesCategoriesCollectionView { get; }

        // Search
        private CancellationTokenSource? _searchDebounceCts;
        private static readonly TimeSpan GlobalSearchDebounce = TimeSpan.FromSeconds(3);
        private string _searchQuery = string.Empty;
        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (value != _searchQuery)
                {
                    _searchQuery = value;
                    OnPropertyChanged();
                    OnSearchQueryChanged();
                }
            }
        }
        private bool _searchAllChannels;
        public bool SearchAllChannels
        {
            get => _searchAllChannels;
            set
            {
                if (value != _searchAllChannels)
                {
                    _searchAllChannels = value;
                    OnPropertyChanged();
                    OnSearchAllToggle();
                }
            }
        }

        private string _vodSearchQuery = string.Empty;
        public string VodSearchQuery
        {
            get => _vodSearchQuery;
            set
            {
                if (value != _vodSearchQuery)
                {
                    _vodSearchQuery = value;
                    OnPropertyChanged();
                    VodContentCollectionView.Refresh();
                }
            }
        }

        // Selection / binding props
        private string _selectedCategoryName = string.Empty;
        public string SelectedCategoryName
        {
            get => _selectedCategoryName;
            set
            {
                if (value != _selectedCategoryName)
                {
                    _selectedCategoryName = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _categoriesCountText = string.Empty;
        public string CategoriesCountText
        {
            get => _categoriesCountText;
            set
            {
                if (value != _categoriesCountText)
                {
                    _categoriesCountText = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _channelsCountText = "0 channels";
        public string ChannelsCountText
        {
            get => _channelsCountText;
            set
            {
                if (value != _channelsCountText)
                {
                    _channelsCountText = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _vodCountText = "0 movies";
        public string VodCountText
        {
            get => _vodCountText;
            set
            {
                if (value != _vodCountText)
                {
                    _vodCountText = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _seriesCountText = "0 series";
        public string SeriesCountText
        {
            get => _seriesCountText;
            set
            {
                if (value != _seriesCountText)
                {
                    _seriesCountText = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _selectedVodCategoryId = string.Empty;
        public string SelectedVodCategoryId
        {
            get => _selectedVodCategoryId;
            set
            {
                if (value != _selectedVodCategoryId)
                {
                    _selectedVodCategoryId = value;
                    OnPropertyChanged();
                    OnVodCategoryChanged();
                }
            }
        }

        private VodContent? _selectedVodContent;
        public VodContent? SelectedVodContent
        {
            get => _selectedVodContent;
            set
            {
                if (value != _selectedVodContent)
                {
                    _selectedVodContent = value;
                    OnPropertyChanged();
                    if (value != null)
                    {
                        _ = LoadVodDetailsAsync(value);
                    }
                }
            }
        }

        // Series collections
        private readonly ObservableCollection<SeriesCategory> _seriesCategories = new(); public ObservableCollection<SeriesCategory> SeriesCategories => _seriesCategories;
        private readonly ObservableCollection<SeriesContent> _seriesContent = new(); public ObservableCollection<SeriesContent> SeriesContent => _seriesContent;

        private string _selectedSeriesCategoryId = string.Empty;
        public string SelectedSeriesCategoryId
        {
            get => _selectedSeriesCategoryId;
            set
            {
                if (value != _selectedSeriesCategoryId)
                {
                    _selectedSeriesCategoryId = value;
                    OnPropertyChanged();
                    OnSeriesCategoryChanged();
                }
            }
        }

        private SeriesContent? _selectedSeriesContent;
        public SeriesContent? SelectedSeriesContent
        {
            get => _selectedSeriesContent;
            set
            {
                if (value != _selectedSeriesContent)
                {
                    _selectedSeriesContent = value;
                    OnPropertyChanged();
                    if (value != null)
                    {
                        _ = LoadSeriesDetailsAsync(value);
                    }
                }
            }
        }

        private Channel? _selectedChannel;
        public Channel? SelectedChannel
        {
            get => _selectedChannel;
            set
            {
                if (value == _selectedChannel)
                    return;

                _selectedChannel = value;
                OnPropertyChanged();
                SelectedChannelName = value?.Name ?? string.Empty;

                if (value != null)
                {
                    if (Session.Mode == SessionMode.Xtream)
                    {
                        _ = EnsureEpgLoadedAsync(value, force: true);
                        _ = LoadFullEpgForSelectedChannelAsync(value);
                    }
                    else
                    {
                        UpdateChannelEpgFromXmltv(value);
                        LoadUpcomingFromXmltv(value);
                    }
                }
                else
                {
                    _upcomingEntries.Clear();
                    NowProgramText = string.Empty;
                }
            }
        }

        private string _selectedChannelName = string.Empty;
        public string SelectedChannelName
        {
            get => _selectedChannelName;
            set
            {
                if (value != _selectedChannelName)
                {
                    _selectedChannelName = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _nowProgramText = string.Empty;
        public string NowProgramText
        {
            get => _nowProgramText;
            set
            {
                if (value != _nowProgramText)
                {
                    _nowProgramText = value;
                    OnPropertyChanged();
                }
            }
        }

        // Lifecycle / scheduling
        private bool _logoutRequested;
        private bool _isClosing;
        private readonly CancellationTokenSource _cts = new();

        // Buffer for log messages during startup before UI is ready
        private readonly List<string> _startupLogBuffer = new();
        private DateTime _nextScheduledEpgRefreshUtc;
        private string _lastEpgUpdateText = "(never)";
        public string LastEpgUpdateText
        {
            get => _lastEpgUpdateText;
            set
            {
                if (value != _lastEpgUpdateText)
                {
                    _lastEpgUpdateText = value;
                    OnPropertyChanged();
                }
            }
        }

        // Profile properties
        private string _profileUsername = string.Empty; public string ProfileUsername { get => _profileUsername; set { if (value != _profileUsername) { _profileUsername = value; OnPropertyChanged(); } } }
        private string _profileStatus = string.Empty; public string ProfileStatus { get => _profileStatus; set { if (value != _profileStatus) { _profileStatus = value; OnPropertyChanged(); } } }
        private string _profileTrialText = string.Empty; public string ProfileTrialText { get => _profileTrialText; set { if (value != _profileTrialText) { _profileTrialText = value; OnPropertyChanged(); } } }
        private string _profileExpiryText = string.Empty; public string ProfileExpiryText { get => _profileExpiryText; set { if (value != _profileExpiryText) { _profileExpiryText = value; OnPropertyChanged(); } } }
        private string _profileDaysRemaining = string.Empty; public string ProfileDaysRemaining { get => _profileDaysRemaining; set { if (value != _profileDaysRemaining) { _profileDaysRemaining = value; OnPropertyChanged(); } } }
        private string _profileMaxConnections = string.Empty; public string ProfileMaxConnections { get => _profileMaxConnections; set { if (value != _profileMaxConnections) { _profileMaxConnections = value; OnPropertyChanged(); } } }
        private string _profileActiveConnections = string.Empty; public string ProfileActiveConnections { get => _profileActiveConnections; set { if (value != _profileActiveConnections) { _profileActiveConnections = value; OnPropertyChanged(); } } }
        private string _profileRawJson = string.Empty; public string ProfileRawJson { get => _profileRawJson; set { if (value != _profileRawJson) { _profileRawJson = value; OnPropertyChanged(); } } }

        // View mode properties
        public enum ViewMode { Grid, List }
        public enum TileSize { Small, Medium, Large }

        private ViewMode _channelsViewMode = ViewMode.Grid;
        public ViewMode ChannelsViewMode
        {
            get => _channelsViewMode;
            set
            {
                if (value != _channelsViewMode)
                {
                    _channelsViewMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsChannelsGridView));
                    OnPropertyChanged(nameof(IsChannelsListView));
                }
            }
        }

        private ViewMode _vodViewMode = ViewMode.Grid;
        public ViewMode VodViewMode
        {
            get => _vodViewMode;
            set
            {
                if (value != _vodViewMode)
                {
                    _vodViewMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsVodGridView));
                    OnPropertyChanged(nameof(IsVodListView));
                }
            }
        }


        private TileSize _currentTileSize = TileSize.Medium;
        private bool _updatingTileSize = false;

        public TileSize CurrentTileSize
        {
            get => _currentTileSize;
            set
            {
                if (value != _currentTileSize)
                {
                    _currentTileSize = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TileWidth));
                    OnPropertyChanged(nameof(TileHeight));
                    OnPropertyChanged(nameof(VodTileHeight));
                }
            }
        }

        // Computed properties for UI binding
        public bool IsChannelsGridView => ChannelsViewMode == ViewMode.Grid;
        public bool IsChannelsListView => ChannelsViewMode == ViewMode.List;
        public bool IsVodGridView => VodViewMode == ViewMode.Grid;
        public bool IsVodListView => VodViewMode == ViewMode.List;

        public double TileWidth => GetResponsiveTileWidth();

        private double GetResponsiveTileWidth()
        {
            var baseWidth = CurrentTileSize switch
            {
                TileSize.Small => 150,
                TileSize.Medium => 200,
                TileSize.Large => 250,
                _ => 200
            };

            // Adjust based on window width for responsiveness
            var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            var scaleFactor = windowWidth switch
            {
                < 1200 => 0.8,  // Smaller tiles on smaller screens
                < 1600 => 1.0,  // Normal size
                _ => 1.2        // Larger tiles on bigger screens
            };

            return baseWidth * scaleFactor;
        }

        public double TileHeight => GetResponsiveTileHeight();

        private double GetResponsiveTileHeight()
        {
            var baseHeight = CurrentTileSize switch
            {
                TileSize.Small => 120,
                TileSize.Medium => 160,
                TileSize.Large => 200,
                _ => 160
            };

            var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            var scaleFactor = windowWidth switch
            {
                < 1200 => 0.8,
                < 1600 => 1.0,
                _ => 1.2
            };

            return baseHeight * scaleFactor;
        }

        public double VodTileHeight => GetResponsiveVodTileHeight();

        private double GetResponsiveVodTileHeight()
        {
            var baseHeight = CurrentTileSize switch
            {
                TileSize.Small => 180,  // Taller for VOD posters
                TileSize.Medium => 240,
                TileSize.Large => 300,
                _ => 240
            };

            var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            var scaleFactor = windowWidth switch
            {
                < 1200 => 0.8,
                < 1600 => 1.0,
                _ => 1.2
            };

            return baseHeight * scaleFactor;
        }

        // View selection
        private string _currentViewKey = "categories";
        public string CurrentViewKey
        {
            get => _currentViewKey;
            set
            {
                if (value != _currentViewKey)
                {
                    if (value == "guide" && !IsGuideReady)
                        return;

                    _currentViewKey = value;
                    OnPropertyChanged();
                    UpdateViewVisibility();
                    UpdateNavButtons();
                    ApplySearch();
                }
            }
        }

        // Guide readiness (enabled only after first category selection)
        private bool _isGuideReady = false;
        public bool IsGuideReady
        {
            get => _isGuideReady;
            set
            {
                if (value != _isGuideReady)
                {
                    _isGuideReady = value;
                    OnPropertyChanged();
                    UpdateNavButtons();
                }
            }
        }

        public DashboardWindow(IChannelService channelService, IVodService vodService, ICacheService cacheService)
        {
            _channelService = channelService ?? throw new ArgumentNullException(nameof(channelService));
            _vodService = vodService ?? throw new ArgumentNullException(nameof(vodService));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));

            // Set up raw output logging for cache status
            _channelService.SetRawOutputLogger(Log);

            InitializeComponent();
            DataContext = this;
            // User name display removed in new layout

            // Subscribe to favorites changes
            Session.FavoritesChanged += OnFavoritesChanged;

            CategoriesCollectionView = CollectionViewSource.GetDefaultView(_categories);
            ChannelsCollectionView = CollectionViewSource.GetDefaultView(_channels);
            VodContentCollectionView = CollectionViewSource.GetDefaultView(_vodContent);
            VodCategoriesCollectionView = CollectionViewSource.GetDefaultView(_vodCategories);
            SeriesContentCollectionView = CollectionViewSource.GetDefaultView(_seriesContent);
            SeriesCategoriesCollectionView = CollectionViewSource.GetDefaultView(_seriesCategories);
            CategoriesCollectionView.Filter = CategoriesFilter;
            ChannelsCollectionView.Filter = ChannelsFilter;
            VodContentCollectionView.Filter = VodContentFilter;
            SeriesContentCollectionView.Filter = SeriesContentFilter;

            LastEpgUpdateText = Session.LastEpgUpdateUtc.HasValue
                ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g")
                : (Session.Mode == SessionMode.M3u ? "(none)" : "(never)");

            ApplyProfileFromSession();

            if (Session.Mode == SessionMode.M3u)
                Session.M3uEpgUpdated += OnM3uEpgUpdated;

            // Subscribe to window size changes for responsive design
            SizeChanged += DashboardWindow_SizeChanged;

            // Initialize tile size ComboBoxes with default selection
            SetTileSizeSelection(CurrentTileSize);

            Loaded += async (_, __) =>
            {
                Session.EpgRefreshRequested += OnEpgRefreshRequested;

                if (Session.Mode == SessionMode.Xtream)
                {
                    _nextScheduledEpgRefreshUtc = DateTime.UtcNow + Session.EpgRefreshInterval;
                    _ = RunEpgSchedulerLoopAsync();
                    await LoadCategoriesAsync();
                    // VOD and Series categories will be loaded on-demand when user navigates to those sections
                }
                else
                {
                    LoadCategoriesFromPlaylist();
                    BuildPlaylistAllChannelsIndex();
                }

                UpdateViewVisibility();
                UpdateNavButtons();
            };

            // Subscribe to recording manager events for channel indicators
            RecordingManager.Instance.PropertyChanged += OnRecordingManagerChanged;
        }

        // ===== Index building for playlist mode (M3U) =====
        private void BuildPlaylistAllChannelsIndex()
        {
            if (Session.Mode != SessionMode.M3u)
                return;

            _allChannelsIndex = Session.PlaylistChannels.Select(p => new Channel
            {
                Id = p.Id,
                Name = p.Name,
                Logo = p.Logo,
                EpgChannelId = p.TvgId
            }).ToList();
        }

        private bool CategoriesFilter(object? obj)
        {
            if (obj is not Category c)
                return false;

            // Don't filter categories based on search query
            return true;
        }

        private bool ChannelsFilter(object? obj)
        {
            // Only used for per-category display; global search constructs subset directly for performance
            if (IsGlobalSearchActive)
                return true; // we already curated _channels

            if (obj is not Channel ch)
                return false;

            if (string.IsNullOrWhiteSpace(SearchQuery))
                return true;

            // Check if search query is a number - if so, search by channel number
            if (int.TryParse(SearchQuery.Trim(), out int searchNumber) && ch.Number == searchNumber)
                return true;

            if (ch.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(ch.NowTitle) && ch.NowTitle.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private bool VodContentFilter(object? obj)
        {
            if (obj is not VodContent vod)
                return false;

            if (!string.IsNullOrEmpty(SelectedVodCategoryId) && vod.CategoryId != SelectedVodCategoryId)
                return false;

            if (string.IsNullOrWhiteSpace(SearchQuery))
                return true;

            if (vod.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(vod.Genre) && vod.Genre.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(vod.Plot) && vod.Plot.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private bool SeriesContentFilter(object? obj)
        {
            if (obj is not SeriesContent series)
                return false;

            if (!string.IsNullOrEmpty(SelectedSeriesCategoryId) && series.CategoryId != SelectedSeriesCategoryId)
                return false;

            if (string.IsNullOrWhiteSpace(SearchQuery))
                return true;

            if (series.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(series.Genre) && series.Genre.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(series.Plot) && series.Plot.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private bool IsGlobalSearchActive => SearchAllChannels && !string.IsNullOrWhiteSpace(SearchQuery);

        private void OnSearchQueryChanged()
        {
            if (!SearchAllChannels)
            {
                // Normal (category) search immediate
                ApplySearch();
                return;
            }
            // Global search debounce
            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                CancelDebounce();
                ApplySearch(); // clears results immediately
                return;
            }
            DebounceGlobalSearch();
        }

        private void OnSearchAllToggle()
        {
            CancelDebounce();
            if (SearchAllChannels)
            {
                // If enabling global and query present start debounce (but also begin index load in background)
                if (!string.IsNullOrWhiteSpace(SearchQuery))
                {
                    if (!_allChannelsIndexLoaded && !_allChannelsIndexLoading)
                    {
                        _ = EnsureAllChannelsIndexAndFilterAsync(); // will load index; filtering happens after debounce expiry
                    }
                    DebounceGlobalSearch();
                }
                else
                {
                    // No query -> do nothing until user types
                    ApplySearch();
                }
            }
            else
            {
                // Disabling global -> immediate normal search apply
                ApplySearch();
            }
        }

        private void DebounceGlobalSearch()
        {
            CancelDebounce();
            _searchDebounceCts = new CancellationTokenSource();
            var token = _searchDebounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(GlobalSearchDebounce, token);
                    if (token.IsCancellationRequested) return;
                    await Application.Current.Dispatcher.InvokeAsync(() => ApplySearch());
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private void CancelDebounce()
        {
            try { _searchDebounceCts?.Cancel(); } catch { } finally { _searchDebounceCts?.Dispose(); _searchDebounceCts = null; }
        }

        // Adjust ApplySearch: remove triggering from immediate key stroke for global search (handled via debounce) but keep logic for when called
        private void ApplySearch()
        {
            if (IsGlobalSearchActive)
            {
                // Ensure view is guide (only if guide is ready)
                if (CurrentViewKey != "guide" && IsGuideReady) CurrentViewKey = "guide";
                _ = EnsureAllChannelsIndexAndFilterAsync();
                return; // filtering will happen async
            }
            // Non-global: just refresh existing collection views (but not categories)
            ChannelsCollectionView.Refresh();
            VodContentCollectionView.Refresh();
            SeriesContentCollectionView.Refresh();
            if (CurrentViewKey == "categories")
                CategoriesCountText = _categories.Count(c => CategoriesFilter(c)).ToString() + " categories";
            else if (CurrentViewKey == "guide")
                ChannelsCountText = _channels.Count(c => ChannelsFilter(c)).ToString() + " channels";

            // Update VOD and Series count to show filtered results
            var filteredVodCount = _vodContent.Count(v => VodContentFilter(v));
            VodCountText = $"{filteredVodCount} movies";
            var filteredSeriesCount = _seriesContent.Count(s => SeriesContentFilter(s));
            SeriesCountText = $"{filteredSeriesCount} series";
        }

        private async Task EnsureAllChannelsIndexAndFilterAsync()
        {
            if (!_allChannelsIndexLoaded)
            {
                if (Session.Mode == SessionMode.Xtream)
                    await LoadAllChannelsIndexAsync();
                else
                    BuildPlaylistAllChannelsIndex();
            }
            FilterGlobalChannels();
        }

        private void FilterGlobalChannels()
        {
            if (_allChannelsIndex == null) return;
            string query = SearchQuery.Trim();
            _channels.Clear();
            if (query.Length == 0)
            {
                ChannelsCountText = "0 channels";
                return; // nothing to show until user types
            }
            // Search by channel number if query is numeric, otherwise search by name/title
            IEnumerable<Channel> filtered;
            if (int.TryParse(query, out int searchNumber))
            {
                // Exact channel number match
                filtered = _allChannelsIndex.Where(c => c.Number == searchNumber);
            }
            else
            {
                // Text search: name or current program title
                filtered = _allChannelsIndex.Where(c => c.Name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (!string.IsNullOrWhiteSpace(c.NowTitle) && c.NowTitle.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            }
            var matches = filtered.Take(1000).ToList(); // safeguard huge lists
            int channelNumber = 1;
            foreach (var m in matches)
            {
                m.Number = channelNumber++;
                _channels.Add(m);
            }
            UpdateChannelsFavoriteStatus();
            ChannelsCountText = matches.Count.ToString() + " channels";
            ChannelsCollectionView.Refresh();
            // Lazy load logos for shown subset
            _ = Task.Run(() => PreloadLogosAsync(matches), _cts.Token);
        }

        private async Task LoadAllChannelsIndexAsync()
        {
            if (_allChannelsIndexLoading || _allChannelsIndexLoaded || Session.Mode != SessionMode.Xtream) return;
            try
            {
                _allChannelsIndexLoading = true;
                OnPropertyChanged(nameof(IsSearchLoading));
                SetGuideLoading(true);
                var url = Session.BuildApi("get_live_streams"); Log($"GET {url} (index all channels)\n");
                var json = await _http.GetStringAsync(url, _cts.Token); Log("(length=" + json.Length + ")\n\n");
                var list = new List<Channel>();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            list.Add(new Channel
                            {
                                Id = el.TryGetProperty("stream_id", out var idEl) && idEl.TryGetInt32(out var sid) ? sid : 0,
                                Name = el.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                Logo = el.TryGetProperty("stream_icon", out var iconEl) ? iconEl.GetString() : null,
                                EpgChannelId = el.TryGetProperty("epg_channel_id", out var epgEl) ? epgEl.GetString() : null
                            });
                        }
                    }
                }
                catch (Exception ex) { Log("PARSE ERROR all channels index: " + ex.Message + "\n"); }
                _allChannelsIndex = list;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR loading all channels index: " + ex.Message + "\n"); }
            finally {
                _allChannelsIndexLoading = false;
                OnPropertyChanged(nameof(IsSearchLoading));
                SetGuideLoading(false);
            }
        }

        // ===================== M3U XMLTV EPG =====================
        private void OnM3uEpgUpdated()
        {
            if (Session.Mode != SessionMode.M3u) return;
            Dispatcher.Invoke(() =>
            {
                LastEpgUpdateText = DateTime.UtcNow.ToLocalTime().ToString("g");
                // Use batch update for better performance, but preserve onlyIfEmpty logic
                foreach (var ch in _channels) UpdateChannelEpgFromXmltv(ch, onlyIfEmpty: ch != SelectedChannel);
                if (SelectedChannel != null) LoadUpcomingFromXmltv(SelectedChannel);
            });
        }

        private void UpdateChannelEpgFromXmltv(Channel ch, bool onlyIfEmpty = false)
        {
            if (Session.Mode != SessionMode.M3u) return;
            var pl = Session.PlaylistChannels.FirstOrDefault(p => p.Id == ch.Id);
            var tvgId = pl?.TvgId; if (string.IsNullOrWhiteSpace(tvgId)) return;
            if (!Session.M3uEpgByChannel.TryGetValue(tvgId, out var entries) || entries.Count == 0) return;
            var nowUtc = DateTime.UtcNow;
            var current = entries.LastOrDefault(e => e.StartUtc <= nowUtc && e.EndUtc > nowUtc);
            if (current == null) return;
            if (onlyIfEmpty && !string.IsNullOrEmpty(ch.NowTitle)) return;
            ch.NowTitle = current.Title; ch.NowDescription = current.Description; ch.NowTimeRange = $"{current.StartUtc.ToLocalTime():h:mm tt} - {current.EndUtc.ToLocalTime():h:mm tt}";
            if (ReferenceEquals(ch, SelectedChannel)) NowProgramText = $"Now: {ch.NowTitle} ({ch.NowTimeRange})";
        }

        private void UpdateChannelsEpgFromXmltvBatch(IEnumerable<Channel> channels)
        {
            if (Session.Mode != SessionMode.M3u || Session.PlaylistChannels == null) return;

            // Create lookup dictionary for O(1) access instead of O(N) for each channel
            var playlistLookup = Session.PlaylistChannels.ToDictionary(p => p.Id, p => p);
            var nowUtc = DateTime.UtcNow;

            foreach (var ch in channels)
            {
                if (!playlistLookup.TryGetValue(ch.Id, out var pl)) continue;
                var tvgId = pl.TvgId; if (string.IsNullOrWhiteSpace(tvgId)) continue;
                if (!Session.M3uEpgByChannel.TryGetValue(tvgId, out var entries) || entries.Count == 0) continue;
                var current = entries.LastOrDefault(e => e.StartUtc <= nowUtc && e.EndUtc > nowUtc);
                if (current == null) continue;
                ch.NowTitle = current.Title; ch.NowDescription = current.Description; ch.NowTimeRange = $"{current.StartUtc.ToLocalTime():h:mm tt} - {current.EndUtc.ToLocalTime():h:mm tt}";
                if (ReferenceEquals(ch, SelectedChannel)) NowProgramText = $"Now: {ch.NowTitle} ({ch.NowTimeRange})";
            }
        }

        private void LoadUpcomingFromXmltv(Channel ch)
        {
            if (Session.Mode != SessionMode.M3u) return;
            _upcomingEntries.Clear();
            var pl = Session.PlaylistChannels.FirstOrDefault(p => p.Id == ch.Id);
            var tvgId = pl?.TvgId; if (string.IsNullOrWhiteSpace(tvgId)) return;
            if (!Session.M3uEpgByChannel.TryGetValue(tvgId, out var entries) || entries.Count == 0) return;
            var nowUtc = DateTime.UtcNow;
            foreach (var e in entries.Where(e => e.StartUtc > nowUtc).OrderBy(e => e.StartUtc).Take(10)) _upcomingEntries.Add(e);
        }

        // ===================== Navigation / UI =====================
        private void UpdateNavButtons()
        {
            try
            {
                // Navigation buttons are now handled by the new layout system
                // VOD access check - enable for Xtream mode, disable for M3U
                if (FindName("VodNavButton") is Button vodBtn)
                {
                    bool vodAvailable = Session.Mode == SessionMode.Xtream;
                    vodBtn.IsEnabled = vodAvailable;
                    vodBtn.Opacity = vodAvailable ? 1.0 : 0.5;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating nav buttons: {ex.Message}");
            }
        }

        private void UpdateViewVisibility()
        {
            // View visibility is now handled by the new navigation system
            // ShowPage method handles page switching
        }
        private void NavCategories_Click(object sender, RoutedEventArgs e) { CurrentViewKey = "categories"; if (!IsGlobalSearchActive) { /* restore category view list remains as-is */ } }
        private void NavGuide_Click(object sender, RoutedEventArgs e) { CurrentViewKey = "guide"; ApplySearch(); }
        private void NavProfile_Click(object sender, RoutedEventArgs e) => CurrentViewKey = "profile";
        private void NavOutput_Click(object sender, RoutedEventArgs e) => CurrentViewKey = "output";

        // ===================== Profile =====================
        private void ApplyProfileFromSession()
        {
            if (Session.Mode == SessionMode.M3u)
            {
                ProfileUsername = "M3U Playlist"; ProfileStatus = "(local)";
                ProfileTrialText = ProfileExpiryText = ProfileDaysRemaining = ProfileMaxConnections = ProfileActiveConnections = string.Empty;
                ProfileRawJson = "Playlist mode: EPG/account info not available."; return;
            }
            var ui = Session.UserInfo; if (ui == null) return;
            ProfileUsername = ui.username ?? Session.Username; ProfileStatus = ui.status ?? string.Empty;
            if (!string.IsNullOrEmpty(ui.is_trial))
            {
                ProfileTrialText = (ui.is_trial == "1" || string.Equals(ui.is_trial, "true", StringComparison.OrdinalIgnoreCase)) ? "Yes" : "No";
            }
            else ProfileTrialText = string.Empty;
            if (!string.IsNullOrEmpty(ui.exp_date) && long.TryParse(ui.exp_date, out var unix) && unix > 0)
            {
                try
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime; ProfileExpiryText = dt.ToString("yyyy-MM-dd HH:mm"); var remaining = dt - DateTime.Now; ProfileDaysRemaining = remaining.TotalSeconds > 0 ? Math.Floor(remaining.TotalDays).ToString() : "Expired";
                }
                catch { }
            }
            ProfileMaxConnections = ui.max_connections ?? string.Empty; ProfileActiveConnections = ui.active_cons ?? string.Empty;
            try { ProfileRawJson = JsonSerializer.Serialize(ui, new JsonSerializerOptions { WriteIndented = true }); } catch { }
        }

        // ===================== EPG scheduler (Xtream) =====================
        private async Task RunEpgSchedulerLoopAsync()
        {
            while (!_cts.IsCancellationRequested && Session.Mode == SessionMode.Xtream)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
                    if (_cts.IsCancellationRequested) break;
                    if (DateTime.UtcNow >= _nextScheduledEpgRefreshUtc)
                    {
                        Session.RaiseEpgRefreshRequested();
                        _nextScheduledEpgRefreshUtc = DateTime.UtcNow + Session.EpgRefreshInterval;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }
        private void OnEpgRefreshRequested()
        {
            if (_cts.IsCancellationRequested || Session.Mode != SessionMode.Xtream) return;
            Dispatcher.InvokeAsync(() =>
            {
                LastEpgUpdateText = Session.LastEpgUpdateUtc.HasValue ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g") : "(never)";
                Log("EPG refresh triggered\n");
                foreach (var ch in _channels) { ch.EpgLoaded = false; ch.EpgLoading = false; ch.EpgAttempts = 0; }
                if (SelectedChannel != null)
                {
                    _ = EnsureEpgLoadedAsync(SelectedChannel, force: true);
                    _ = LoadFullEpgForSelectedChannelAsync(SelectedChannel);
                }
            });
        }

        // ===================== Categories / Channels =====================
        private void LoadCategoriesFromPlaylist()
        {
            var groups = Session.PlaylistChannels.GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? "Other" : p.Category)
                .OrderBy(g => g.Key)
                .Select(g => new Category { Id = g.Key, Name = g.Key, ParentId = 0, ImageUrl = null }).ToList();

            // Add Favorites as the first category
            _categories.Clear();
            _categories.Add(new Category { Id = "⭐ Favorites", Name = "⭐ Favorites", ParentId = 0, ImageUrl = null });
            foreach (var c in groups) _categories.Add(c);
            CategoriesCountText = _categories.Count + " categories";
            ApplySearch();
        }
        private async Task LoadCategoriesAsync()
        {
            ShowLoadingOverlay("CategoriesLoadingOverlay");
            try
            {
                Log("Loading categories using ChannelService...\n");
                var categories = await _channelService.LoadCategoriesAsync(_cts.Token);

                _categories.Clear();
                // Add Favorites as the first category
                _categories.Add(new Category { Id = "⭐ Favorites", Name = "⭐ Favorites", ParentId = 0, ImageUrl = null });
                foreach (var c in categories)
                {
                    _categories.Add(c);
                }

                CategoriesCountText = $"{_categories.Count} categories";
                ApplySearch();
                Log($"Loaded {categories.Count} categories\n");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message + "\n");
                // On error, clear categories to show empty state
                _categories.Clear();
                CategoriesCountText = "0 categories";
            }
            finally { HideLoadingOverlay("CategoriesLoadingOverlay"); }
        }
        private void SetGuideLoading(bool loading)
        {
            // Guide loading indicators removed in new layout
            // TODO: Add loading indicators to new Live TV page if needed
        }
        private async Task LoadChannelsForCategoryAsync(Category cat)
        {
            if (cat == null)
            {
                Log("Cannot load channels: category is null\n");
                return;
            }

            if (IsGlobalSearchActive)
                return; // avoid overriding global results

            ShowLoadingOverlay("ChannelsLoadingOverlay");

            // Handle special Favorites category
            if (cat.Id == "⭐ Favorites")
            {
                await LoadFavoritesAsCategoryAsync();
                return;
            }
            if (Session.Mode == SessionMode.M3u)
            {
                SetGuideLoading(true);
                try
                {
                    if (Session.PlaylistChannels == null)
                    {
                        Log("Playlist channels are not loaded\n");
                        return;
                    }

                    var list = Session.PlaylistChannels.Where(p => (string.IsNullOrWhiteSpace(p.Category) ? "Other" : p.Category) == cat.Id)
                        .Select(p => new Channel { Id = p.Id, Name = p.Name, Logo = p.Logo, EpgChannelId = p.TvgId }).ToList();
                    _channels.Clear();
                    int channelNumber = 1;
                    foreach (var c in list)
                    {
                        c.Number = channelNumber++;
                        _channels.Add(c);
                    }
                    UpdateChannelsFavoriteStatus();
                    ChannelsCountText = _channels.Count.ToString() + " channels";
                    _ = Task.Run(() => PreloadLogosAsync(_channels), _cts.Token);
                    UpdateChannelsEpgFromXmltvBatch(_channels);
                }
                finally { SetGuideLoading(false); }
                ApplySearch();
                return;
            }
            SetGuideLoading(true);
            try
            {
                Log($"Loading channels for category: {cat.Name} using ChannelService...\n");
                var channels = await _channelService.LoadChannelsForCategoryAsync(cat, _cts.Token);

                _channels.Clear();
                int channelNumber = 1;
                foreach (var c in channels)
                {
                    c.Number = channelNumber++;
                    _channels.Add(c);
                }
                UpdateChannelsFavoriteStatus();

                ChannelsCountText = _channels.Count.ToString() + " channels";
                Log($"Loaded {channels.Count} channels for category: {cat.Name}\n");

                _ = Task.Run(() => PreloadLogosAsync(channels), _cts.Token);

                // Start gradual EPG loading in background without blocking UI
                _ = Task.Run(() => StartGradualEpgLoadingAsync(channels), _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR loading channels: " + ex.Message + "\n"); }
            finally
            {
                SetGuideLoading(false);
                HideLoadingOverlay("ChannelsLoadingOverlay");
            }
            ApplySearch();
        }

        private async Task LoadFavoritesAsCategoryAsync()
        {
            SetGuideLoading(true);
            try
            {
                Log("Loading favorites as category...\n");
                var favoriteChannels = Session.GetFavoriteChannels();
                var channels = new List<Channel>();

                // Convert FavoriteChannel objects to Channel objects
                foreach (var favorite in favoriteChannels)
                {
                    // Try to find the channel in current session data for fresh info
                    Channel? existingChannel = null;

                    if (Session.Mode == SessionMode.M3u)
                    {
                        var playlistChannel = Session.PlaylistChannels?.FirstOrDefault(p => p.Id == favorite.Id);
                        if (playlistChannel != null)
                        {
                            existingChannel = new Channel
                            {
                                Id = playlistChannel.Id,
                                Name = playlistChannel.Name,
                                Logo = playlistChannel.Logo,
                                EpgChannelId = playlistChannel.TvgId
                            };
                        }
                    }

                    if (existingChannel != null)
                    {
                        // Use fresh data from playlist/session
                        existingChannel.IsFavorite = true;
                        channels.Add(existingChannel);
                    }
                    else
                    {
                        // Use stored favorite data
                        var favoriteChannel = new Channel
                        {
                            Id = favorite.Id,
                            Name = favorite.Name,
                            Logo = favorite.Logo,
                            EpgChannelId = favorite.EpgChannelId,
                            IsFavorite = true
                        };
                        channels.Add(favoriteChannel);
                    }
                }

                // Update UI
                _channels.Clear();
                int channelNumber = 1;
                foreach (var c in channels)
                {
                    c.Number = channelNumber++;
                    _channels.Add(c);
                }

                ChannelsCountText = $"{_channels.Count} favorite channels";
                Log($"Loaded {channels.Count} favorite channels\n");

                // Load logos and EPG data
                _ = Task.Run(() => PreloadLogosAsync(_channels), _cts.Token);

                // Update EPG for channels if in M3U mode
                if (Session.Mode == SessionMode.M3u)
                {
                    UpdateChannelsEpgFromXmltvBatch(_channels);
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading favorites: {ex.Message}\n");
            }
            finally
            {
                SetGuideLoading(false);
                HideLoadingOverlay("ChannelsLoadingOverlay");
            }
            ApplySearch();
        }

        // ===================== Logos =====================
        private async Task PreloadLogosAsync(IEnumerable<Channel> channels)
        { try { await Task.WhenAll(channels.Where(c => !string.IsNullOrWhiteSpace(c.Logo)).Select(LoadLogoAsync)); } catch { } }
        private async Task LoadLogoAsync(Channel channel)
        {
            if (_cts.IsCancellationRequested) return;
            var url = channel.Logo;
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                // Use channel_id based caching for better cache matching
                var cachedImage = await _cacheService.GetChannelLogoAsync(channel.Id, url, _cts.Token);
                if (cachedImage != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        channel.LogoImage = cachedImage;
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                Log($"Error loading logo for channel {channel.Id} from {url}: {ex.Message}\n");
            }
        }

        // ===================== Xtream EPG loading =====================
        private async Task LoadEpgDataBatchedAsync(List<Channel> channels)
        {
            if (Session.Mode != SessionMode.Xtream || _cts.IsCancellationRequested) return;

            const int batchSize = 3; // Limit concurrent API calls to avoid overwhelming server
            const int maxRetries = 3;
            const int retryDelayMs = 2000;

            Log($"Starting batched EPG loading for {channels.Count} channels (batch size: {batchSize})\n");

            for (int i = 0; i < channels.Count; i += batchSize)
            {
                if (_cts.IsCancellationRequested) break;

                var batch = channels.Skip(i).Take(batchSize).ToList();
                var tasks = batch.Select(async ch =>
                {
                    for (int retry = 0; retry < maxRetries; retry++)
                    {
                        if (_cts.IsCancellationRequested) break;
                        if (ch.EpgLoaded && !string.IsNullOrEmpty(ch.NowTitle)) break;

                        try
                        {
                            await Dispatcher.InvokeAsync(() => _ = EnsureEpgLoadedAsync(ch, force: retry > 0));
                            // Small delay to check if loading succeeded
                            await Task.Delay(500, _cts.Token);

                            if (ch.EpgLoaded && !string.IsNullOrEmpty(ch.NowTitle))
                            {
                                Log($"EPG loaded successfully for channel {ch.Name} (attempt {retry + 1})\n");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"EPG loading failed for channel {ch.Name} (attempt {retry + 1}): {ex.Message}\n");
                        }

                        if (retry < maxRetries - 1)
                        {
                            Log($"Retrying EPG load for channel {ch.Name} in {retryDelayMs}ms...\n");
                            await Task.Delay(retryDelayMs, _cts.Token);
                        }
                    }
                }).ToList();

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    Log($"ERROR in EPG batch processing: {ex.Message}\n");
                }

                // Small delay between batches to be respectful to the server
                if (i + batchSize < channels.Count && !_cts.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cts.Token);
                }
            }

            var successCount = channels.Count(ch => ch.EpgLoaded && !string.IsNullOrEmpty(ch.NowTitle));
            Log($"EPG batch loading completed: {successCount}/{channels.Count} channels loaded successfully\n");
        }

        private async Task EnsureEpgLoadedAsync(Channel ch, bool force = false)
        {
            if (Session.Mode != SessionMode.Xtream) return;
            if (_cts.IsCancellationRequested) return;
            if (!force && (ch.EpgLoaded || ch.EpgLoading)) return;
            if (!force && ch.EpgAttempts >= 3 && !string.IsNullOrEmpty(ch.NowTitle)) return;

            // Use ChannelService instead of direct API calls
            ch.EpgLoading = true;
            ch.EpgAttempts++;
            try
            {
                await _channelService.LoadEpgForChannelAsync(ch, _cts.Token);
            }
            catch (Exception ex)
            {
                Log($"ERROR loading EPG for {ch.Name}: {ex.Message}\n");
            }
            finally
            {
                ch.EpgLoading = false;
            }
        }

        private async Task StartGradualEpgLoadingAsync(IEnumerable<Channel> channels)
        {
            if (Session.Mode != SessionMode.Xtream || _cts.IsCancellationRequested) return;

            var channelList = channels.ToList();
            Log($"Starting gradual EPG loading for {channelList.Count} channels in background\n");

            const int batchSize = 5; // Load EPG for 5 channels at a time
            const int delayBetweenBatches = 1000; // 1 second delay between batches

            try
            {
                for (int i = 0; i < channelList.Count; i += batchSize)
                {
                    if (_cts.IsCancellationRequested) break;

                    var batch = channelList.Skip(i).Take(batchSize);
                    var batchTasks = batch.Select(async channel =>
                    {
                        if (_cts.IsCancellationRequested || channel.EpgLoaded || channel.EpgLoading)
                            return;

                        try
                        {
                            channel.EpgLoading = true;
                            await _channelService.LoadEpgForChannelAsync(channel, _cts.Token);
                        }
                        catch (Exception ex)
                        {
                            // Don't log individual EPG failures in background loading to avoid spam
                        }
                        finally
                        {
                            channel.EpgLoading = false;
                        }
                    });

                    await Task.WhenAll(batchTasks);

                    // Small delay between batches to avoid overwhelming the server
                    if (i + batchSize < channelList.Count && !_cts.IsCancellationRequested)
                    {
                        await Task.Delay(delayBetweenBatches, _cts.Token);
                    }
                }

                // Update EPG timestamp when background loading completes
                if (!_cts.IsCancellationRequested && Session.LastEpgUpdateUtc == null)
                {
                    Session.LastEpgUpdateUtc = DateTime.UtcNow;
                    await Dispatcher.InvokeAsync(() => LastEpgUpdateText = Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g"));
                }

                Log($"Completed gradual EPG loading for {channelList.Count} channels\n");
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                Log($"Error in gradual EPG loading: {ex.Message}\n");
            }
        }

        private static string TryGetString(JsonElement el, params string[] names)
        { foreach (var n in names) if (el.TryGetProperty(n, out var p)) { if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? string.Empty; if (p.ValueKind == JsonValueKind.Number) return p.ToString(); } return string.Empty; }
        private void ApplyNow(Channel ch, string title, string desc, DateTime startUtc, DateTime endUtc)
        { ch.NowTitle = title; ch.NowDescription = desc; ch.NowTimeRange = $"{startUtc.ToLocalTime():h:mm tt} - {endUtc.ToLocalTime():h:mm tt}"; if (ReferenceEquals(ch, SelectedChannel)) NowProgramText = $"Now: {ch.NowTitle} ({ch.NowTimeRange})"; if (Session.LastEpgUpdateUtc == null) { Session.LastEpgUpdateUtc = DateTime.UtcNow; LastEpgUpdateText = Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g"); } }
        private async Task LoadFullEpgForSelectedChannelAsync(Channel ch)
        {
            if (Session.Mode != SessionMode.Xtream) return; if (_cts.IsCancellationRequested) { _upcomingEntries.Clear(); return; }
            try
            {
                var url = Session.BuildApi("get_simple_data_table") + "&stream_id=" + ch.Id; Log($"GET {url} (full for selected)\n");
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token); var json = await resp.Content.ReadAsStringAsync(_cts.Token); Log("(length=" + json.Length + ")\n\n");
                var trimmed = json.AsSpan().TrimStart(); if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return;
                var upcoming = new List<EpgEntry>(); var nowUtc = DateTime.UtcNow;
                try
                {
                    using var doc = JsonDocument.Parse(json); if (doc.RootElement.TryGetProperty("epg_listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                        foreach (var el in listings.EnumerateArray())
                        {
                            var start = GetUnix(el, "start_timestamp"); var end = GetUnix(el, "stop_timestamp"); if (start == DateTime.MinValue || end == DateTime.MinValue) continue;
                            string titleRaw = TryGetString(el, "title", "name", "programme", "program"); string descRaw = TryGetString(el, "description", "desc", "info", "plot", "short_description");
                            string title = DecodeMaybeBase64(titleRaw); string desc = DecodeMaybeBase64(descRaw); bool isNow = nowUtc >= start && nowUtc < end; if (!isNow && start >= nowUtc) upcoming.Add(new EpgEntry { StartUtc = start, EndUtc = end, Title = title, Description = desc });
                        }
                }
                catch (Exception ex) { Log("PARSE ERROR (full epg selected): " + ex.Message + "\n"); }
                await Dispatcher.InvokeAsync(() => { _upcomingEntries.Clear(); foreach (var e in upcoming.OrderBy(e => e.StartUtc).Take(10)) _upcomingEntries.Add(e); });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR loading full epg: " + ex.Message + "\n"); }
        }
        private static DateTime GetUnix(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var tsEl))
                return DateTime.MinValue;

            var str = tsEl.GetString();
            if (string.IsNullOrEmpty(str) || !long.TryParse(str, out var unix) || unix <= 0)
                return DateTime.MinValue;

            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        }
        private static string DecodeMaybeBase64(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            // Check if string looks like Base64 (proper length and characters)
            if (raw.Length % 4 != 0 || !raw.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '='))
                return raw;

            try
            {
                var bytes = Convert.FromBase64String(raw);
                var txt = System.Text.Encoding.UTF8.GetString(bytes);

                // If decoded text contains unexpected control characters, return original
                if (txt.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                    return raw;

                return txt;
            }
            catch (Exception)
            {
                // If Base64 decode fails, return original string
                return raw;
            }
        }

        // ===================== Misc UI actions =====================
        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow { Owner = this };
            if (settings.ShowDialog() == true)
            {
                _nextScheduledEpgRefreshUtc = DateTime.UtcNow + Session.EpgRefreshInterval;
                UpdateRecordingPageDisplay(); // Update recording page to reflect any changed settings
            }
        }
        private DispatcherTimer? _toastTimer;

        private void ShowToast(string title, string message, string colorHex)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    ToastTitle.Text = title;
                    ToastMessage.Text = message;
                    ToastColorBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));

                    // Show with fade-in animation
                    ToastContainer.Visibility = Visibility.Visible;
                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                    ToastContainer.BeginAnimation(OpacityProperty, fadeIn);

                    // Auto-hide after 3 seconds
                    _toastTimer?.Stop();
                    _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    _toastTimer.Tick += (s, e) =>
                    {
                        _toastTimer.Stop();
                        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                        fadeOut.Completed += (_, __) => ToastContainer.Visibility = Visibility.Collapsed;
                        ToastContainer.BeginAnimation(OpacityProperty, fadeOut);
                    };
                    _toastTimer.Start();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Toast error: {ex.Message}");
                }
            });
        }

        private void Log(string text)
        {
            try
            {
                if (_isClosing)
                    return;

                // Quick check if logging is enabled to avoid UI work
                bool loggingEnabled = true; // Default to enabled during startup
                try
                {
                    if (FindName("SettingsEnableLoggingCheckBox") is CheckBox enableLoggingCheckBox)
                        loggingEnabled = enableLoggingCheckBox.IsChecked == true;
                    // If checkbox doesn't exist yet (during startup), assume enabled
                }
                catch
                {
                    // If checkbox not found, assume logging is enabled for startup
                    loggingEnabled = true;
                }

                if (!loggingEnabled)
                {
                    // Still write to debug output for development
                    System.Diagnostics.Debug.Write(text);
                    return;
                }

                // Use low priority async update to avoid blocking UI
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] {text}";

                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    try
                    {
                        if (FindName("RawOutputLogTextBlock") is TextBlock logTextBlock)
                        {
                            // If this is the first time we're accessing the log, replay buffered messages
                            if (logTextBlock.Text == "Raw output log will appear here when logging is enabled..." && _startupLogBuffer.Any())
                            {
                                var bufferedMessages = string.Join("", _startupLogBuffer);
                                logTextBlock.Text = bufferedMessages + logEntry;
                                _startupLogBuffer.Clear();
                            }
                            else if (logTextBlock.Text == "Raw output log will appear here when logging is enabled...")
                            {
                                logTextBlock.Text = logEntry;
                            }
                            else
                            {
                                logTextBlock.Text += logEntry;
                            }

                            // Auto-scroll to bottom (only if user is at bottom)
                            if (FindName("LogScrollViewer") is ScrollViewer scrollViewer)
                            {
                                var isAtBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 10;
                                if (isAtBottom)
                                {
                                    scrollViewer.ScrollToEnd();
                                }
                            }

                            // Limit log size periodically to prevent memory issues
                            var lines = logTextBlock.Text.Split('\n');
                            if (lines.Length > 8000) // Reduced threshold for better performance
                            {
                                var recentLines = lines.Skip(lines.Length - 4000).ToArray();
                                logTextBlock.Text = string.Join("\n", recentLines);
                            }
                        }
                        else
                        {
                            // UI not ready yet, buffer the message
                            _startupLogBuffer.Add(logEntry);
                        }
                    }
                    catch (Exception ex)
                    {
                        // UI not ready yet, buffer the message
                        _startupLogBuffer.Add(logEntry);
                        System.Diagnostics.Debug.WriteLine($"UI Log failed, buffered message: {ex.Message}");
                    }
                });

                // Also write to debug output for debugging
                System.Diagnostics.Debug.Write(text);
            }
            catch (Exception ex)
            {
                // Failed to log - attempt to write to debug output as fallback
                System.Diagnostics.Debug.WriteLine($"Log failed: {ex.Message}");
            }
        }
        private void Logout_Click(object sender, RoutedEventArgs e) { _logoutRequested = true; _cts.Cancel(); Session.Username = Session.Password = string.Empty; if (Owner is MainWindow mw) { Application.Current.MainWindow = mw; mw.Show(); } Close(); }
        // modify existing OnClosed (search and replace previous implementation) - keep rest of file intact
        protected override void OnClosed(EventArgs e)
        {
            try { StopRecording(); } catch { }
            try { if (_vodWindow != null) { _vodWindow.Close(); _vodWindow = null; } } catch { }
            CancelDebounce(); _isClosing = true; _cts.Cancel(); base.OnClosed(e); _cts.Dispose(); Session.EpgRefreshRequested -= OnEpgRefreshRequested; Session.M3uEpgUpdated -= OnM3uEpgUpdated; Session.FavoritesChanged -= OnFavoritesChanged; RecordingManager.Instance.PropertyChanged -= OnRecordingManagerChanged; if (!_logoutRequested) { if (Owner is MainWindow mw) { try { mw.Close(); } catch { } } Application.Current.Shutdown(); }
        }

        private void OnRecordingManagerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RecordingManager.RecordingChannelId))
            {
                UpdateChannelRecordingStatus();
            }

            // Update recording page display for relevant property changes
            if (e.PropertyName == nameof(RecordingManager.IsRecording) ||
                e.PropertyName == nameof(RecordingManager.State) ||
                e.PropertyName == nameof(RecordingManager.StatusDisplay) ||
                e.PropertyName == nameof(RecordingManager.DurationDisplay) ||
                e.PropertyName == nameof(RecordingManager.SizeDisplay) ||
                e.PropertyName == nameof(RecordingManager.BitrateDisplay) ||
                e.PropertyName == nameof(RecordingManager.ChannelName) ||
                e.PropertyName == nameof(RecordingManager.StartedDisplay) ||
                e.PropertyName == nameof(RecordingManager.FileName) ||
                e.PropertyName == nameof(RecordingManager.FilePath))
            {
                UpdateRecordingPageDisplay();
            }
        }

        private void UpdateChannelRecordingStatus()
        {
            var recordingChannelId = RecordingManager.Instance.RecordingChannelId;

            // Update channels in current collection
            foreach (var channel in _channels)
            {
                channel.IsRecording = (recordingChannelId.HasValue && channel.Id == recordingChannelId.Value);
            }

            // Also update all channels index if loaded
            if (_allChannelsIndex != null)
            {
                foreach (var channel in _allChannelsIndex)
                {
                    channel.IsRecording = (recordingChannelId.HasValue && channel.Id == recordingChannelId.Value);
                }
            }
        }


        private async void ChannelTile_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Channel ch)
                await EnsureEpgLoadedAsync(ch);
        }

        private void ChannelTile_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Currently no specific action needed on mouse leave
        }

        private void ChannelFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // Prevent the channel tile click event from firing

            if (sender is Button button && button.DataContext is Channel channel)
            {
                Session.ToggleFavoriteChannel(channel);

                // Update the channel's IsFavorite property to reflect the change
                channel.IsFavorite = Session.IsFavoriteChannel(channel.Id);

                Log($"{(channel.IsFavorite ? "Added" : "Removed")} '{channel.Name}' {(channel.IsFavorite ? "to" : "from")} favorites\n");

                // Show toast notification
                ShowToast(
                    channel.IsFavorite ? "⭐ Added to Favorites" : "Removed from Favorites",
                    $"{channel.Name}",
                    channel.IsFavorite ? "#28A745" : "#6C757D"
                );
            }
        }

        private void ChannelTile_Click(object sender, RoutedEventArgs e) { }

        private void ChannelRecordButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // Prevent the channel tile click event from firing

            if (sender is Button button && button.DataContext is Channel channel)
            {
                // Set the selected channel (this ensures the recording tab shows the correct channel)
                SelectedChannel = channel;

                // Check if this channel is currently being recorded
                bool isCurrentlyRecording = RecordingManager.Instance.IsRecording &&
                                          RecordingManager.Instance.RecordingChannelId == channel.Id;

                if (isCurrentlyRecording)
                {
                    // Stop recording
                    if (RecordingManager.Instance.IsManualRecording)
                    {
                        StopRecording();
                    }
                    else
                    {
                        MessageBox.Show(this, "Cannot stop scheduled recording manually. Use the Recording Scheduler to cancel scheduled recordings.",
                            "Scheduled Recording Active", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    // Check if another recording is active
                    if (_recordProcess != null)
                    {
                        MessageBox.Show(this, "Another recording is already in progress. Stop the current recording before starting a new one.",
                            "Recording Active", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    // Start recording this channel
                    StartRecording();
                }
            }
        }

        private DateTime _lastChannelClickTime;
        private FrameworkElement? _lastChannelClickedElement;
        private void ChannelTile_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not Channel ch)
                return;

            var now = DateTime.UtcNow;
            const int doubleClickMs = 400;

            if (_lastChannelClickedElement == fe && (now - _lastChannelClickTime).TotalMilliseconds <= doubleClickMs)
            {
                SelectedChannel = ch;
                TryLaunchChannelInPlayer(ch);
                _lastChannelClickedElement = null;
                UpdateRecordingPageDisplay();
            }
            else
            {
                SelectedChannel = ch;
                _lastChannelClickedElement = fe;
                _lastChannelClickTime = now;
                UpdateRecordingPageDisplay();
            }
        }
        private void TryLaunchChannelInPlayer(Channel ch)
        {
            try
            {
                string url = Session.Mode == SessionMode.M3u
                    ? Session.PlaylistChannels.FirstOrDefault(p => p.Id == ch.Id)?.StreamUrl ?? string.Empty
                    : Session.BuildStreamUrl(ch.Id, "ts");

                if (string.IsNullOrWhiteSpace(url))
                {
                    Log("Stream URL not found.\n");
                    return;
                }

                Log($"Launching player: {Session.PreferredPlayer} {url}\n");
                var psi = Session.BuildPlayerProcess(url, ch.Name);

                if (string.IsNullOrWhiteSpace(psi.FileName))
                {
                    Log("Player executable not set. Configure in Settings.\n");
                    MessageBox.Show(this, "Player executable not set. Open Settings and configure a path.",
                        "Player Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("Failed to launch player: " + ex.Message + "\n");

                try
                {
                    MessageBox.Show(this, "Unable to start player. Check settings.",
                        "Player Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception msgEx)
                {
                    Log($"Failed to show error message: {msgEx.Message}\n");
                }
            }
        }

        // ===================== VOD =====================
        private async Task LoadVodCategoriesAsync()
        {
            if (Session.Mode != SessionMode.Xtream)
            {
                HasVodAccess = false;
                return;
            }
            try
            {
                var url = Session.BuildApi("get_vod_categories");
                Log($"GET {url}\n");
                var json = await _http.GetStringAsync(url, _cts.Token);
                Log(json + "\n\n");

                var parsed = new List<VodCategory>();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            parsed.Add(new VodCategory
                            {
                                CategoryId = el.TryGetProperty("category_id", out var idEl) ?
                                    (idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? string.Empty :
                                     idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32().ToString() : string.Empty) : string.Empty,
                                CategoryName = el.TryGetProperty("category_name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                ParentId = el.TryGetProperty("parent_id", out var pEl) ?
                                    (pEl.ValueKind == JsonValueKind.String ? pEl.GetString() :
                                     pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32().ToString() : null) : null
                            });
                        }
                    }
                }
                catch (Exception ex) { Log("PARSE ERROR VOD categories: " + ex.Message + "\n"); }

                // Populate local collection for UI binding
                _vodCategories.Clear();
                foreach (var cat in parsed)
                    _vodCategories.Add(cat);

                // Also populate session collection
                Session.VodCategories.Clear();
                Session.VodCategories.AddRange(parsed);

                // Set HasVodAccess based on whether we got any categories
                HasVodAccess = parsed.Count > 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("ERROR loading VOD categories: " + ex.Message + "\n");
                HasVodAccess = false;
            }
        }

        private async Task LoadSeriesCategoriesAsync()
        {
            if (Session.Mode != SessionMode.Xtream)
            {
                return;
            }
            try
            {
                var url = Session.BuildApi("get_series_categories");
                Log($"GET {url}\n");
                var json = await _http.GetStringAsync(url, _cts.Token);
                Log(json + "\n\n");

                var parsed = new List<SeriesCategory>();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            parsed.Add(new SeriesCategory
                            {
                                CategoryId = el.TryGetProperty("category_id", out var idEl) ?
                                    (idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? string.Empty :
                                     idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32().ToString() : string.Empty) : string.Empty,
                                CategoryName = el.TryGetProperty("category_name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                ParentId = el.TryGetProperty("parent_id", out var pEl) ?
                                    (pEl.ValueKind == JsonValueKind.String ? pEl.GetString() :
                                     pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32().ToString() : null) : null
                            });
                        }
                    }
                }
                catch (Exception ex) { Log("PARSE ERROR series categories: " + ex.Message + "\n"); }

                _seriesCategories.Clear();
                foreach (var c in parsed) _seriesCategories.Add(c);
                Session.SeriesCategories.Clear();
                Session.SeriesCategories.AddRange(parsed);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("ERROR loading series categories: " + ex.Message + "\n");
            }
        }

        private async Task LoadVodContentAsync(string categoryId)
        {
            if (Session.Mode != SessionMode.Xtream || string.IsNullOrEmpty(categoryId)) return;

            // Show appropriate loading overlay based on current view
            if (FindName("MoviesViewBtn") is Button moviesBtn && moviesBtn.Background.ToString().Contains("223247"))
            {
                ShowLoadingOverlay("MoviesLoadingOverlay");
            }
            else if (FindName("SeriesViewBtn") is Button seriesBtn && seriesBtn.Background.ToString().Contains("223247"))
            {
                ShowLoadingOverlay("SeriesLoadingOverlay");
            }
            try
            {
                var url = Session.BuildApi("get_vod_streams") + "&category_id=" + Uri.EscapeDataString(categoryId);
                Log($"GET {url}\n");
                var json = await _http.GetStringAsync(url, _cts.Token);
                Log(json + "\n\n");

                var parsed = new List<VodContent>();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        // Local helper to safely extract string or number-as-string
                        static string? GetFlex(JsonElement parent, string prop)
                        {
                            if (!parent.TryGetProperty(prop, out var el)) return null;
                            return el.ValueKind switch
                            {
                                JsonValueKind.String => el.GetString(),
                                JsonValueKind.Number => el.ToString(),
                                JsonValueKind.True => "1",
                                JsonValueKind.False => "0",
                                _ => null
                            };
                        }

                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            // stream_id expected int; guard against non-int
                            int id = 0;
                            if (el.TryGetProperty("stream_id", out var idEl))
                            {
                                if (idEl.ValueKind == JsonValueKind.Number) idEl.TryGetInt32(out id);
                                else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var parsedId)) id = parsedId;
                            }

                            var vod = new VodContent
                            {
                                Id = id,
                                Name = GetFlex(el, "name") ?? string.Empty,
                                CategoryId = categoryId,
                                StreamIcon = GetFlex(el, "stream_icon"),
                                Plot = GetFlex(el, "plot"),
                                Cast = GetFlex(el, "cast"),
                                Director = GetFlex(el, "director"),
                                Genre = GetFlex(el, "genre"),
                                // Some portals use releaseDate, others release_date
                                ReleaseDate = GetFlex(el, "releaseDate") ?? GetFlex(el, "release_date"),
                                Duration = GetFlex(el, "duration"),
                                Rating = GetFlex(el, "rating") ?? GetFlex(el, "rating_5based"),
                                Country = GetFlex(el, "country"),
                                Added = GetFlex(el, "added"),
                                ContainerExtension = GetFlex(el, "container_extension")
                            };

                            if (!string.IsNullOrEmpty(vod.StreamIcon))
                            {
                                _ = LoadVodPosterAsync(vod);
                            }

                            parsed.Add(vod);
                        }
                    }
                }
                catch (Exception ex) { Log("PARSE ERROR VOD content: " + ex.Message + "\n"); }

                // Add to session
                var existing = Session.VodContent.Where(v => v.CategoryId != categoryId).ToList();
                Session.VodContent.Clear();
                Session.VodContent.AddRange(existing);
                Session.VodContent.AddRange(parsed);

                // Update UI collection
                _vodContent.Clear();
                foreach (var v in parsed) _vodContent.Add(v);
                VodContentCollectionView.Refresh();
                var filteredVodCount = _vodContent.Count(v => VodContentFilter(v));
                VodCountText = $"{filteredVodCount} movies";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR loading VOD content: " + ex.Message + "\n"); }
            finally
            {
                HideLoadingOverlay("MoviesLoadingOverlay");
                HideLoadingOverlay("SeriesLoadingOverlay");
            }
        }

        private async Task LoadVodPosterAsync(VodContent vod)
        {
            if (string.IsNullOrEmpty(vod.StreamIcon)) return;

            try
            {
                await _logoSemaphore.WaitAsync(_cts.Token);
                try
                {
                    // Try to get from cache first, then fallback to direct download if cache fails
                    var bitmap = await _cacheService.GetImageAsync(vod.StreamIcon, _cts.Token);
                    if (bitmap != null)
                    {
                        vod.PosterImage = bitmap;
                        return;
                    }

                    // Fallback to direct download if cache fails
                    var imageData = await _http.GetByteArrayAsync(vod.StreamIcon, _cts.Token);
                    var fallbackBitmap = new BitmapImage();
                    fallbackBitmap.BeginInit();
                    fallbackBitmap.StreamSource = new MemoryStream(imageData);
                    fallbackBitmap.CacheOption = BitmapCacheOption.OnLoad;
                    fallbackBitmap.EndInit();
                    fallbackBitmap.Freeze();

                    vod.PosterImage = fallbackBitmap;
                }
                finally
                {
                    _logoSemaphore.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Failed to load poster for {vod.Name}: {ex.Message}\n");
            }
        }

        private async Task LoadSeriesPosterAsync(SeriesContent series)
        {
            if (string.IsNullOrEmpty(series.StreamIcon)) return;

            try
            {
                await _logoSemaphore.WaitAsync(_cts.Token);
                try
                {
                    // Try to get from cache first, then fallback to direct download if cache fails
                    var bitmap = await _cacheService.GetImageAsync(series.StreamIcon, _cts.Token);
                    if (bitmap != null)
                    {
                        series.PosterImage = bitmap;
                        return;
                    }

                    // Fallback to direct download if cache fails
                    var imageBytes = await _http.GetByteArrayAsync(series.StreamIcon, _cts.Token);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        using var stream = new MemoryStream(imageBytes);
                        var fallbackBitmap = new BitmapImage();
                        fallbackBitmap.BeginInit();
                        fallbackBitmap.CacheOption = BitmapCacheOption.OnLoad;
                        fallbackBitmap.StreamSource = stream;
                        fallbackBitmap.EndInit();
                        fallbackBitmap.Freeze();
                        series.PosterImage = fallbackBitmap;
                    }, DispatcherPriority.Background, _cts.Token);
                }
                finally
                {
                    _logoSemaphore.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Failed to load poster for {series.Name}: {ex.Message}\n");
            }
        }

        private void OnVodCategoryChanged()
        {
            if (!string.IsNullOrEmpty(SelectedVodCategoryId))
            {
                _ = LoadVodContentAsync(SelectedVodCategoryId);
            }
            else
            {
                _vodContent.Clear();
                VodCountText = "0 movies";
                VodContentCollectionView.Refresh();
            }
        }

        private void OnSeriesCategoryChanged()
        {
            if (!string.IsNullOrEmpty(SelectedSeriesCategoryId))
            {
                _ = LoadSeriesContentAsync(SelectedSeriesCategoryId);
            }
            else
            {
                _seriesContent.Clear();
                SeriesCountText = "0 series";
                SeriesContentCollectionView.Refresh();
            }
        }

        private async Task LoadSeriesContentAsync(string categoryId)
        {
            try
            {
                var url = Session.BuildApi("get_series") + "&category_id=" + Uri.EscapeDataString(categoryId);
                var json = await _http.GetStringAsync(url, _cts.Token);

                var parsed = new List<SeriesContent>();
                var jArray = JsonSerializer.Deserialize<JsonElement[]>(json);

                if (jArray != null)
                {
                    foreach (var item in jArray)
                {
                    var series = new SeriesContent
                    {
                        Id = item.TryGetProperty("series_id", out var idProp) ? (idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : int.TryParse(idProp.GetString() ?? "0", out var id) ? id : 0) : 0,
                        Name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty,
                        CategoryId = categoryId,
                        StreamIcon = item.TryGetProperty("cover", out var coverProp) ? coverProp.GetString() : null,
                        Plot = item.TryGetProperty("plot", out var plotProp) ? plotProp.GetString() : null,
                        Cast = item.TryGetProperty("cast", out var castProp) ? castProp.GetString() : null,
                        Director = item.TryGetProperty("director", out var directorProp) ? directorProp.GetString() : null,
                        Genre = item.TryGetProperty("genre", out var genreProp) ? genreProp.GetString() : null,
                        ReleaseDate = item.TryGetProperty("releaseDate", out var releaseProp) ? releaseProp.GetString() : null,
                        Rating = item.TryGetProperty("rating", out var ratingProp) ? ratingProp.GetString() : null,
                        LastModified = item.TryGetProperty("last_modified", out var lastModProp) ? lastModProp.GetString() : null
                    };
                    parsed.Add(series);
                    }
                }

                // Add to session
                Session.SeriesContent.Clear();
                Session.SeriesContent.AddRange(parsed);

                // Update UI collection
                _seriesContent.Clear();
                foreach (var s in parsed) _seriesContent.Add(s);
                SeriesContentCollectionView.Refresh();
                var filteredSeriesCount = _seriesContent.Count(s => SeriesContentFilter(s));
                SeriesCountText = $"{filteredSeriesCount} series";

                // Load posters for visible series
                foreach (var series in parsed.Take(10)) // Load first 10 posters
                {
                    _ = LoadSeriesPosterAsync(series);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR loading series content: " + ex.Message + "\n"); }
        }


        private async Task LoadSeriesDetailsAsync(SeriesContent series)
        {
            if (series.DetailsLoaded || series.DetailsLoading) return;

            try
            {
                series.DetailsLoading = true;
                var url = Session.BuildApi("get_series_info") + "&series_id=" + series.Id;
                var json = await _http.GetStringAsync(url, _cts.Token);
                var data = JsonSerializer.Deserialize<JsonElement>(json);

                // Parse series info
                if (data.TryGetProperty("info", out var infoProp))
                {
                    series.Plot = infoProp.TryGetProperty("plot", out var plotProp) ? plotProp.GetString() : series.Plot;
                    series.Cast = infoProp.TryGetProperty("cast", out var castProp) ? castProp.GetString() : series.Cast;
                    series.Director = infoProp.TryGetProperty("director", out var directorProp) ? directorProp.GetString() : series.Director;
                    series.Genre = infoProp.TryGetProperty("genre", out var genreProp) ? genreProp.GetString() : series.Genre;
                    series.Rating = infoProp.TryGetProperty("rating", out var ratingProp) ? ratingProp.GetString() : series.Rating;
                }

                // Parse seasons and episodes
                if (data.TryGetProperty("episodes", out var episodesProp) && episodesProp.ValueKind == JsonValueKind.Object)
                {
                    var seasons = new List<SeasonInfo>();

                    foreach (var seasonEntry in episodesProp.EnumerateObject())
                    {
                        if (int.TryParse(seasonEntry.Name, out var seasonNum) && seasonEntry.Value.ValueKind == JsonValueKind.Array)
                        {
                            var season = new SeasonInfo { SeasonNumber = seasonNum };
                            var episodes = new List<EpisodeContent>();

                            foreach (var episodeItem in seasonEntry.Value.EnumerateArray())
                            {
                                var episode = new EpisodeContent
                                {
                                    Id = episodeItem.TryGetProperty("id", out var idProp) ? (idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : int.TryParse(idProp.GetString() ?? "0", out var id) ? id : 0) : 0,
                                    SeriesId = series.Id,
                                    SeasonNumber = seasonNum,
                                    EpisodeNumber = episodeItem.TryGetProperty("episode_num", out var epNumProp) ? (epNumProp.ValueKind == JsonValueKind.Number ? epNumProp.GetInt32() : int.TryParse(epNumProp.GetString() ?? "0", out var epNum) ? epNum : 0) : 0,
                                    Name = episodeItem.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? string.Empty : string.Empty,
                                    Plot = episodeItem.TryGetProperty("info", out var infoProp2) && infoProp2.TryGetProperty("plot", out var plotProp2) ? plotProp2.GetString() : null,
                                    Duration = episodeItem.TryGetProperty("info", out var infoProp3) && infoProp3.TryGetProperty("duration", out var durProp) ? durProp.GetString() : null,
                                    ReleaseDate = episodeItem.TryGetProperty("info", out var infoProp4) && infoProp4.TryGetProperty("releasedate", out var relProp) ? relProp.GetString() : null,
                                    ContainerExtension = episodeItem.TryGetProperty("container_extension", out var contProp) ? contProp.GetString() : "mp4"
                                };
                                episodes.Add(episode);
                            }

                            season.Episodes = episodes.OrderBy(e => e.EpisodeNumber).ToList();
                            season.EpisodeCount = season.Episodes.Count;
                            seasons.Add(season);
                        }
                    }

                    series.Seasons = seasons.OrderBy(s => s.SeasonNumber).ToList();
                }

                series.DetailsLoaded = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"ERROR loading series details for {series.Name}: {ex.Message}\n");
            }
            finally
            {
                series.DetailsLoading = false;
            }
        }

        private void VodCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Handled by SelectedVodCategoryId property change
        }

        private void VodContent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not VodContent vod)
                return;

            // Check for double-click to launch player
            var now = DateTime.UtcNow;
            const int doubleClickMs = 400;

            if (SelectedVodContent == vod && (now - _lastVodClickTime).TotalMilliseconds <= doubleClickMs)
            {
                TryLaunchVodInPlayer(vod);
                _lastVodClickTime = DateTime.MinValue; // Reset to avoid triple-click issues
            }
            else
            {
                SelectedVodContent = vod;
                _lastVodClickTime = now;

                // Update details panel
                ShowVodDetailsPanel(vod);
            }
        }

        private DateTime _lastVodClickTime;
        private DateTime _lastSeriesClickTime;

        private void VodPlay_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedVodContent != null)
            {
                TryLaunchVodInPlayer(SelectedVodContent);
            }
        }

        private async Task LoadVodDetailsAsync(VodContent vod)
        {
            if (Session.Mode != SessionMode.Xtream || vod.DetailsLoaded || vod.DetailsLoading)
                return;

            vod.DetailsLoading = true;
            try
            {
                var url = Session.BuildApi("get_vod_info") + "&vod_id=" + vod.Id;
                Log($"GET {url} (VOD details)\n");
                var json = await _http.GetStringAsync(url, _cts.Token);
                Log($"(VOD details length={json.Length})\n\n");

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("info", out var info))
                    {
                        // Helper to safely get string values
                        static string? GetStringValue(JsonElement parent, string prop)
                        {
                            if (!parent.TryGetProperty(prop, out var el)) return null;
                            return el.ValueKind switch
                            {
                                JsonValueKind.String => el.GetString(),
                                JsonValueKind.Number => el.ToString(),
                                JsonValueKind.True => "1",
                                JsonValueKind.False => "0",
                                _ => null
                            };
                        }

                        // Update with detailed info
                        vod.Plot = GetStringValue(info, "plot") ?? vod.Plot;
                        vod.Cast = GetStringValue(info, "cast") ?? vod.Cast;
                        vod.Director = GetStringValue(info, "director") ?? vod.Director;
                        vod.Genre = GetStringValue(info, "genre") ?? vod.Genre;
                        vod.ReleaseDate = GetStringValue(info, "releasedate") ?? GetStringValue(info, "release_date") ?? vod.ReleaseDate;
                        vod.Rating = GetStringValue(info, "rating") ?? GetStringValue(info, "rating_5based") ?? vod.Rating;
                        vod.Duration = GetStringValue(info, "duration") ?? vod.Duration;
                        vod.Country = GetStringValue(info, "country") ?? vod.Country;
                        vod.Backdrop = GetStringValue(info, "backdrop_path");
                        vod.Trailer = GetStringValue(info, "youtube_trailer");
                        vod.TmdbId = GetStringValue(info, "tmdb_id");
                        vod.ImdbId = GetStringValue(info, "imdb_id");
                        vod.Language = GetStringValue(info, "language");
                        vod.BitRate = GetStringValue(info, "bitrate");
                        vod.VideoCodec = GetStringValue(info, "video");
                        vod.AudioCodec = GetStringValue(info, "audio");

                        // Parse numeric values
                        if (int.TryParse(GetStringValue(info, "played"), out var played))
                            vod.Played = played;
                        if (int.TryParse(GetStringValue(info, "views"), out var views))
                            vod.Views = views;
                    }

                    // Also check for movie_data array (some servers use this format)
                    if (doc.RootElement.TryGetProperty("movie_data", out var movieData) && movieData.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var movie in movieData.EnumerateArray())
                        {
                            if (movie.TryGetProperty("stream_id", out var idEl) &&
                                idEl.TryGetInt32(out var movieId) && movieId == vod.Id)
                            {
                                // Update with movie_data info if available
                                vod.Name = movie.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? vod.Name : vod.Name;
                                break;
                            }
                        }
                    }

                    vod.DetailsLoaded = true;
                    Log($"VOD details loaded for: {vod.Name}\n");
                }
                catch (Exception ex)
                {
                    Log($"PARSE ERROR VOD details: {ex.Message}\n");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"ERROR loading VOD details: {ex.Message}\n");
            }
            finally
            {
                vod.DetailsLoading = false;
            }
        }

        private void TryLaunchVodInPlayer(VodContent vod)
        {
            try
            {
                var extension = !string.IsNullOrEmpty(vod.ContainerExtension) ? vod.ContainerExtension : "mp4";
                var url = Session.BuildVodStreamUrl(vod.Id, extension);

                Log($"Launching VOD player: {Session.PreferredPlayer} {url}\n");
                var psi = Session.BuildPlayerProcess(url, vod.Name);

                if (string.IsNullOrWhiteSpace(psi.FileName))
                {
                    Log("Player executable not set. Configure in Settings.\n");
                    MessageBox.Show(this, "Player executable not set. Open Settings and configure a path.",
                        "Player Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("Failed to launch VOD player: " + ex.Message + "\n");
                try
                {
                    MessageBox.Show(this, "Unable to start player for VOD. Check settings.",
                        "Player Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception msgEx)
                {
                    Log($"Failed to show error message: {msgEx.Message}\n");
                }
            }
        }

        private void TryLaunchEpisodeInPlayer(EpisodeContent episode)
        {
            try
            {
                var extension = !string.IsNullOrEmpty(episode.ContainerExtension) ? episode.ContainerExtension : "mp4";
                var url = Session.BuildSeriesStreamUrl(episode.Id, extension);

                Log($"Launching episode player: {Session.PreferredPlayer} {url}\n");
                var psi = Session.BuildPlayerProcess(url, episode.DisplayTitle);

                if (string.IsNullOrWhiteSpace(psi.FileName))
                {
                    Log("Player executable not set. Configure in Settings.\n");
                    MessageBox.Show(this, "Player executable not set. Open Settings and configure a path.",
                        "Player Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("Failed to launch episode player: " + ex.Message + "\n");
                try
                {
                    MessageBox.Show(this, "Unable to start player for episode. Check settings.",
                        "Player Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception msgEx)
                {
                    Log($"Failed to show error message: {msgEx.Message}\n");
                }
            }
        }


        private void TryLaunchSeriesInPlayer(SeriesContent series)
        {
            // For series, we typically want to show episodes rather than launch directly
            // This could open a series episodes window or launch first episode
            try
            {
                Log($"Opening series: {series.Name}\n");

                // For now, this could open a separate series episodes window
                // or we could implement inline episode browsing
                var seriesWindow = new VodWindow(series) { Owner = this };
                seriesWindow.Show();
            }
            catch (Exception ex)
            {
                Log("Failed to open series: " + ex.Message + "\n");
                MessageBox.Show(this, "Unable to open series details.",
                    "Series Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // API test output (disabled in M3U mode)
        private async void LoadStreams_Click(object sender, RoutedEventArgs e) => await RunApiCall("get_live_streams");
        private async Task RunApiCall(string action)
        { if (Session.Mode != SessionMode.Xtream) { Log("API calls disabled in M3U mode.\n"); return; } try { var url = Session.BuildApi(action); Log($"GET {url}\n"); var json = await _http.GetStringAsync(url, _cts.Token); if (json.Length > 50_000) json = json[..50_000] + "...<truncated>"; Log(json + "\n\n"); } catch (OperationCanceledException) { } catch (Exception ex) { Log("ERROR: " + ex.Message + "\n"); } }

        private void OpenRecordingFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var recordingDir = Session.RecordingDirectory;
                if (string.IsNullOrEmpty(recordingDir))
                {
                    recordingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
                }

                // Create directory if it doesn't exist
                if (!Directory.Exists(recordingDir))
                {
                    Directory.CreateDirectory(recordingDir);
                }

                // Open the folder in Windows Explorer
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{recordingDir}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log($"Failed to open recording folder: {ex.Message}\n");
                try
                {
                    MessageBox.Show(this, "Unable to open recording folder. Check settings.",
                        "Folder Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            }
        }

        private void OpenRecordingStatus_Click(object sender, RoutedEventArgs e)
        {
            if (_recordingWindow == null || !_recordingWindow.IsVisible)
            {
                _recordingWindow = new RecordingStatusWindow { Owner = this };
                RecordingStatusWindow.RecordingStoppedRequested += OnRecordingStoppedRequested;
                _recordingWindow.Closed += (_, _) =>
                {
                    RecordingStatusWindow.RecordingStoppedRequested -= OnRecordingStoppedRequested;
                    _recordingWindow = null;
                };
                _recordingWindow.Show();
            }
            else
            {
                _recordingWindow.Activate();
            }
        }

        private void OpenScheduler_Click(object sender, RoutedEventArgs e)
        {
            var schedulerWindow = new RecordingSchedulerWindow { Owner = this };
            schedulerWindow.Show();
        }

        private void OnRecordingStoppedRequested()
        {
            Dispatcher.Invoke(() => { if (_recordProcess != null) StopRecording(); });
        }

        private void StartRecording()
        {
            if (_recordProcess != null) return;

            if (SelectedChannel == null)
            {
                Log("No channel selected to record.\n");
                MessageBox.Show(this, "Please select a channel from the Live TV tab first before recording.",
                    "No Channel Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string streamUrl = Session.Mode == SessionMode.M3u ? Session.PlaylistChannels.FirstOrDefault(p => p.Id == SelectedChannel.Id)?.StreamUrl ?? string.Empty : Session.BuildStreamUrl(SelectedChannel.Id, "ts");
            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                Log("Stream URL not found for recording.\n");
                MessageBox.Show(this, "Unable to get stream URL for the selected channel. Please try selecting the channel again.",
                    "Stream Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(Session.FfmpegPath) || !File.Exists(Session.FfmpegPath)) { Log("FFmpeg path not set (Settings).\n"); MessageBox.Show(this, "Set FFmpeg path in Settings.", "Recording", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            string baseDir = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            try
            {
                Directory.CreateDirectory(baseDir);
            }
            catch (Exception ex)
            {
                Log($"Failed to create recording directory '{baseDir}': {ex.Message}\n");
                MessageBox.Show(this, $"Unable to create recording directory: {ex.Message}", "Recording Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            static string Sanitize(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                var invalid = Path.GetInvalidFileNameChars();
                var parts = raw.Split(invalid, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var joined = string.Join("_", parts);
                if (joined.Length > 60) joined = joined[..60];
                return joined.Trim('_');
            }

            string safeChannel = Sanitize(SelectedChannel.Name) ;
            string safeProgram = Sanitize(SelectedChannel.NowTitle);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = string.IsNullOrWhiteSpace(safeProgram)
                ? $"{safeChannel}_{timestamp}.ts"
                : $"{safeChannel}_{safeProgram}_{timestamp}.ts";
            // Collapse any double underscores
            while (fileName.Contains("__")) fileName = fileName.Replace("__", "_");
            _currentRecordingFile = Path.Combine(baseDir, fileName);

            var psi = Session.BuildFfmpegRecordProcess(streamUrl, SelectedChannel.Name, _currentRecordingFile); if (psi == null) { Log("Unable to build FFmpeg process.\n"); return; }
            Log($"Recording start: {_currentRecordingFile}\n");
            _recordProcess = new Process { StartInfo = psi, EnableRaisingEvents = false }; // we handle cleanup directly
            _recordProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log("FFMPEG: " + e.Data + "\n"); };
            _recordProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log("FFMPEG: " + e.Data + "\n"); };
            if (_recordProcess.Start())
            {
                try { _recordProcess.BeginOutputReadLine(); _recordProcess.BeginErrorReadLine(); } catch { }
                RecordingManager.Instance.Start(_currentRecordingFile, SelectedChannel.Name, SelectedChannel.Id, true);

                // Update button to show Stop state
                if (FindName("RecordBtnText") is TextBlock btnText) btnText.Text = "Stop Recording";
                if (FindName("RecordBtnIcon") is TextBlock btnIcon) btnIcon.Text = "⏹️";
                if (FindName("RecordButton") is Button btn) btn.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45)); // Red color

                // Update recording page visual feedback
                UpdateRecordingPageDisplay();

                // Show toast notification
                ShowToast(
                    "⏺️ Recording Started",
                    $"{SelectedChannel.Name}",
                    "#DC3545"
                );
            }
            else
            {
                Log("Failed to start FFmpeg.\n");
                _recordProcess.Dispose();
                _recordProcess = null;

                // Show error toast
                ShowToast(
                    "❌ Recording Failed",
                    "Failed to start FFmpeg",
                    "#DC3545"
                );
            }
        }
        private void StopRecording()
        {
            if (_recordProcess == null || _recordStopping) return;
            _recordStopping = true;
            var proc = _recordProcess;
            var recordedFile = _currentRecordingFile; // capture before clearing
            _recordProcess = null;
            _currentRecordingFile = null;

            // Update UI state immediately
            if (FindName("RecordBtnText") is TextBlock btnText) btnText.Text = "Start Recording";
            if (FindName("RecordBtnIcon") is TextBlock btnIcon) btnIcon.Text = "⏺️";
            if (FindName("RecordButton") is Button btn) btn.Background = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45)); // Green color

            var channelName = RecordingManager.Instance.ChannelName ?? "Unknown";
            RecordingManager.Instance.Stop();
            Log("Stopping recording...\n");

            // Update recording page visual feedback
            UpdateRecordingPageDisplay();

            // Show toast notification
            ShowToast(
                "⏹️ Recording Stopped",
                channelName,
                "#28A745"
            );

            // Terminate process on background thread so UI never blocks
            _ = Task.Run(() =>
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        try { proc.CloseMainWindow(); } catch { }
                        // brief grace period
                        Thread.Sleep(400);
                        if (!proc.HasExited)
                        {
                            try { proc.Kill(true); } catch { }
                        }
                        try { proc.WaitForExit(3000); } catch { }
                    }
                }
                catch { }
                finally
                {
                    try { proc.Dispose(); } catch { }
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(recordedFile))
                            Log("Recording saved: " + recordedFile + "\n");
                        _recordStopping = false;
                    });
                }
            });
        }

        private void RecordBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_recordProcess == null)
            {
                StartRecording();
            }
            else
            {
                // Only allow stopping if it's a manual recording
                if (RecordingManager.Instance.IsManualRecording)
                {
                    StopRecording();
                }
                else
                {
                    MessageBox.Show("Cannot stop scheduled recording manually. Use the Recording Scheduler to cancel scheduled recordings.",
                        "Scheduled Recording Active", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        // Navigation Methods for New Layout
        private void NavigateToLiveTv(object sender, RoutedEventArgs e)
        {
            ShowPage("LiveTv");
            SetSelectedNavButton(sender as Button);
        }

        private void NavigateToFavorites(object sender, RoutedEventArgs e)
        {
            ShowPage("Favorites");
            SetSelectedNavButton(sender as Button);
            LoadFavoritesPage();
        }

        private async void NavigateToVod(object sender, RoutedEventArgs e)
        {
            ShowPage("Vod");
            SetSelectedNavButton(sender as Button);

            // Load VOD categories if not already loaded
            if (_vodCategories.Count == 0 && Session.Mode == SessionMode.Xtream)
            {
                await LoadVodCategoriesAsync();
                await LoadSeriesCategoriesAsync();
            }
        }

        private void NavigateToRecording(object sender, RoutedEventArgs e)
        {
            ShowPage("Recording");
            SetSelectedNavButton(sender as Button);
            UpdateRecordingPageDisplay();
        }

        private void NavigateToScheduler(object sender, RoutedEventArgs e)
        {
            ShowPage("Scheduler");

            // Always highlight the correct nav button regardless of which button triggered this
            if (FindName("SchedulerNavButton") is Button schedulerNavBtn)
            {
                SetSelectedNavButton(schedulerNavBtn);
            }

            // Initialize the scheduler when navigating to it
            InitializeScheduler();
        }

        private void NavigateToProfile(object sender, RoutedEventArgs e)
        {
            ShowPage("Profile");
            SetSelectedNavButton(sender as Button);
            LoadProfileData();
        }

        private void NavigateToSettings(object sender, RoutedEventArgs e)
        {
            ShowPage("Settings");

            // Always highlight the correct nav button regardless of which button triggered this
            if (FindName("SettingsNavButton") is Button settingsNavBtn)
            {
                SetSelectedNavButton(settingsNavBtn);
            }

            LoadSettingsPage();
        }

        private void NavigateToLogs(object sender, RoutedEventArgs e)
        {
            ShowPage("Logs");
            SetSelectedNavButton(sender as Button);
        }

        private void OpenCacheInspector(object sender, RoutedEventArgs e)
        {
            try
            {
                var cacheInspector = App.GetRequiredService<CacheInspectorWindow>();
                cacheInspector.Owner = this;
                cacheInspector.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to open Cache Inspector: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowPage(string pageName)
        {
            // Hide all pages
            if (FindName("LiveTvPage") is Grid liveTvPage) liveTvPage.Visibility = Visibility.Collapsed;
            if (FindName("FavoritesPage") is Grid favoritesPage) favoritesPage.Visibility = Visibility.Collapsed;
            if (FindName("VodPage") is Grid vodPage) vodPage.Visibility = Visibility.Collapsed;
            if (FindName("RecordingPage") is Grid recordingPage) recordingPage.Visibility = Visibility.Collapsed;
            if (FindName("SchedulerPage") is Grid schedulerPage) schedulerPage.Visibility = Visibility.Collapsed;
            if (FindName("ProfilePage") is Grid profilePage) profilePage.Visibility = Visibility.Collapsed;
            if (FindName("SettingsPage") is Grid settingsPage) settingsPage.Visibility = Visibility.Collapsed;
            if (FindName("LogsPage") is Grid logsPage) logsPage.Visibility = Visibility.Collapsed;

            // Show selected page
            if (FindName($"{pageName}Page") is Grid targetPage)
                targetPage.Visibility = Visibility.Visible;
        }

        private void SetSelectedNavButton(Button? selectedButton)
        {
            // Clear all nav button selections
            if (FindName("LiveTvNavButton") is Button liveTvBtn) liveTvBtn.Tag = null;
            if (FindName("FavoritesNavButton") is Button favoritesBtn) favoritesBtn.Tag = null;
            if (FindName("VodNavButton") is Button vodBtn) vodBtn.Tag = null;
            if (FindName("RecordingNavButton") is Button recordingBtn) recordingBtn.Tag = null;
            if (FindName("SchedulerNavButton") is Button schedulerBtn) schedulerBtn.Tag = null;
            if (FindName("ProfileNavButton") is Button profileBtn) profileBtn.Tag = null;
            if (FindName("SettingsNavButton") is Button settingsBtn) settingsBtn.Tag = null;
            if (FindName("LogsNavButton") is Button logsBtn) logsBtn.Tag = null;

            // Set selected button
            if (selectedButton != null)
                selectedButton.Tag = "Selected";
        }

        private void UpdateRecordingPageDisplay()
        {
            if (FindName("SelectedChannelText") is TextBlock channelText)
            {
                channelText.Text = SelectedChannel?.Name ?? "None selected";
            }

            // Update recording status indicators
            bool isRecording = _recordProcess != null;

            if (FindName("RecordingPageStatusIndicator") is System.Windows.Shapes.Ellipse statusIndicator)
            {
                statusIndicator.Fill = isRecording
                    ? new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45)) // Red for recording
                    : new SolidColorBrush(Color.FromRgb(0x6C, 0x75, 0x7D)); // Gray for idle
            }

            if (FindName("RecordingStatusDisplay") is TextBlock statusText)
            {
                if (isRecording)
                {
                    statusText.Text = $"Recording: {SelectedChannel?.Name ?? "Unknown"}";
                }
                else
                {
                    statusText.Text = RecordingManager.Instance.StatusDisplay ?? "Idle";
                }
            }

            // Update new recording status section
            var recordingManager = RecordingManager.Instance;

            // Update status badge
            if (FindName("RecordingStatusBadge") is Border statusBadge && FindName("RecordingStatusBadgeText") is TextBlock badgeText)
            {
                badgeText.Text = recordingManager.StatusDisplay;

                statusBadge.Background = recordingManager.State switch
                {
                    RecordingState.Recording => new SolidColorBrush(Color.FromRgb(0x25, 0x6D, 0x1B)), // Green
                    RecordingState.Stopped => new SolidColorBrush(Color.FromRgb(0x5A, 0x3C, 0x05)), // Yellow/Orange
                    _ => new SolidColorBrush(Color.FromRgb(0x39, 0x41, 0x50)) // Gray
                };
            }

            // Show/hide recording details based on recording state
            if (FindName("RecordingDetailsPanel") is StackPanel detailsPanel && FindName("RecordingIdlePanel") is StackPanel idlePanel)
            {
                if (recordingManager.IsRecording)
                {
                    detailsPanel.Visibility = Visibility.Visible;
                    idlePanel.Visibility = Visibility.Collapsed;

                    // Update recording details
                    if (FindName("RecordingChannelText") is TextBlock channelTextBlock)
                        channelTextBlock.Text = recordingManager.ChannelName ?? "Unknown";

                    if (FindName("RecordingStartedText") is TextBlock startedTextBlock)
                        startedTextBlock.Text = recordingManager.StartedDisplay;

                    if (FindName("RecordingFileText") is TextBlock fileTextBlock)
                        fileTextBlock.Text = recordingManager.FileName ?? "--";

                    if (FindName("RecordingPathText") is TextBlock pathTextBlock)
                        pathTextBlock.Text = recordingManager.FilePath ?? "--";

                    if (FindName("RecordingDurationText") is TextBlock durationTextBlock)
                        durationTextBlock.Text = recordingManager.DurationDisplay;

                    if (FindName("RecordingSizeText") is TextBlock sizeTextBlock)
                        sizeTextBlock.Text = recordingManager.SizeDisplay;

                    if (FindName("RecordingBitrateText") is TextBlock bitrateTextBlock)
                        bitrateTextBlock.Text = recordingManager.BitrateDisplay;
                }
                else
                {
                    detailsPanel.Visibility = Visibility.Collapsed;
                    idlePanel.Visibility = Visibility.Visible;
                }
            }

            // Update output directory display
            if (FindName("RecordingOutputDirectoryText") is TextBlock outputDirText)
            {
                string actualDirectory = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                outputDirText.Text = actualDirectory;
            }
        }

        private async void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is Category category)
            {
                await LoadChannelsForCategoryAsync(category);
            }
        }


        private async void VodCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedValue is string categoryId)
            {
                // Load content based on current view (Movies or Series)
                bool isMoviesVisible = (FindName("MoviesGridScrollViewer") is ScrollViewer moviesGridViewer && moviesGridViewer.Visibility == Visibility.Visible) ||
                                     (FindName("MoviesListScrollViewer") is ScrollViewer moviesListViewer && moviesListViewer.Visibility == Visibility.Visible);
                bool isSeriesVisible = (FindName("SeriesGridScrollViewer") is ScrollViewer seriesGridViewer && seriesGridViewer.Visibility == Visibility.Visible) ||
                                     (FindName("SeriesListScrollViewer") is ScrollViewer seriesListViewer && seriesListViewer.Visibility == Visibility.Visible);

                if (isMoviesVisible)
                {
                    await LoadVodContentAsync(categoryId);
                }
                else if (isSeriesVisible)
                {
                    await LoadSeriesContentAsync(categoryId);
                }
            }
        }

        private void ShowMoviesView_Click(object sender, RoutedEventArgs e)
        {
            // Show movies and hide series based on current view mode
            if (FindName("MoviesGridScrollViewer") is ScrollViewer moviesGridViewer)
                moviesGridViewer.Visibility = IsVodGridView ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("MoviesListScrollViewer") is ScrollViewer moviesListViewer)
                moviesListViewer.Visibility = IsVodListView ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("SeriesGridScrollViewer") is ScrollViewer seriesGridViewer)
                seriesGridViewer.Visibility = Visibility.Collapsed;
            if (FindName("SeriesListScrollViewer") is ScrollViewer seriesListViewer)
                seriesListViewer.Visibility = Visibility.Collapsed;

            // Update button states
            if (FindName("MoviesViewBtn") is Button moviesBtn)
            {
                moviesBtn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x47));
                moviesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0xE6));
            }
            if (FindName("SeriesViewBtn") is Button seriesBtn)
            {
                seriesBtn.Background = Brushes.Transparent;
                seriesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0xB2, 0xC7));
            }

            // Update category combo to show VOD categories
            if (FindName("VodCategoryCombo") is ComboBox combo)
            {
                combo.ItemsSource = VodCategoriesCollectionView;
                combo.DisplayMemberPath = "CategoryName";
                combo.SelectedValuePath = "CategoryId";
            }

            // Clear VOD details panel
            ClearVodDetailsPanel();
        }

        private void ShowSeriesView_Click(object sender, RoutedEventArgs e)
        {
            // Show series and hide movies based on current view mode
            if (FindName("MoviesGridScrollViewer") is ScrollViewer moviesGridViewer)
                moviesGridViewer.Visibility = Visibility.Collapsed;
            if (FindName("MoviesListScrollViewer") is ScrollViewer moviesListViewer)
                moviesListViewer.Visibility = Visibility.Collapsed;
            if (FindName("SeriesGridScrollViewer") is ScrollViewer seriesGridViewer)
                seriesGridViewer.Visibility = IsVodGridView ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("SeriesListScrollViewer") is ScrollViewer seriesListViewer)
                seriesListViewer.Visibility = IsVodListView ? Visibility.Visible : Visibility.Collapsed;

            // Update button states
            if (FindName("MoviesViewBtn") is Button moviesBtn)
            {
                moviesBtn.Background = Brushes.Transparent;
                moviesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0xB2, 0xC7));
            }
            if (FindName("SeriesViewBtn") is Button seriesBtn)
            {
                seriesBtn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x47));
                seriesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0xE6));
            }

            // Update category combo to show series categories
            if (FindName("VodCategoryCombo") is ComboBox combo)
            {
                combo.ItemsSource = SeriesCategoriesCollectionView;
                combo.DisplayMemberPath = "CategoryName";
                combo.SelectedValuePath = "CategoryId";
            }

            // Clear VOD details panel
            ClearVodDetailsPanel();
        }

        private void SeriesContent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not SeriesContent series)
                return;

            // Check for double-click to launch player or open episodes
            var now = DateTime.UtcNow;
            const int doubleClickMs = 400;

            if (SelectedSeriesContent == series && (now - _lastSeriesClickTime).TotalMilliseconds <= doubleClickMs)
            {
                // Open series episodes or launch player
                TryLaunchSeriesInPlayer(series);
                _lastSeriesClickTime = DateTime.MinValue;
            }
            else
            {
                SelectedSeriesContent = series;
                _lastSeriesClickTime = now;

                // Update details panel
                ShowSeriesDetailsPanel(series);
            }
        }


        // Recording Scheduler Properties and Methods
        private readonly RecordingScheduler _scheduler = RecordingScheduler.Instance;
        private Channel? _schedulerSelectedChannel;
        private EpgEntry? _schedulerSelectedProgram;

        private void RecordingType_Changed(object sender, RoutedEventArgs e)
        {
            if (FindName("EpgPanel") is Panel epgPanel && FindName("CustomPanel") is Panel customPanel)
            {
                if (FindName("EpgRadio") is RadioButton epgRadio && epgRadio.IsChecked == true)
                {
                    epgPanel.IsEnabled = true;
                    customPanel.IsEnabled = false;
                }
                else
                {
                    epgPanel.IsEnabled = false;
                    customPanel.IsEnabled = true;
                }

                UpdateOutputFilePath();
            }
        }
        private void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FindName("ChannelCombo") is ComboBox channelCombo && channelCombo.SelectedItem is Channel channel)
            {
                _schedulerSelectedChannel = channel;
                LoadProgramsForChannel(channel);
                UpdateOutputFilePath();
            }
        }
        private void ProgramCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FindName("ProgramCombo") is ComboBox programCombo && programCombo.SelectedItem is EpgEntry program)
            {
                _schedulerSelectedProgram = program;
                if (FindName("TitleBox") is TextBox titleBox)
                {
                    // Remove the LIVE NOW indicator for the title box
                    var cleanTitle = program.Title.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");
                    titleBox.Text = cleanTitle;
                }
                if (FindName("ProgramTimeText") is TextBlock programTimeText)
                    programTimeText.Text = program.TimeRangeLocal;
                UpdateOutputFilePath();
            }
        }
        private void CustomChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateOutputFilePath();
        }
        private void CustomTime_Changed(object sender, EventArgs e)
        {
            UpdateOutputFilePath();
        }
        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Transport Stream|*.ts|MP4 Video|*.mp4|All Files|*.*",
                DefaultExt = ".ts"
            };

            if (FindName("OutputFileBox") is TextBox outputFileBox && !string.IsNullOrEmpty(outputFileBox.Text))
            {
                dialog.FileName = Path.GetFileName(outputFileBox.Text);
                dialog.InitialDirectory = Path.GetDirectoryName(outputFileBox.Text);
            }

            if (dialog.ShowDialog() == true)
            {
                if (FindName("OutputFileBox") is TextBox box)
                    box.Text = dialog.FileName;
            }
        }
        private void ScheduleRecording_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ScheduledRecording recording;

                if (FindName("EpgRadio") is RadioButton epgRadio && epgRadio.IsChecked == true)
                {
                    // EPG-based recording
                    if (_schedulerSelectedChannel == null || _schedulerSelectedProgram == null)
                    {
                        MessageBox.Show("Please select a channel and program.", "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // For currently airing shows, start recording now instead of at the original start time
                    var now = DateTime.UtcNow;
                    var isCurrentlyAiring = _schedulerSelectedProgram.StartUtc <= now && _schedulerSelectedProgram.EndUtc > now;
                    var effectiveStartTime = isCurrentlyAiring ? now : _schedulerSelectedProgram.StartUtc;

                    recording = new ScheduledRecording
                    {
                        Title = _schedulerSelectedProgram.Title.Replace("🔴 ", "").Replace(" (LIVE NOW)", ""),
                        Description = _schedulerSelectedProgram.Description ?? "",
                        ChannelId = _schedulerSelectedChannel.Id,
                        ChannelName = _schedulerSelectedChannel.Name,
                        StreamUrl = GetStreamUrlForChannel(_schedulerSelectedChannel),
                        StartTime = effectiveStartTime,
                        EndTime = _schedulerSelectedProgram.EndUtc,
                        IsEpgBased = true,
                        EpgProgramId = _schedulerSelectedProgram.GetHashCode().ToString()
                    };
                }
                else
                {
                    // Custom time recording
                    if (FindName("CustomChannelCombo") is not ComboBox customChannelCombo || customChannelCombo.SelectedItem is not Channel customChannel)
                    {
                        MessageBox.Show("Please select a channel.", "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var startTimeBox = FindName("StartTimeBox") as TextBox;
                    var endTimeBox = FindName("EndTimeBox") as TextBox;

                    if (!DateTime.TryParse(startTimeBox?.Text, out var startTime) ||
                        !DateTime.TryParse(endTimeBox?.Text, out var endTime))
                    {
                        MessageBox.Show("Please enter valid start and end times (HH:mm format).", "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var startDatePicker = FindName("StartDatePicker") as DatePicker;
                    var startDate = startDatePicker?.SelectedDate ?? DateTime.Today;
                    var startDateTime = startDate.Date.Add(startTime.TimeOfDay);
                    var endDateTime = startDate.Date.Add(endTime.TimeOfDay);

                    // If end time is before start time, assume it's the next day
                    if (endDateTime <= startDateTime)
                    {
                        endDateTime = endDateTime.AddDays(1);
                    }

                    var titleBox = FindName("TitleBox") as TextBox;
                    recording = new ScheduledRecording
                    {
                        Title = string.IsNullOrWhiteSpace(titleBox?.Text) ? "Custom Recording" : titleBox.Text,
                        ChannelId = customChannel.Id,
                        ChannelName = customChannel.Name,
                        StreamUrl = GetStreamUrlForChannel(customChannel),
                        StartTime = startDateTime.ToUniversalTime(),
                        EndTime = endDateTime.ToUniversalTime(),
                        IsEpgBased = false
                    };
                }

                // Set buffer times
                if (FindName("PreBufferBox") is TextBox preBufferBox && int.TryParse(preBufferBox.Text, out var preBuffer))
                    recording.PreBufferMinutes = preBuffer;
                if (FindName("PostBufferBox") is TextBox postBufferBox && int.TryParse(postBufferBox.Text, out var postBuffer))
                    recording.PostBufferMinutes = postBuffer;

                // Set output file path
                if (FindName("OutputFileBox") is TextBox outputFileBox && !string.IsNullOrWhiteSpace(outputFileBox.Text))
                    recording.OutputFilePath = outputFileBox.Text;

                // Check for conflicts
                if (_scheduler.HasConflictingRecording(recording.StartTime, recording.EndTime))
                {
                    var result = MessageBox.Show(
                        "This recording conflicts with an existing scheduled recording. Do you want to schedule it anyway?",
                        "Recording Conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                // Schedule the recording
                _scheduler.ScheduleRecording(recording);

                MessageBox.Show($"Recording scheduled successfully!\n\nTitle: {recording.Title}\nTime: {recording.TimeRangeText}",
                    "Recording Scheduled", MessageBoxButton.OK, MessageBoxImage.Information);

                // Reset form
                ResetSchedulerForm();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error scheduling recording: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetSchedulerForm()
        {
            if (FindName("TitleBox") is TextBox titleBox)
                titleBox.Text = "";
            if (FindName("ChannelCombo") is ComboBox channelCombo)
                channelCombo.SelectedIndex = -1;
            if (FindName("ProgramCombo") is ComboBox programCombo)
                programCombo.ItemsSource = null;
            if (FindName("ProgramTimeText") is TextBlock programTimeText)
                programTimeText.Text = "";
            if (FindName("CustomChannelCombo") is ComboBox customChannelCombo)
                customChannelCombo.SelectedIndex = -1;
            if (FindName("StartDatePicker") is DatePicker startDatePicker)
                startDatePicker.SelectedDate = DateTime.Today;
            if (FindName("StartTimeBox") is TextBox startTimeBox)
                startTimeBox.Text = "20:00";
            if (FindName("EndTimeBox") is TextBox endTimeBox)
                endTimeBox.Text = "21:00";
            if (FindName("PreBufferBox") is TextBox preBufferBox)
                preBufferBox.Text = "2";
            if (FindName("PostBufferBox") is TextBox postBufferBox)
                postBufferBox.Text = "5";
            if (FindName("OutputFileBox") is TextBox outputFileBox)
                outputFileBox.Text = "";

            _schedulerSelectedChannel = null;
            _schedulerSelectedProgram = null;
        }
        private void RefreshScheduled_Click(object sender, RoutedEventArgs e)
        {
            LoadScheduledRecordings();
        }
        private void DeleteCompleted_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will delete all completed, failed, and cancelled recordings from the list. Continue?",
                "Delete Completed Recordings", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _scheduler.DeleteCompletedRecordings();
            }
        }
        private void PropertiesRecording_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not ScheduledRecording recording)
                return;

            var properties = $"Recording Properties\n\n" +
                            $"Title: {recording.Title}\n" +
                            $"Channel: {recording.ChannelName}\n" +
                            $"Status: {recording.StatusText}\n" +
                            $"Start Time: {recording.StartTimeLocal}\n" +
                            $"End Time: {recording.EndTimeLocal}\n" +
                            $"Duration: {recording.DurationText}\n" +
                            $"Pre-buffer: {recording.PreBufferMinutes} minutes\n" +
                            $"Post-buffer: {recording.PostBufferMinutes} minutes\n" +
                            $"EPG-based: {(recording.IsEpgBased ? "Yes" : "No")}\n" +
                            $"Output File: {recording.OutputFilePath}\n" +
                            $"Stream URL: {recording.StreamUrl}\n";

            if (!string.IsNullOrEmpty(recording.Description))
            {
                properties += $"Description: {recording.Description}\n";
            }

            MessageBox.Show(properties, "Recording Properties", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void EditRecording_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not ScheduledRecording recording)
                return;

            // Simple edit dialog using message boxes for now
            var editMessage = $"Current recording details:\n\n" +
                             $"Title: {recording.Title}\n" +
                             $"Pre-buffer: {recording.PreBufferMinutes} minutes\n" +
                             $"Post-buffer: {recording.PostBufferMinutes} minutes\n\n" +
                             $"This is a basic edit confirmation. Would you like to add 1 minute to both pre and post buffer?";

            var result = MessageBox.Show(editMessage, "Edit Recording",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            // Update the recording with increased buffer times
            var updatedRecording = new ScheduledRecording
            {
                Id = recording.Id,
                Title = recording.Title,
                Description = recording.Description,
                ChannelId = recording.ChannelId,
                ChannelName = recording.ChannelName,
                StreamUrl = recording.StreamUrl,
                StartTime = recording.StartTime,
                EndTime = recording.EndTime,
                Status = recording.Status,
                OutputFilePath = recording.OutputFilePath,
                IsEpgBased = recording.IsEpgBased,
                EpgProgramId = recording.EpgProgramId,
                PreBufferMinutes = recording.PreBufferMinutes + 1,
                PostBufferMinutes = recording.PostBufferMinutes + 1,
                CreatedAt = recording.CreatedAt
            };

            _scheduler.UpdateRecording(updatedRecording);

            MessageBox.Show("Recording updated successfully!", "Edit Recording",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void CancelRecording_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not ScheduledRecording recording)
                return;

            var result = MessageBox.Show($"Cancel recording '{recording.Title}'?", "Cancel Recording",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _scheduler.CancelRecording(recording.Id);
            }
        }

        // Favorites page methods
        private void LoadFavoritesPage()
        {
            try
            {
                var favoriteChannels = Session.GetFavoriteChannels();
                var channels = new List<Channel>();

                // Convert FavoriteChannel objects to Channel objects and load their current data
                foreach (var favorite in favoriteChannels)
                {
                    // Try to find the channel in current categories/channels for fresh data
                    var existingChannel = _channels.FirstOrDefault(c => c.Id == favorite.Id);
                    if (existingChannel != null)
                    {
                        // Use existing channel data (includes fresh logo, EPG, etc.)
                        var favoriteChannel = new Channel
                        {
                            Id = existingChannel.Id,
                            Name = existingChannel.Name,
                            Logo = existingChannel.Logo,
                            LogoImage = existingChannel.LogoImage,
                            EpgChannelId = existingChannel.EpgChannelId,
                            NowTitle = existingChannel.NowTitle,
                            NowTimeRange = existingChannel.NowTimeRange,
                            EpgLoaded = existingChannel.EpgLoaded,
                            EpgLoading = existingChannel.EpgLoading,
                            IsRecording = existingChannel.IsRecording,
                            IsFavorite = true
                        };
                        channels.Add(favoriteChannel);
                    }
                    else
                    {
                        // Use stored favorite data when channel isn't currently loaded
                        var favoriteChannel = new Channel
                        {
                            Id = favorite.Id,
                            Name = favorite.Name,
                            Logo = favorite.Logo,
                            EpgChannelId = favorite.EpgChannelId,
                            LogoImage = null, // Will be loaded later
                            IsFavorite = true
                        };
                        channels.Add(favoriteChannel);
                    }
                }

                // Update UI
                if (FindName("FavoritesChannelsControl") is ItemsControl favoritesControl)
                {
                    favoritesControl.ItemsSource = channels;
                }

                // Update count and visibility
                UpdateFavoritesDisplay(channels.Count);

                // Load logos for favorites that don't have them
                _ = Task.Run(() => LoadFavoriteLogosAsync(channels));
            }
            catch (Exception ex)
            {
                Log($"Error loading favorites: {ex.Message}\n");
            }
        }

        private void UpdateFavoritesDisplay(int count)
        {
            if (FindName("FavoritesCountLabel") is TextBlock countLabel)
            {
                countLabel.Text = count == 0 ? "No favorites" :
                                 count == 1 ? "1 favorite" :
                                 $"{count} favorites";
            }

            if (FindName("NoFavoritesMessage") is Border noFavoritesMessage)
            {
                noFavoritesMessage.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (FindName("FavoritesChannelsControl") is ItemsControl favoritesControl)
            {
                favoritesControl.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateChannelsFavoriteStatus()
        {
            // Optimize: Get all favorites once instead of reading file for each channel
            var favoriteChannels = Session.GetFavoriteChannels();
            var favoriteIds = favoriteChannels.Select(f => f.Id).ToHashSet();

            // Update IsFavorite property for all loaded channels
            foreach (var channel in _channels)
            {
                channel.IsFavorite = favoriteIds.Contains(channel.Id);
            }
        }

        private void OnFavoritesChanged()
        {
            // Update favorite status of all loaded channels when favorites change
            Dispatcher.Invoke(() =>
            {
                UpdateChannelsFavoriteStatus();

                // If we're on the favorites page, refresh it
                if (FindName("FavoritesPage") is FrameworkElement favoritesPage &&
                    favoritesPage.Visibility == Visibility.Visible)
                {
                    LoadFavoritesPage();
                }
            });
        }

        private async Task LoadFavoriteLogosAsync(List<Channel> favoriteChannels)
        {
            foreach (var channel in favoriteChannels.Where(c => !string.IsNullOrWhiteSpace(c.Logo) && c.LogoImage == null))
            {
                if (_cts.IsCancellationRequested) return;

                try
                {
                    var logoImage = await _cacheService.GetChannelLogoAsync(channel.Id, channel.Logo, _cts.Token);
                    if (logoImage != null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            channel.LogoImage = logoImage;
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error loading logo for favorite channel {channel.Name}: {ex.Message}\n");
                }
            }
        }

        private void RefreshFavorites_Click(object sender, RoutedEventArgs e)
        {
            LoadFavoritesPage();
        }

        private void ClearAllFavorites_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this,
                "Are you sure you want to remove all channels from favorites?",
                "Clear All Favorites",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // Get current favorites and remove them
                var favoriteChannels = Session.GetFavoriteChannels();
                foreach (var favorite in favoriteChannels)
                {
                    Session.RemoveFavoriteChannel(favorite.Id);
                }

                // Refresh the favorites page
                LoadFavoritesPage();

                Log("All favorite channels cleared\n");
            }
        }

        private void RemoveFromFavorites_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Channel channel)
            {
                Session.RemoveFavoriteChannel(channel.Id);

                // Refresh the favorites page
                LoadFavoritesPage();

                Log($"Removed '{channel.Name}' from favorites\n");
            }
        }

        private void FavoritesViewToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Switch to list view (implementation can be added later if needed)
        }

        private void FavoritesViewToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            // Switch to grid view (default)
        }

        // Initialize scheduler when navigating to Scheduler tab
        private void InitializeScheduler()
        {
            InitializeSchedulerWindow();
            LoadSchedulerChannels();
            LoadScheduledRecordings();
        }

        private void InitializeSchedulerWindow()
        {
            if (FindName("StartDatePicker") is DatePicker startDatePicker)
                startDatePicker.SelectedDate = DateTime.Today;
            if (FindName("StartTimeBox") is TextBox startTimeBox)
                startTimeBox.Text = "20:00";
            if (FindName("EndTimeBox") is TextBox endTimeBox)
                endTimeBox.Text = "21:00";
            UpdateOutputFilePath();
        }

        private void LoadSchedulerChannels()
        {
            var channels = Channels.OrderBy(c => c.Name).ToList();
            if (FindName("ChannelCombo") is ComboBox channelCombo)
                channelCombo.ItemsSource = channels;
            if (FindName("CustomChannelCombo") is ComboBox customChannelCombo)
                customChannelCombo.ItemsSource = channels;
        }

        private void LoadScheduledRecordings()
        {
            if (FindName("ScheduledGrid") is DataGrid scheduledGrid)
                scheduledGrid.ItemsSource = _scheduler.ScheduledRecordings;
        }

        private async void LoadProgramsForChannel(Channel channel)
        {
            var now = DateTime.UtcNow;
            var programs = new List<EpgEntry>();

            if (Session.Mode == SessionMode.M3u)
            {
                // For M3U mode, use cached EPG data
                if (!string.IsNullOrWhiteSpace(channel.EpgChannelId) &&
                    Session.M3uEpgByChannel.TryGetValue(channel.EpgChannelId, out var entries))
                {
                    programs = entries
                        .Where(epg => epg.EndUtc > now) // Include currently airing shows (end time must be in future)
                        .Select(epg =>
                        {
                            var isCurrentlyAiring = epg.StartUtc <= now && epg.EndUtc > now;
                            var displayTitle = isCurrentlyAiring
                                ? $"🔴 {epg.Title} (LIVE NOW)"
                                : epg.Title;

                            return new EpgEntry
                            {
                                StartUtc = epg.StartUtc,
                                EndUtc = epg.EndUtc,
                                Title = displayTitle,
                                Description = epg.Description
                            };
                        })
                        .OrderBy(epg => epg.StartUtc)
                        .Take(50) // Limit to next 50 programs
                        .ToList();
                }
            }
            else if (Session.Mode == SessionMode.Xtream)
            {
                // For Xtream mode, fetch EPG data via API
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    var url = Session.BuildApi("get_simple_data_table") + "&stream_id=" + channel.Id;

                    System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Fetching EPG for channel {channel.Name}: {url}");

                    using var resp = await http.GetAsync(url);
                    var json = await resp.Content.ReadAsStringAsync();

                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var trimmed = json.AsSpan().TrimStart();
                        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("epg_listings", out var listings) &&
                                listings.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var el in listings.EnumerateArray())
                                {
                                    var start = GetUnixTimestamp(el, "start_timestamp");
                                    var end = GetUnixTimestamp(el, "stop_timestamp");

                                    if (start == DateTime.MinValue || end == DateTime.MinValue) continue;
                                    if (end <= now) continue; // Skip programs that have already ended (but include currently airing)

                                    var title = GetStringValue(el, "title", "name", "programme", "program");
                                    var desc = GetStringValue(el, "description", "desc", "info", "plot", "short_description");

                                    if (!string.IsNullOrWhiteSpace(title))
                                    {
                                        var isCurrentlyAiring = start <= now && end > now;
                                        var displayTitle = isCurrentlyAiring
                                            ? $"🔴 {DecodeMaybeBase64(title)} (LIVE NOW)"
                                            : DecodeMaybeBase64(title);

                                        programs.Add(new EpgEntry
                                        {
                                            StartUtc = start,
                                            EndUtc = end,
                                            Title = displayTitle,
                                            Description = DecodeMaybeBase64(desc ?? "")
                                        });
                                    }
                                }

                                programs = programs
                                    .OrderBy(epg => epg.StartUtc)
                                    .Take(50)
                                    .ToList();
                            }
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Loaded {programs.Count} programs for channel {channel.Name}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Error loading EPG for channel {channel.Name}: {ex.Message}");
                }
            }

            if (FindName("ProgramCombo") is ComboBox programCombo)
            {
                programCombo.ItemsSource = programs;

                if (programs.Any())
                {
                    programCombo.SelectedIndex = 0;
                }
            }
        }

        private void UpdateOutputFilePath()
        {
            if (FindName("OutputFileBox") is not TextBox outputFileBox) return;

            if (FindName("EpgRadio") is RadioButton epgRadio && epgRadio.IsChecked == true && _schedulerSelectedChannel != null && _schedulerSelectedProgram != null)
            {
                // Clean the title by removing emoji and LIVE NOW indicator
                var cleanTitle = _schedulerSelectedProgram.Title.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");
                var sanitizedTitle = SanitizeFileName(cleanTitle);
                var sanitizedChannel = SanitizeFileName(_schedulerSelectedChannel.Name);
                var timestamp = _schedulerSelectedProgram.StartUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm");
                var fileName = $"{sanitizedChannel}_{sanitizedTitle}_{timestamp}.ts";
                var recordingDir = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                outputFileBox.Text = Path.Combine(recordingDir, fileName);
            }
            else if (FindName("CustomRadio") is RadioButton customRadio && customRadio.IsChecked == true && FindName("CustomChannelCombo") is ComboBox customChannelCombo && customChannelCombo.SelectedItem is Channel customChannel)
            {
                var titleBox = FindName("TitleBox") as TextBox;
                var title = string.IsNullOrWhiteSpace(titleBox?.Text) ? "Custom_Recording" : titleBox.Text;
                var sanitizedTitle = SanitizeFileName(title);
                var sanitizedChannel = SanitizeFileName(customChannel.Name);
                var startDatePicker = FindName("StartDatePicker") as DatePicker;
                var startDate = startDatePicker?.SelectedDate ?? DateTime.Today;
                var timestamp = startDate.ToString("yyyy-MM-dd_HH-mm");
                var fileName = $"{sanitizedChannel}_{sanitizedTitle}_{timestamp}.ts";
                var recordingDir = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                outputFileBox.Text = Path.Combine(recordingDir, fileName);
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "Unknown";

            // First remove emoji and LIVE NOW indicators
            var cleaned = fileName.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");

            // Remove other emoji characters (basic cleanup)
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[\uD800-\uDBFF\uDC00-\uDFFF]", "");

            // Remove invalid file name characters
            var invalidChars = Path.GetInvalidFileNameChars();
            var result = string.Join("_", cleaned.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

            // Trim and ensure we have something
            result = result.Trim('_', ' ');
            return string.IsNullOrWhiteSpace(result) ? "Recording" : result;
        }

        private string GetStreamUrlForChannel(Channel channel)
        {
            if (Session.Mode == SessionMode.M3u)
            {
                // For M3U mode, find the corresponding playlist entry
                var playlistEntry = Session.PlaylistChannels.FirstOrDefault(p => p.Id == channel.Id);
                return playlistEntry?.StreamUrl ?? "";
            }
            else
            {
                // For Xtream mode, build the stream URL
                return Session.BuildStreamUrl(channel.Id, "ts");
            }
        }

        // Helper methods for recording scheduler EPG parsing
        private static DateTime GetUnixTimestamp(System.Text.Json.JsonElement el, params string[] props)
        {
            foreach (var prop in props)
            {
                if (el.TryGetProperty(prop, out var val))
                {
                    if (val.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(val.GetString(), out var ts))
                        return DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
                    if (val.ValueKind == System.Text.Json.JsonValueKind.Number && val.TryGetInt64(out var tsNum))
                        return DateTimeOffset.FromUnixTimeSeconds(tsNum).UtcDateTime;
                }
            }
            return DateTime.MinValue;
        }

        private static string GetStringValue(System.Text.Json.JsonElement el, params string[] props)
        {
            foreach (var prop in props)
            {
                if (el.TryGetProperty(prop, out var val) && val.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var str = val.GetString();
                    if (!string.IsNullOrWhiteSpace(str)) return str;
                }
            }
            return "";
        }

        // VOD Details Panel Methods
        private VodContent? _currentSubscribedVod;

        private void ClearVodDetailsPanel()
        {
            // Clear selections
            SelectedVodContent = null;
            SelectedSeriesContent = null;

            // Unsubscribe from property changes
            if (_currentSubscribedVod != null)
            {
                _currentSubscribedVod.PropertyChanged -= VodContent_PropertyChanged;
                _currentSubscribedVod = null;
            }
            if (_currentSubscribedSeries != null)
            {
                _currentSubscribedSeries.PropertyChanged -= SeriesContent_PropertyChanged;
                _currentSubscribedSeries = null;
            }

            // Reset UI to show placeholder
            if (FindName("VodDetailsPlaceholder") is TextBlock placeholder)
            {
                placeholder.Text = "Select a movie or series to view details";
                placeholder.Visibility = Visibility.Visible;
            }

            if (FindName("VodDetailsContent") is StackPanel content)
            {
                content.Visibility = Visibility.Collapsed;
            }

            // Hide episodes section when clearing
            HideEpisodesUI();

            // Hide actions panel when clearing
            if (FindName("VodActionsPanel") is StackPanel actionsPanel)
                actionsPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowVodDetailsPanel(VodContent vod)
        {
            try
            {
                // Unsubscribe from previous VOD property changes
                if (_currentSubscribedVod != null)
                {
                    _currentSubscribedVod.PropertyChanged -= VodContent_PropertyChanged;
                }

                // Subscribe to this VOD's property changes
                _currentSubscribedVod = vod;
                vod.PropertyChanged += VodContent_PropertyChanged;

                // If details are already loaded, show them immediately
                if (vod.DetailsLoaded)
                {
                    DisplayVodDetailsPanel(vod);
                    return;
                }

                // Show loading state
                if (FindName("VodDetailsPlaceholder") is TextBlock placeholder)
                {
                    placeholder.Text = "Loading details...";
                    placeholder.Visibility = Visibility.Visible;
                }

                if (FindName("VodDetailsContent") is StackPanel content)
                    content.Visibility = Visibility.Collapsed;

                // Start loading details in background (fire and forget)
                _ = LoadVodDetailsAsync(vod);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error showing VOD details: {ex.Message}");
                // Show error state
                if (FindName("VodDetailsPlaceholder") is TextBlock placeholder)
                {
                    placeholder.Text = "Failed to load details";
                    placeholder.Visibility = Visibility.Visible;
                }
                if (FindName("VodDetailsContent") is StackPanel content)
                    content.Visibility = Visibility.Collapsed;
            }
        }

        private void VodContent_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "DetailsLoaded" &&
                sender is VodContent vod && vod.DetailsLoaded && ReferenceEquals(vod, this.SelectedVodContent))
            {
                // Details have been loaded for the currently selected VOD, update UI
                Dispatcher.Invoke(() => DisplayVodDetailsPanel(vod));
            }
        }

        private void DisplayVodDetailsPanel(VodContent vod)
        {
            // Hide placeholder, show content
            if (FindName("VodDetailsPlaceholder") is TextBlock placeholder)
                placeholder.Visibility = Visibility.Collapsed;

            if (FindName("VodDetailsContent") is StackPanel content)
                content.Visibility = Visibility.Visible;

            // Set title
            if (FindName("VodDetailsTitle") is TextBlock title)
                title.Text = vod.Name ?? "Unknown";

            // Update all detail fields
            UpdateVodDetailsDisplay(vod);
        }

        private void UpdateVodDetailsDisplay(VodContent vod)
        {
            // Only update if details are actually loaded
            if (!vod.DetailsLoaded)
            {
                // Keep showing loading state
                return;
            }

            // Update all fields with loaded data
            if (FindName("VodDetailsYear") is TextBlock year)
                year.Text = !string.IsNullOrWhiteSpace(vod.ReleaseDate) ? vod.DisplayYear : "";

            if (FindName("VodDetailsDuration") is TextBlock duration)
                duration.Text = !string.IsNullOrWhiteSpace(vod.Duration) ? vod.DisplayDuration : "";

            if (FindName("VodDetailsRating") is TextBlock rating)
                rating.Text = !string.IsNullOrWhiteSpace(vod.Rating) ? vod.Rating : "";

            if (FindName("VodDetailsGenre") is TextBlock genre)
                genre.Text = !string.IsNullOrWhiteSpace(vod.Genre) ? vod.Genre : "";

            if (FindName("VodDetailsCast") is TextBlock cast)
                cast.Text = !string.IsNullOrWhiteSpace(vod.Cast) ? vod.Cast : "";

            if (FindName("VodDetailsDirector") is TextBlock director)
                director.Text = !string.IsNullOrWhiteSpace(vod.Director) ? vod.Director : "";

            if (FindName("VodDetailsPlot") is TextBlock plot)
                plot.Text = !string.IsNullOrWhiteSpace(vod.Plot) ? vod.Plot : "No plot available";

            // Hide episodes section for movies
            HideEpisodesUI();

            // Show play button for movies only
            if (FindName("VodActionsPanel") is StackPanel actionsPanel)
                actionsPanel.Visibility = Visibility.Visible;
        }

        private SeriesContent? _currentSubscribedSeries;

        private void ShowSeriesDetailsPanel(SeriesContent series)
        {
            try
            {
                // Unsubscribe from previous series property changes
                if (_currentSubscribedSeries != null)
                {
                    _currentSubscribedSeries.PropertyChanged -= SeriesContent_PropertyChanged;
                }

                // Subscribe to this series' property changes
                _currentSubscribedSeries = series;
                series.PropertyChanged += SeriesContent_PropertyChanged;

                // If details are already loaded, show them immediately
                if (series.DetailsLoaded)
                {
                    DisplaySeriesDetailsPanel(series);
                    return;
                }

                // Show loading state
                if (FindName("VodDetailsPlaceholder") is TextBlock placeholder)
                {
                    placeholder.Text = "Loading details...";
                    placeholder.Visibility = Visibility.Visible;
                }

                if (FindName("VodDetailsContent") is StackPanel content)
                    content.Visibility = Visibility.Collapsed;

                // Start loading details in background (fire and forget)
                _ = LoadSeriesDetailsAsync(series);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error showing series details: {ex.Message}");
                // Show error state
                if (FindName("VodDetailsPlaceholder") is TextBlock placeholder)
                {
                    placeholder.Text = "Failed to load details";
                    placeholder.Visibility = Visibility.Visible;
                }
                if (FindName("VodDetailsContent") is StackPanel content)
                    content.Visibility = Visibility.Collapsed;
            }
        }

        private void SeriesContent_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "DetailsLoaded" &&
                sender is SeriesContent series && series.DetailsLoaded && ReferenceEquals(series, this.SelectedSeriesContent))
            {
                // Details have been loaded for the currently selected series, update UI
                Dispatcher.Invoke(() => DisplaySeriesDetailsPanel(series));
            }
        }

        private void DisplaySeriesDetailsPanel(SeriesContent series)
        {
            // Hide placeholder, show content
            if (FindName("VodDetailsPlaceholder") is TextBlock placeholder)
                placeholder.Visibility = Visibility.Collapsed;

            if (FindName("VodDetailsContent") is StackPanel content)
                content.Visibility = Visibility.Visible;

            // Set title
            if (FindName("VodDetailsTitle") is TextBlock title)
                title.Text = series.Name ?? "Unknown";

            // Update all detail fields
            UpdateSeriesDetailsDisplay(series);
        }

        private void UpdateSeriesDetailsDisplay(SeriesContent series)
        {
            // Only update if details are actually loaded
            if (!series.DetailsLoaded)
            {
                // Keep showing loading state
                return;
            }

            // Update all fields with loaded data
            if (FindName("VodDetailsYear") is TextBlock year)
                year.Text = !string.IsNullOrWhiteSpace(series.ReleaseDate) ? series.DisplayYear : "";

            if (FindName("VodDetailsDuration") is TextBlock duration)
                duration.Text = series.SeasonCount > 0 ? series.DisplayDuration : "";

            if (FindName("VodDetailsRating") is TextBlock rating)
                rating.Text = !string.IsNullOrWhiteSpace(series.Rating) ? series.Rating : "";

            if (FindName("VodDetailsGenre") is TextBlock genre)
                genre.Text = !string.IsNullOrWhiteSpace(series.Genre) ? series.Genre : "";

            if (FindName("VodDetailsCast") is TextBlock cast)
                cast.Text = !string.IsNullOrWhiteSpace(series.Cast) ? series.Cast : "";

            if (FindName("VodDetailsDirector") is TextBlock director)
                director.Text = !string.IsNullOrWhiteSpace(series.Director) ? series.Director : "";

            if (FindName("VodDetailsPlot") is TextBlock plot)
                plot.Text = !string.IsNullOrWhiteSpace(series.Plot) ? series.Plot : "No plot available";

            // Hide play button for TV shows (use episodes instead)
            if (FindName("VodActionsPanel") is StackPanel actionsPanel)
                actionsPanel.Visibility = Visibility.Collapsed;

            // Show episodes section for series and populate it
            PopulateEpisodesUI(series);
        }

        private void PopulateEpisodesUI(SeriesContent series)
        {
            // Show episodes section
            if (FindName("EpisodesSection") is Border episodesSection)
                episodesSection.Visibility = Visibility.Visible;

            // Clear existing episodes
            if (FindName("SeasonsPanel") is StackPanel seasonsPanel)
            {
                seasonsPanel.Children.Clear();

                foreach (var season in series.Seasons)
                {
                    // Create season header
                    var seasonHeader = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x47)),
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 4, 0, 2),
                        Padding = new Thickness(8, 4, 8, 4)
                    };

                    var seasonHeaderText = new TextBlock
                    {
                        Text = $"{season.DisplayName} ({season.Episodes.Count} episodes)",
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE6, 0xF2)),
                        FontSize = 12
                    };

                    seasonHeader.Child = seasonHeaderText;
                    seasonsPanel.Children.Add(seasonHeader);

                    // Create episodes list
                    foreach (var episode in season.Episodes)
                    {
                        var episodeButton = new Button
                        {
                            Content = episode.DisplayTitle,
                            Margin = new Thickness(8, 1, 0, 1),
                            Padding = new Thickness(8, 4, 8, 4),
                            Background = Brushes.Transparent,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0xB2, 0xC7)),
                            BorderBrush = Brushes.Transparent,
                            BorderThickness = new Thickness(0),
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            FontSize = 11,
                            Cursor = System.Windows.Input.Cursors.Hand
                        };

                        episodeButton.Click += (s, e) => TryLaunchEpisodeInPlayer(episode);
                        seasonsPanel.Children.Add(episodeButton);
                    }
                }
            }
        }

        private void HideEpisodesUI()
        {
            // Hide episodes section
            if (FindName("EpisodesSection") is Border episodesSection)
                episodesSection.Visibility = Visibility.Collapsed;

            // Clear episodes
            if (FindName("SeasonsPanel") is StackPanel seasonsPanel)
                seasonsPanel.Children.Clear();
        }

        private void VodPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedVodContent != null)
            {
                TryLaunchVodInPlayer(SelectedVodContent);
            }
            else if (SelectedSeriesContent != null)
            {
                TryLaunchSeriesInPlayer(SelectedSeriesContent);
            }
        }


        // Profile page methods
        private void LoadProfileData()
        {
            try
            {
                // Update account information
                if (FindName("UsernameText") is TextBlock usernameText)
                    usernameText.Text = Session.Username ?? "Unknown";

                if (FindName("StatusText") is TextBlock statusText)
                    statusText.Text = Session.UserInfo?.status ?? "Unknown";

                if (FindName("IsTrialText") is TextBlock isTrialText)
                    isTrialText.Text = Session.UserInfo?.is_trial ?? "Unknown";

                if (FindName("ExpiryDateText") is TextBlock expiryText)
                {
                    if (Session.UserInfo?.exp_date != null && long.TryParse(Session.UserInfo.exp_date, out var expTimestamp))
                    {
                        var expiry = DateTimeOffset.FromUnixTimeSeconds(expTimestamp).DateTime;
                        expiryText.Text = expiry.ToString("MM/dd/yyyy hh:mm tt");
                    }
                    else
                        expiryText.Text = "Never";
                }

                if (FindName("MaxConnectionsText") is TextBlock maxConnText)
                    maxConnText.Text = Session.UserInfo?.max_connections ?? "Unknown";

                if (FindName("ActiveConnectionsText") is TextBlock activeConnText)
                    activeConnText.Text = Session.UserInfo?.active_cons ?? "0";

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading profile data: {ex.Message}");
            }
        }

        private async void RefreshProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Re-fetch user info if in Xtream mode
                if (Session.Mode == SessionMode.Xtream)
                {
                    var url = Session.BuildApi("get_account_info");
                    var response = await _http.GetStringAsync(url);
                    var userInfo = JsonSerializer.Deserialize<UserInfo>(response);

                    if (userInfo != null)
                    {
                        Session.UserInfo = userInfo;
                    }
                }

                LoadProfileData();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing profile: {ex.Message}");
            }
        }


        // Settings page methods
        private void LoadSettingsPage()
        {
            try
            {
                if (FindName("SettingsPlayerKindCombo") is ComboBox playerCombo)
                {
                    playerCombo.SelectedIndex = Session.PreferredPlayer switch
                    {
                        PlayerKind.VLC => 0,
                        PlayerKind.MPCHC => 1,
                        PlayerKind.MPV => 2,
                        PlayerKind.Custom => 3,
                        _ => 0
                    };
                }

                if (FindName("SettingsPlayerExeTextBox") is TextBox playerExe)
                    playerExe.Text = Session.PlayerExePath ?? string.Empty;

                if (FindName("SettingsArgsTemplateTextBox") is TextBox argsTemplate)
                    argsTemplate.Text = Session.PlayerArgsTemplate ?? string.Empty;

                if (FindName("SettingsFfmpegPathTextBox") is TextBox ffmpegPath)
                    ffmpegPath.Text = Session.FfmpegPath ?? string.Empty;

                if (FindName("SettingsRecordingDirTextBox") is TextBox recordingDir)
                    recordingDir.Text = Session.RecordingDirectory ?? string.Empty;

                if (FindName("SettingsFfmpegArgsTextBox") is TextBox ffmpegArgs)
                    ffmpegArgs.Text = Session.FfmpegArgsTemplate ?? string.Empty;

                if (FindName("SettingsLastEpgUpdateTextBox") is TextBox lastEpg)
                {
                    lastEpg.Text = Session.LastEpgUpdateUtc.HasValue
                        ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g")
                        : "(never)";
                }

                if (FindName("SettingsEpgIntervalTextBox") is TextBox epgInterval)
                    epgInterval.Text = ((int)Session.EpgRefreshInterval.TotalMinutes).ToString();

                if (FindName("SettingsCachingEnabledCheckBox") is CheckBox cachingEnabled)
                    cachingEnabled.IsChecked = Session.CachingEnabled;

                // Set credentials folder path
                if (FindName("SettingsCredentialsFolderTextBox") is TextBox credentialsFolder)
                {
                    credentialsFolder.Text = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "IPTV-Desktop-Browser");
                }

                ValidateAllSettingsFields();

                // Initialize logging display
                Log("Settings page loaded. Raw output logging is active.\n");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error loading settings: {ex.Message}", true);
            }
        }

        private void SetSettingsStatusMessage(string message, bool isError = false)
        {
            if (FindName("SettingsStatusText") is TextBlock statusText)
            {
                statusText.Text = message;
                statusText.Foreground = new SolidColorBrush(isError ? Color.FromRgb(0xF8, 0x81, 0x66) : Color.FromRgb(0x8B, 0xA1, 0xB9));
            }
        }

        private void ValidateAllSettingsFields()
        {
            ValidateSettingsPlayerPath();
            ValidateSettingsFfmpegPath();
            ValidateSettingsRecordingDirectory();
            ValidateSettingsEpgInterval();
        }

        private void ValidateSettingsPlayerPath()
        {
            if (FindName("SettingsPlayerExeTextBox") is not TextBox pathBox || FindName("SettingsPlayerPathStatus") is not TextBlock status)
                return;

            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                status.Text = "Path is required for custom players";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
            else if (!File.Exists(path))
            {
                status.Text = "File not found";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
            else
            {
                status.Text = "✓ Valid executable";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
        }

        private void ValidateSettingsFfmpegPath()
        {
            if (FindName("SettingsFfmpegPathTextBox") is not TextBox pathBox || FindName("SettingsFfmpegPathStatus") is not TextBlock status)
                return;

            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                status.Text = "FFmpeg path not set (recording disabled)";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));
            }
            else if (!File.Exists(path))
            {
                status.Text = "FFmpeg executable not found";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
            else
            {
                status.Text = "✓ FFmpeg ready for recording";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
        }

        private void ValidateSettingsRecordingDirectory()
        {
            if (FindName("SettingsRecordingDirTextBox") is not TextBox pathBox || FindName("SettingsRecordingDirStatus") is not TextBlock status)
                return;

            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                status.Text = "Using default: My Videos folder";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));
            }
            else if (!Directory.Exists(path))
            {
                status.Text = "Directory will be created when recording";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xC5, 0x55));
            }
            else
            {
                status.Text = "✓ Directory exists";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
        }

        private void ValidateSettingsEpgInterval()
        {
            if (FindName("SettingsEpgIntervalTextBox") is not TextBox textBox || FindName("SettingsEpgIntervalStatus") is not TextBlock status)
                return;

            var text = textBox.Text.Trim();
            if (int.TryParse(text, out var minutes) && minutes >= 5 && minutes <= 720)
            {
                status.Text = $"✓ EPG will refresh every {minutes} minutes";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
            else
            {
                status.Text = "Invalid interval (5-720 minutes allowed)";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
        }

        private void SettingsAutoDetectPlayer_Click(object sender, RoutedEventArgs e)
        {
            var kind = GetSettingsSelectedPlayerKind();
            var detectedPath = DetectSettingsPlayerPath(kind);

            if (!string.IsNullOrEmpty(detectedPath))
            {
                if (FindName("SettingsPlayerExeTextBox") is TextBox textBox)
                    textBox.Text = detectedPath;
                SetSettingsStatusMessage($"Auto-detected {kind} player");
                ValidateSettingsPlayerPath();
            }
            else
            {
                SetSettingsStatusMessage($"Could not auto-detect {kind} player", true);
            }
        }

        private void SettingsAutoDetectFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            var detectedPath = DetectSettingsFfmpegPath();

            if (!string.IsNullOrEmpty(detectedPath))
            {
                if (FindName("SettingsFfmpegPathTextBox") is TextBox textBox)
                    textBox.Text = detectedPath;
                SetSettingsStatusMessage("Auto-detected FFmpeg");
                ValidateSettingsFfmpegPath();
            }
            else
            {
                SetSettingsStatusMessage("Could not auto-detect FFmpeg", true);
            }
        }

        private string DetectSettingsPlayerPath(PlayerKind kind)
        {
            var commonPaths = kind switch
            {
                PlayerKind.VLC => new[]
                {
                    @"C:\Program Files\VideoLAN\VLC\vlc.exe",
                    @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\VideoLAN\VLC\vlc.exe"),
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\VideoLAN\VLC\vlc.exe")
                },
                PlayerKind.MPCHC => new[]
                {
                    @"C:\Program Files\MPC-HC\mpc-hc64.exe",
                    @"C:\Program Files (x86)\MPC-HC\mpc-hc.exe",
                    @"C:\Program Files\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                    @"C:\Program Files (x86)\K-Lite Codec Pack\MPC-HC\mpc-hc.exe"
                },
                PlayerKind.MPV => new[]
                {
                    @"C:\Program Files\mpv\mpv.exe",
                    @"C:\Program Files (x86)\mpv\mpv.exe"
                },
                _ => Array.Empty<string>()
            };

            return commonPaths.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private string DetectSettingsFfmpegPath()
        {
            var commonPaths = new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\ffmpeg\bin\ffmpeg.exe"),
                "ffmpeg.exe"
            };

            foreach (var path in commonPaths)
            {
                try
                {
                    if (Path.GetFileName(path) == "ffmpeg.exe" && path == "ffmpeg.exe")
                    {
                        var process = Process.Start(new ProcessStartInfo
                        {
                            FileName = "where",
                            Arguments = "ffmpeg",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        });

                        if (process != null)
                        {
                            var output = process.StandardOutput.ReadToEnd();
                            process.WaitForExit();
                            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                            {
                                return output.Split('\n')[0].Trim();
                            }
                        }
                    }
                    else if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        // Settings event handlers
        private void SettingsPlayerExeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSettingsPlayerPath();
        }

        private void SettingsFfmpegPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSettingsFfmpegPath();
        }

        private void SettingsRecordingDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSettingsRecordingDirectory();
        }

        private void SettingsEpgIntervalTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSettingsEpgInterval();
        }

        private void SettingsArgsTemplateTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (FindName("SettingsArgsTemplateTextBox") is not TextBox textBox) return;

            var template = textBox.Text.Trim();
            if (string.IsNullOrEmpty(template))
            {
                SetSettingsStatusMessage("Using default arguments for selected player");
            }
            else if (template.Contains("{url}"))
            {
                SetSettingsStatusMessage("Arguments template looks valid");
            }
            else
            {
                SetSettingsStatusMessage("Warning: Template should contain {url} token", true);
            }
        }

        private void SettingsTestPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("SettingsTestPlayerStatus") is not TextBlock status || FindName("SettingsPlayerExeTextBox") is not TextBox pathBox)
                return;

            status.Text = "Testing...";
            status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));

            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                status.Text = "❌ Invalid player path";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
                return;
            }

            try
            {
                var testUrl = "https://sample-videos.com/zip/10/mp4/SampleVideo_1280x720_1mb.mp4";
                var args = string.Empty;

                if (FindName("SettingsArgsTemplateTextBox") is TextBox argsBox)
                {
                    args = string.IsNullOrEmpty(argsBox.Text)
                        ? GetSettingsDefaultArgsForPlayer()
                        : argsBox.Text;
                }

                args = args.Replace("{url}", $"\"{testUrl}\"")
                          .Replace("{title}", "Test Video");

                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args,
                    UseShellExecute = false
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    status.Text = "✅ Player launched successfully";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
                }
                else
                {
                    status.Text = "❌ Failed to start player";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
                }
            }
            catch (Exception ex)
            {
                status.Text = $"❌ Error: {ex.Message}";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
        }

        private string GetSettingsDefaultArgsForPlayer()
        {
            return GetSettingsSelectedPlayerKind() switch
            {
                PlayerKind.VLC => "\"{url}\" --meta-title=\"{title}\"",
                PlayerKind.MPCHC => "\"{url}\" /play",
                PlayerKind.MPV => "--force-media-title=\"{title}\" \"{url}\"",
                _ => "{url}"
            };
        }

        private void SettingsBrowsePlayer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select player executable",
                    Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                    CheckFileExists = true
                };

                if (dlg.ShowDialog() == true)
                {
                    if (FindName("SettingsPlayerExeTextBox") is TextBox textBox)
                        textBox.Text = dlg.FileName;
                    SetSettingsStatusMessage("Player executable selected");
                }
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error selecting player: {ex.Message}", true);
            }
        }

        private void SettingsBrowseFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select ffmpeg executable",
                    Filter = "ffmpeg (ffmpeg.exe)|ffmpeg.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
                    CheckFileExists = true
                };

                if (dlg.ShowDialog() == true)
                {
                    if (FindName("SettingsFfmpegPathTextBox") is TextBox textBox)
                        textBox.Text = dlg.FileName;
                    SetSettingsStatusMessage("FFmpeg executable selected");
                }
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error selecting FFmpeg: {ex.Message}", true);
            }
        }

        private void SettingsBrowseRecordingDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Select recording directory"
                };

                if (FindName("SettingsRecordingDirTextBox") is TextBox textBox)
                {
                    var currentPath = textBox.Text.Trim();
                    if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
                    {
                        dialog.InitialDirectory = currentPath;
                    }

                    if (dialog.ShowDialog() == true)
                    {
                        textBox.Text = dialog.FolderName;
                        SetSettingsStatusMessage("Recording directory selected");
                    }
                }
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error selecting directory: {ex.Message}", true);
            }
        }

        private void SettingsPlayerKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized) return;

            var kind = GetSettingsSelectedPlayerKind();

            if (FindName("SettingsArgsTemplateTextBox") is TextBox argsBox)
            {
                var currentArgs = argsBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(currentArgs) ||
                    currentArgs == "{url}" ||
                    currentArgs.Contains("meta-title") ||
                    currentArgs.Contains("force-media-title") ||
                    currentArgs.Contains("/play"))
                {
                    argsBox.Text = GetSettingsDefaultArgsForPlayer();
                }
            }

            if (FindName("SettingsPlayerExeTextBox") is TextBox playerBox && string.IsNullOrEmpty(playerBox.Text.Trim()))
            {
                var detectedPath = DetectSettingsPlayerPath(kind);
                if (!string.IsNullOrEmpty(detectedPath))
                {
                    playerBox.Text = detectedPath;
                    SetSettingsStatusMessage($"Auto-detected {kind} player");
                }
            }

            ValidateAllSettingsFields();
        }

        private PlayerKind GetSettingsSelectedPlayerKind()
        {
            if (FindName("SettingsPlayerKindCombo") is ComboBox combo &&
                combo.SelectedItem is ComboBoxItem cbi &&
                cbi.Tag is string tag &&
                Enum.TryParse<PlayerKind>(tag, out var val))
                return val;
            return PlayerKind.VLC;
        }

        private void SettingsSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var isValid = true;
                var errorMessages = new List<string>();

                if (FindName("SettingsEpgIntervalTextBox") is TextBox epgBox)
                {
                    if (!int.TryParse(epgBox.Text.Trim(), out var minutes) || minutes < 5 || minutes > 720)
                    {
                        errorMessages.Add("EPG refresh interval must be between 5 and 720 minutes");
                        isValid = false;
                    }
                    else
                    {
                        Session.EpgRefreshInterval = TimeSpan.FromMinutes(minutes);
                    }
                }

                if (FindName("SettingsPlayerExeTextBox") is TextBox playerBox)
                {
                    var playerPath = playerBox.Text.Trim();
                    if (!string.IsNullOrEmpty(playerPath) && !File.Exists(playerPath))
                    {
                        errorMessages.Add("Player executable path is invalid");
                        isValid = false;
                    }
                    else
                    {
                        Session.PlayerExePath = string.IsNullOrWhiteSpace(playerPath) ? null : playerPath;
                    }
                }

                if (FindName("SettingsFfmpegPathTextBox") is TextBox ffmpegBox)
                {
                    var ffmpegPath = ffmpegBox.Text.Trim();
                    if (!string.IsNullOrEmpty(ffmpegPath) && !File.Exists(ffmpegPath))
                    {
                        errorMessages.Add("FFmpeg executable path is invalid");
                        isValid = false;
                    }
                    else
                    {
                        Session.FfmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath;
                    }
                }

                if (!isValid)
                {
                    var message = "Please fix the following issues:\n\n" + string.Join("\n", errorMessages);
                    MessageBox.Show(message, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Session.PreferredPlayer = GetSettingsSelectedPlayerKind();

                if (FindName("SettingsArgsTemplateTextBox") is TextBox argsBox)
                {
                    Session.PlayerArgsTemplate = string.IsNullOrWhiteSpace(argsBox.Text)
                        ? string.Empty
                        : argsBox.Text.Trim();
                }

                if (FindName("SettingsRecordingDirTextBox") is TextBox recordingBox)
                {
                    Session.RecordingDirectory = string.IsNullOrWhiteSpace(recordingBox.Text)
                        ? null
                        : recordingBox.Text.Trim();
                }

                if (FindName("SettingsFfmpegArgsTextBox") is TextBox ffmpegArgsBox)
                {
                    Session.FfmpegArgsTemplate = string.IsNullOrWhiteSpace(ffmpegArgsBox.Text)
                        ? Session.FfmpegArgsTemplate
                        : ffmpegArgsBox.Text.Trim();
                }

                SettingsStore.SaveFromSession();
                SetSettingsStatusMessage("Settings saved successfully!");
                MessageBox.Show("Settings saved successfully!", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error saving settings: {ex.Message}", true);
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SettingsUpdateEpgNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Session.RaiseEpgRefreshRequested();
                if (FindName("SettingsLastEpgUpdateTextBox") is TextBox lastEpgBox)
                {
                    lastEpgBox.Text = Session.LastEpgUpdateUtc.HasValue
                        ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g")
                        : "(never)";
                }
                SetSettingsStatusMessage("EPG refresh requested");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error requesting EPG refresh: {ex.Message}", true);
            }
        }

        private void SettingsRestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("This will reset all settings to their default values. Continue?",
                "Restore Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                if (FindName("SettingsPlayerKindCombo") is ComboBox playerCombo)
                    playerCombo.SelectedIndex = 0;

                if (FindName("SettingsPlayerExeTextBox") is TextBox playerBox)
                    playerBox.Text = string.Empty;

                if (FindName("SettingsArgsTemplateTextBox") is TextBox argsBox)
                    argsBox.Text = "\"{url}\" --meta-title=\"{title}\"";

                if (FindName("SettingsFfmpegPathTextBox") is TextBox ffmpegBox)
                    ffmpegBox.Text = string.Empty;

                if (FindName("SettingsRecordingDirTextBox") is TextBox recordingBox)
                    recordingBox.Text = string.Empty;

                if (FindName("SettingsFfmpegArgsTextBox") is TextBox ffmpegArgsBox)
                    ffmpegArgsBox.Text = "-i \"{url}\" -c copy -f mpegts \"{output}\"";

                if (FindName("SettingsEpgIntervalTextBox") is TextBox epgBox)
                    epgBox.Text = "30";

                ValidateAllSettingsFields();
                SetSettingsStatusMessage("Settings restored to defaults (not saved yet)");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error restoring defaults: {ex.Message}", true);
            }
        }

        // Log control event handlers
        private void SettingsEnableLogging_Changed(object sender, RoutedEventArgs e)
        {
            if (FindName("RawOutputLogTextBlock") is TextBlock logTextBlock)
            {
                if (FindName("SettingsEnableLoggingCheckBox") is CheckBox checkBox && checkBox.IsChecked == true)
                {
                    if (logTextBlock.Text == "Raw output log will appear here when logging is enabled...")
                    {
                        logTextBlock.Text = $"[{DateTime.Now:HH:mm:ss.fff}] Logging enabled.\n";
                    }
                    else
                    {
                        Log("Logging enabled.\n");
                    }
                }
                else
                {
                    Log("Logging disabled.\n");
                }
            }
        }

        private void SettingsClearLog_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("RawOutputLogTextBlock") is TextBlock logTextBlock)
            {
                logTextBlock.Text = $"[{DateTime.Now:HH:mm:ss.fff}] Log cleared.\n";
            }
        }

        private void SettingsCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (FindName("RawOutputLogTextBlock") is TextBlock logTextBlock)
                {
                    Clipboard.SetText(logTextBlock.Text);
                    Log("Log copied to clipboard.\n");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy log to clipboard: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SettingsSaveLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (FindName("RawOutputLogTextBlock") is TextBlock logTextBlock)
                {
                    var saveDialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
                        DefaultExt = ".txt",
                        FileName = $"iptv-log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt"
                    };

                    if (saveDialog.ShowDialog() == true)
                    {
                        System.IO.File.WriteAllText(saveDialog.FileName, logTextBlock.Text);
                        Log($"Log saved to: {saveDialog.FileName}\n");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save log: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SettingsCachingEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (SettingsCachingEnabledCheckBox.IsChecked.HasValue)
            {
                Session.CachingEnabled = SettingsCachingEnabledCheckBox.IsChecked.Value;
                Log($"Disk caching {(Session.CachingEnabled ? "enabled" : "disabled")} (in-memory caching always active)\n");
            }
        }

        private async void SettingsClearCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("Clear all cached data and images? This will remove all cached EPG data, VOD content, and images.",
                    "Confirm Clear Cache", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    var button = sender as Button;
                    if (button != null)
                    {
                        button.IsEnabled = false;
                        button.Content = "🧹 Clearing...";
                    }

                    _cacheService.ClearImageCache();
                    await _cacheService.ClearAllDataAsync();

                    Log("All cache cleared successfully\n");
                    MessageBox.Show("Cache cleared successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    if (button != null)
                    {
                        button.IsEnabled = true;
                        button.Content = "Clear All Cache";
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to clear cache: {ex.Message}\n");
                MessageBox.Show($"Failed to clear cache: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SettingsCacheInspector_Click(object sender, RoutedEventArgs e)
        {
            OpenCacheInspector(sender, e);
        }

        private void SettingsOpenCredentialsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var credentialsFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "IPTV-Desktop-Browser");

                // Create the folder if it doesn't exist
                if (!Directory.Exists(credentialsFolder))
                {
                    Directory.CreateDirectory(credentialsFolder);
                }

                // Open the folder in Windows Explorer
                Process.Start(new ProcessStartInfo
                {
                    FileName = credentialsFolder,
                    UseShellExecute = true,
                    Verb = "open"
                });

                SetSettingsStatusMessage("Credentials folder opened in Explorer");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error opening folder: {ex.Message}", true);
                MessageBox.Show(this, $"Failed to open credentials folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowLoadingOverlay(string overlayName)
        {
            Dispatcher.Invoke(() =>
            {
                if (FindName(overlayName) is Grid overlay)
                {
                    overlay.Visibility = Visibility.Visible;
                }
            });
        }

        private void HideLoadingOverlay(string overlayName)
        {
            Dispatcher.Invoke(() =>
            {
                if (FindName(overlayName) is Grid overlay)
                {
                    overlay.Visibility = Visibility.Collapsed;
                }
            });
        }


        // View mode event handlers
        private void ChannelsGridView_Click(object sender, RoutedEventArgs e)
        {
            ChannelsViewMode = ViewMode.Grid;
            UpdateChannelsViewButtons();
            UpdateChannelsViewVisibility();
        }

        private void ChannelsListView_Click(object sender, RoutedEventArgs e)
        {
            ChannelsViewMode = ViewMode.List;
            UpdateChannelsViewButtons();
            UpdateChannelsViewVisibility();
        }

        private void VodGridView_Click(object sender, RoutedEventArgs e)
        {
            VodViewMode = ViewMode.Grid;
            UpdateVodViewButtons();
            UpdateVodViewVisibility();
        }

        private void VodListView_Click(object sender, RoutedEventArgs e)
        {
            VodViewMode = ViewMode.List;
            UpdateVodViewButtons();
            UpdateVodViewVisibility();
        }


        private void UpdateChannelsViewButtons()
        {
            if (FindName("ChannelsGridViewBtn") is Button gridBtn)
                gridBtn.Background = IsChannelsGridView ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223247")) : Brushes.Transparent;
            if (FindName("ChannelsListViewBtn") is Button listBtn)
                listBtn.Background = IsChannelsListView ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223247")) : Brushes.Transparent;
        }

        private void UpdateVodViewButtons()
        {
            if (FindName("VodGridViewBtn") is Button gridBtn)
                gridBtn.Background = IsVodGridView ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223247")) : Brushes.Transparent;
            if (FindName("VodListViewBtn") is Button listBtn)
                listBtn.Background = IsVodListView ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223247")) : Brushes.Transparent;
        }

        private void UpdateChannelsViewVisibility()
        {
            if (FindName("ChannelsGridScrollViewer") is ScrollViewer gridScrollViewer)
                gridScrollViewer.Visibility = IsChannelsGridView ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("ChannelsListScrollViewer") is ScrollViewer listScrollViewer)
                listScrollViewer.Visibility = IsChannelsListView ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateVodViewVisibility()
        {
            // Update Movies view visibility
            if (FindName("MoviesGridScrollViewer") is ScrollViewer moviesGridScrollViewer)
                moviesGridScrollViewer.Visibility = IsVodGridView ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("MoviesListScrollViewer") is ScrollViewer moviesListScrollViewer)
                moviesListScrollViewer.Visibility = IsVodListView ? Visibility.Visible : Visibility.Collapsed;

            // Update Series view visibility
            if (FindName("SeriesGridScrollViewer") is ScrollViewer seriesGridScrollViewer)
                seriesGridScrollViewer.Visibility = IsVodGridView ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("SeriesListScrollViewer") is ScrollViewer seriesListScrollViewer)
                seriesListScrollViewer.Visibility = IsVodListView ? Visibility.Visible : Visibility.Collapsed;
        }


        private void TileSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingTileSize) return;

            if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                CurrentTileSize = tag switch
                {
                    "Small" => TileSize.Small,
                    "Large" => TileSize.Large,
                    _ => TileSize.Medium
                };

                // Keep both ComboBoxes in sync
                SetTileSizeSelection(CurrentTileSize);
            }
        }

        private void SetTileSizeSelection(TileSize tileSize)
        {
            _updatingTileSize = true;

            string targetTag = tileSize switch
            {
                TileSize.Small => "Small",
                TileSize.Large => "Large",
                _ => "Medium"
            };

            // Update both ComboBoxes
            if (FindName("TileSizeCombo") is ComboBox tileSizeCombo)
            {
                foreach (ComboBoxItem item in tileSizeCombo.Items)
                {
                    if (item.Tag?.ToString() == targetTag)
                    {
                        tileSizeCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            if (FindName("VodTileSizeCombo") is ComboBox vodTileSizeCombo)
            {
                foreach (ComboBoxItem item in vodTileSizeCombo.Items)
                {
                    if (item.Tag?.ToString() == targetTag)
                    {
                        vodTileSizeCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            _updatingTileSize = false;
        }


        private void DefaultViewMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var newMode = tag == "List" ? ViewMode.List : ViewMode.Grid;
                ChannelsViewMode = newMode;
                VodViewMode = newMode;
                UpdateChannelsViewButtons();
                UpdateVodViewButtons();
            }
        }

        private void DashboardWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Update tile sizes when window size changes for responsive design
            OnPropertyChanged(nameof(TileWidth));
            OnPropertyChanged(nameof(TileHeight));
            OnPropertyChanged(nameof(VodTileHeight));
        }

        public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
