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
using System.Windows.Input;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using Microsoft.Win32;

namespace DesktopApp.Views
{
    public partial class DashboardWindow : Window, INotifyPropertyChanged
    {
        public DashboardNavigationViewModel Navigation { get; } = new();
        public LiveTvPageViewModel LiveTv { get; } = new();
        public MoviesSeriesPageViewModel Catalog { get; }
        public SchedulerPageViewModel SchedulerPageModel { get; }
        public SettingsPageViewModel SettingsPageModel { get; }

        private RecordingStatusWindow? _recordingWindow;
        // NOTE: Duplicate recording fields and OnClosed removed. This is the consolidated file.
        // Collections / state
        private readonly HttpClient _http = new();
        private readonly IChannelService _channelService;
        private readonly IVodService _vodService;
        private readonly ICacheService _cacheService;
        private BulkObservableCollection<Category> _categories => LiveTv.Categories; public ObservableCollection<Category> Categories => _categories;
        private BulkObservableCollection<Channel> _channels => LiveTv.Channels; public ObservableCollection<Channel> Channels => _channels;
        private BulkObservableCollection<EpgEntry> _upcomingEntries => LiveTv.UpcomingPrograms; public ObservableCollection<EpgEntry> UpcomingEntries => _upcomingEntries;

        // VOD collections
        private BulkObservableCollection<VodCategory> _vodCategories => Catalog.MovieCategories; public ObservableCollection<VodCategory> VodCategories => _vodCategories;
        private BulkObservableCollection<VodContent> _vodContent => Catalog.Movies; public ObservableCollection<VodContent> VodContent => _vodContent;
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

        private bool _isLoadingVodContent { get => Catalog.IsLoadingMovies; set => Catalog.IsLoadingMovies = value; }
        public bool IsLoadingVodContent
        {
            get => _isLoadingVodContent;
            set
            {
                if (value != _isLoadingVodContent)
                {
                    _isLoadingVodContent = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isLoadingSeriesContent { get => Catalog.IsLoadingSeries; set => Catalog.IsLoadingSeries = value; }
        public bool IsLoadingSeriesContent
        {
            get => _isLoadingSeriesContent;
            set
            {
                if (value != _isLoadingSeriesContent)
                {
                    _isLoadingSeriesContent = value;
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

        // Expose RecordingManager singleton for XAML binding
        public RecordingManager RecordingManager => RecordingManager.Instance;

        // Search
        private CancellationTokenSource? _searchDebounceCts;
        private static readonly TimeSpan GlobalSearchDebounce = TimeSpan.FromSeconds(3);
        private string _searchQuery { get => LiveTv.SearchQuery; set => LiveTv.SearchQuery = value; }
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
        private bool _searchAllChannels { get => LiveTv.SearchAllChannels; set => LiveTv.SearchAllChannels = value; }
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

        private string _selectedVodCategoryId { get => Catalog.SelectedMovieCategoryId; set => Catalog.SelectedMovieCategoryId = value; }
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

        public VodContent? SelectedVodContent => Catalog.SelectedMovie;

        // Series collections
        private BulkObservableCollection<SeriesCategory> _seriesCategories => Catalog.SeriesCategories; public ObservableCollection<SeriesCategory> SeriesCategories => _seriesCategories;
        private BulkObservableCollection<SeriesContent> _seriesContent => Catalog.Series; public ObservableCollection<SeriesContent> SeriesContent => _seriesContent;

        private string _selectedSeriesCategoryId { get => Catalog.SelectedSeriesCategoryId; set => Catalog.SelectedSeriesCategoryId = value; }
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

        public SeriesContent? SelectedSeriesContent => Catalog.SelectedSeries;

        private Channel? _selectedChannel { get => LiveTv.SelectedChannel; set => LiveTv.SelectedChannel = value; }
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
                        RefreshCatalogResources();
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
                    RefreshCatalogResources();
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
        private LatestRequestLoader _categoryLoader => LiveTv.CategoryRequests;
        private LatestRequestLoader _globalSearchLoader => LiveTv.SearchRequests;
        private LatestRequestLoader _vodCategoryLoader => Catalog.MovieRequests;
        private LatestRequestLoader _seriesCategoryLoader => Catalog.SeriesRequests;
        private bool IsSeriesCatalog => Catalog.ContentType == CatalogContentType.Series;

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
            SchedulerPageModel = new SchedulerPageViewModel(
                new RecordingFormViewModel(new RecordingScheduleService(_channelService, _scheduler),
                    new Dashboard.RecordingFormInteraction(() => this)),
                new RecordingManagementViewModel(new RecordingManagementService(_scheduler),
                    new Dashboard.RecordingManagementInteraction(() => this)));
            Catalog = new MoviesSeriesPageViewModel(_vodService);
            Catalog.Details.MoviePlaybackRequested += TryLaunchVodInPlayer;
            Catalog.Details.EpisodePlaybackRequested += TryLaunchEpisodeInPlayer;
            Catalog.Details.LoadFailed += error => Log($"ERROR loading details: {error.Message}\n");
            SettingsPageModel = new SettingsPageViewModel(new ApplicationSettingsService(),
                new Dashboard.SettingsInteraction(() => this), _cacheService);

            // Set up raw output logging for cache status
            _channelService.SetRawOutputLogger(Log);

            InitializeComponent();
            DataContext = this;
            Navigation.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Navigation.ActivePage)) ApplyActivePage();
            };
            Catalog.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(Catalog.SelectedMovie) or nameof(Catalog.SelectedSeries))
                {
                    OnPropertyChanged(nameof(SelectedVodContent));
                    OnPropertyChanged(nameof(SelectedSeriesContent));
                    ScheduleCatalogRefresh();
                    return;
                }
                if (e.PropertyName != nameof(Catalog.ContentType)) return;
                CancelVodRequests();
                ApplyCatalogView();
            };
            // User name display removed in new layout

            // Subscribe to favorites changes
            Session.FavoritesChanged += OnFavoritesChanged;

            // Subscribe to EPG refresh for series recordings
            _scheduler.EpgRefreshNeeded += OnEpgRefreshNeeded;
            _scheduler.RecordingFailed += OnScheduledRecordingFailed;

            CategoriesCollectionView = LiveTv.CategoriesView;
            ChannelsCollectionView = LiveTv.ChannelsView;
            VodContentCollectionView = Catalog.MoviesView;
            VodCategoriesCollectionView = Catalog.MovieCategoriesView;
            SeriesContentCollectionView = Catalog.SeriesView;
            SeriesCategoriesCollectionView = Catalog.SeriesCategoriesView;
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
                try
                {
                    // Always show UI first, then load data in background
                    UpdateViewVisibility();
                    UpdateNavButtons();

                    Session.EpgRefreshRequested += OnEpgRefreshRequested;

                    // Reload series recordings for the current account/session
                    _scheduler.ReloadForCurrentSession();

                    if (Session.Mode == SessionMode.Xtream)
                    {
                        _nextScheduledEpgRefreshUtc = DateTime.UtcNow + Session.EpgRefreshInterval;
                        _ = RunEpgSchedulerLoopAsync();
                        await LoadCategoriesAsync();
                        // VOD and Series categories will be loaded on-demand when user navigates to those sections
                    }
                    else
                    {
                        await LoadCategoriesFromPlaylistAsync();
                        await BuildPlaylistAllChannelsIndexAsync();
                    }
                }
                catch (Exception ex)
                {
                    Log($"ERROR during window load: {ex.Message}\n{ex.StackTrace}");
                    // Ensure UI is visible even if loading fails
                    UpdateViewVisibility();
                    UpdateNavButtons();
                }
            };

            // Subscribe to recording manager events for channel indicators
            RecordingManager.Instance.PropertyChanged += OnRecordingManagerChanged;
            InitializeCatalogLoading();
        }

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
            try { ProfileRawJson = DesktopApp.Security.DiagnosticRedactor.Redact(JsonSerializer.Serialize(ui, new JsonSerializerOptions { WriteIndented = true })); } catch { }
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
                    RefreshVisibleNowPlaying();
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
        private void RefreshVisibleNowPlaying()
        {
            var now = DateTime.UtcNow;
            foreach (var channel in ReadCatalogViewport().Select(i => i.Item).OfType<Channel>())
                channel.RefreshCurrentProgram(now);
            RefreshCatalogResources();
        }
        private void UpdateSelectedNowPlaying()
        {
            NowProgramText = string.IsNullOrWhiteSpace(SelectedChannel?.NowTitle)
                ? string.Empty : $"Now: {SelectedChannel.NowTitle} ({SelectedChannel.NowTimeRange})";
        }

        private void OnEpgRefreshRequested()
        {
            if (_cts.IsCancellationRequested || Session.Mode != SessionMode.Xtream) return;
            Dispatcher.InvokeAsync(() =>
            {
                LastEpgUpdateText = Session.LastEpgUpdateUtc.HasValue ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g") : "(never)";
                Log("EPG refresh triggered\n");
                RefreshVisibleNowPlaying();
            });
        }

        private static string TryGetString(JsonElement el, params string[] names)
        { foreach (var n in names) if (el.TryGetProperty(n, out var p)) { if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? string.Empty; if (p.ValueKind == JsonValueKind.Number) return p.ToString(); } return string.Empty; }
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
            text = DesktopApp.Security.DiagnosticRedactor.Redact(text, Session.Username, Session.Password);
            try
            {
                if (_isClosing)
                    return;

                // Quick check if logging is enabled to avoid UI work
                bool loggingEnabled = true; // Default to enabled during startup
                try
                {
                    if (FindDashboardElement("SettingsEnableLoggingCheckBox") is CheckBox enableLoggingCheckBox)
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
                        if (FindDashboardElement("RawOutputLogTextBlock") is TextBlock logTextBlock)
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
                            if (FindDashboardElement("LogScrollViewer") is ScrollViewer scrollViewer)
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

        internal void ExportFavorites_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var favorites = Session.GetFavoriteChannels();
                if (favorites == null || favorites.Count == 0)
                {
                    MessageBox.Show(this, "You have no favorites to export.", "Export Favorites", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var saveDialog = new SaveFileDialog
                {
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    DefaultExt = "json",
                    FileName = $"favorites_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                    Title = "Export Favorites"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    if (Session.ExportFavorites(saveDialog.FileName))
                    {
                        Log($"Exported {favorites.Count} favorite(s) to {saveDialog.FileName}\n");
                        MessageBox.Show(this, $"Successfully exported {favorites.Count} favorite(s) to:\n{saveDialog.FileName}", "Export Favorites", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Log("Failed to export favorites\n");
                        MessageBox.Show(this, "Failed to export favorites. Please try again.", "Export Favorites", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error exporting favorites: {ex.Message}\n");
                MessageBox.Show(this, $"Error exporting favorites:\n{ex.Message}", "Export Favorites", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void ImportFavorites_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    DefaultExt = "json",
                    Title = "Import Favorites"
                };

                if (openDialog.ShowDialog() == true)
                {
                    var result = Session.ImportFavorites(openDialog.FileName);

                    if (result > 0)
                    {
                        Log($"Imported {result} new favorite(s) from {openDialog.FileName}\n");
                        MessageBox.Show(this, $"Successfully imported {result} new favorite(s).\n\nNote: Favorites with duplicate IDs were skipped.", "Import Favorites", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (result == 0)
                    {
                        MessageBox.Show(this, "No new favorites were imported. All favorites from the file already exist or the file was empty.", "Import Favorites", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Log("Failed to import favorites\n");
                        MessageBox.Show(this, "Failed to import favorites. Please check the file format and try again.", "Import Favorites", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error importing favorites: {ex.Message}\n");
                MessageBox.Show(this, $"Error importing favorites:\n{ex.Message}", "Import Favorites", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // modify existing OnClosed (search and replace previous implementation) - keep rest of file intact
        protected override void OnClosed(EventArgs e)
        {
            SchedulerPageModel.Dispose();
            _globalSearchLoader.Cancel();
            CancelVodRequests();
            StopCatalogLoading();
            _scheduler.RecordingFailed -= OnScheduledRecordingFailed;
            _scheduler.EpgRefreshNeeded -= OnEpgRefreshNeeded;
            try { StopRecording(); } catch { }
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


        internal void ChannelTile_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Channel ch)
                ScheduleCatalogRefresh();
        }

        private void ChannelTile_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Currently no specific action needed on mouse leave
        }

        internal void ChannelFavoriteButton_Click(object sender, RoutedEventArgs e)
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

        internal void ChannelRecordButton_Click(object sender, RoutedEventArgs e)
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
        internal void ChannelTile_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
        // API test output (disabled in M3U mode)
        private async void LoadStreams_Click(object sender, RoutedEventArgs e) => await RunApiCall("get_live_streams");
        private async Task RunApiCall(string action)
        { if (Session.Mode != SessionMode.Xtream) { Log("API calls disabled in M3U mode.\n"); return; } try { var url = Session.BuildApi(action); Log($"GET {url}\n"); var json = await _http.GetStringAsync(url, _cts.Token); if (json.Length > 50_000) json = json[..50_000] + "...<truncated>"; Log(json + "\n\n"); } catch (OperationCanceledException) { } catch (Exception ex) { Log("ERROR: " + ex.Message + "\n"); } }

        internal void OpenRecordingFolder_Click(object sender, RoutedEventArgs e) =>
            SchedulerPageModel.Recordings.OpenFolderCommand.Execute(null);
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
                if (FindDashboardElement("RecordBtnText") is TextBlock btnText) btnText.Text = "Stop Recording";
                if (FindDashboardElement("RecordBtnIcon") is TextBlock btnIcon) btnIcon.Text = "⏹️";
                if (FindDashboardElement("RecordButton") is Button btn) btn.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45)); // Red color

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
            if (FindDashboardElement("RecordBtnText") is TextBlock btnText) btnText.Text = "Start Recording";
            if (FindDashboardElement("RecordBtnIcon") is TextBlock btnIcon) btnIcon.Text = "⏺️";
            if (FindDashboardElement("RecordButton") is Button btn) btn.Background = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45)); // Green color

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
            ShowPage(DashboardPage.LiveTv);
            SetSelectedNavButton(sender as Button);
        }

        private void NavigateToFavorites(object sender, RoutedEventArgs e)
        {
            ShowPage(DashboardPage.Favorites);
            SetSelectedNavButton(sender as Button);
            LoadFavoritesPage();
        }

        private async void NavigateToVod(object sender, RoutedEventArgs e)
        {
            ShowPage(DashboardPage.Vod);
            SetSelectedNavButton(sender as Button);

            // Load VOD categories if not already loaded
            if (_vodCategories.Count == 0 && Session.Mode == SessionMode.Xtream)
            {
                await LoadVodCategoriesAsync();
                await LoadSeriesCategoriesAsync();
            }

            // Leaving the page cancels pending catalogs. Resume a still-selected category
            // when returning, even if the picker did not raise another selection event.
            if (_isClosing || _cts.IsCancellationRequested || Navigation.ActivePage != DashboardPage.Vod) return;
            if (IsSeriesCatalog && !IsLoadingSeriesContent && _seriesContent.Count == 0 && !string.IsNullOrEmpty(SelectedSeriesCategoryId))
                await LoadSeriesContentAsync(SelectedSeriesCategoryId);
            else if (!IsSeriesCatalog && !IsLoadingVodContent && _vodContent.Count == 0 && !string.IsNullOrEmpty(SelectedVodCategoryId))
                await LoadVodContentAsync(SelectedVodCategoryId);
        }

        private void NavigateToRecording(object sender, RoutedEventArgs e)
        {
            ShowPage(DashboardPage.Recording);
            SetSelectedNavButton(sender as Button);
            UpdateRecordingPageDisplay();
        }

        private void NavigateToScheduler(object sender, RoutedEventArgs e)
        {
            ShowPage(DashboardPage.Scheduler);

            // Always highlight the correct nav button regardless of which button triggered this
            if (FindDashboardElement("SchedulerNavButton") is Button schedulerNavBtn)
            {
                SetSelectedNavButton(schedulerNavBtn);
            }

        }

        private void NavigateToProfile(object sender, RoutedEventArgs e)
        {
            ShowPage(DashboardPage.Profile);
            SetSelectedNavButton(sender as Button);
            LoadProfileData();
        }

        private void NavigateToSettings(object sender, RoutedEventArgs e)
        {
            ShowPage(DashboardPage.Settings);

            // Always highlight the correct nav button regardless of which button triggered this
            if (FindDashboardElement("SettingsNavButton") is Button settingsNavBtn)
            {
                SetSelectedNavButton(settingsNavBtn);
            }

        }

        private void NavigateToLogs(object sender, RoutedEventArgs e)
        {
            ShowPage(DashboardPage.Logs);
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

        private void ShowPage(DashboardPage page)
        {
            if (Navigation.ActivePage == page) ApplyActivePage();
            else Navigation.ActivePage = page;
        }

        private void ApplyActivePage()
        {
            var page = Navigation.ActivePage;
            if (page == DashboardPage.Scheduler) InitializeScheduler();
            else SchedulerPageModel.NewRecording.Deactivate();
            if (page != DashboardPage.Vod) CancelVodRequests();

            // Hide all pages
            if (FindDashboardElement("LiveTvPage") is FrameworkElement liveTvPage) liveTvPage.Visibility = Visibility.Collapsed;
            if (FindDashboardElement("FavoritesPage") is FrameworkElement favoritesPage) favoritesPage.Visibility = Visibility.Collapsed;
            if (FindDashboardElement("VodPage") is FrameworkElement vodPage) vodPage.Visibility = Visibility.Collapsed;
            if (FindDashboardElement("RecordingPage") is FrameworkElement recordingPage) recordingPage.Visibility = Visibility.Collapsed;
            if (FindDashboardElement("SchedulerPage") is FrameworkElement schedulerPage) schedulerPage.Visibility = Visibility.Collapsed;
            if (FindDashboardElement("ProfilePage") is FrameworkElement profilePage) profilePage.Visibility = Visibility.Collapsed;
            if (FindDashboardElement("SettingsPage") is FrameworkElement settingsPage) settingsPage.Visibility = Visibility.Collapsed;
            if (FindDashboardElement("LogsPage") is FrameworkElement logsPage) logsPage.Visibility = Visibility.Collapsed;

            SetSelectedNavButton(FindDashboardElement($"{page}NavButton") as Button);
            // Show selected page
            if (FindDashboardElement($"{page}Page") is FrameworkElement targetPage)
                targetPage.Visibility = Visibility.Visible;
            if (page == DashboardPage.Settings) SettingsPageModel.Load();
            RefreshCatalogResources();
        }

        private void SetSelectedNavButton(Button? selectedButton)
        {
            // Clear all nav button selections
            if (FindDashboardElement("LiveTvNavButton") is Button liveTvBtn) liveTvBtn.Tag = null;
            if (FindDashboardElement("FavoritesNavButton") is Button favoritesBtn) favoritesBtn.Tag = null;
            if (FindDashboardElement("VodNavButton") is Button vodBtn) vodBtn.Tag = null;
            if (FindDashboardElement("RecordingNavButton") is Button recordingBtn) recordingBtn.Tag = null;
            if (FindDashboardElement("SchedulerNavButton") is Button schedulerBtn) schedulerBtn.Tag = null;
            if (FindDashboardElement("ProfileNavButton") is Button profileBtn) profileBtn.Tag = null;
            if (FindDashboardElement("SettingsNavButton") is Button settingsBtn) settingsBtn.Tag = null;
            if (FindDashboardElement("LogsNavButton") is Button logsBtn) logsBtn.Tag = null;

            // Set selected button
            if (selectedButton != null)
                selectedButton.Tag = "Selected";
        }

        private void UpdateRecordingPageDisplay()
        {
            if (FindDashboardElement("SelectedChannelText") is TextBlock channelText)
            {
                channelText.Text = SelectedChannel?.Name ?? "None selected";
            }

            // Update recording status indicators
            bool isRecording = _recordProcess != null;

            if (FindDashboardElement("RecordingPageStatusIndicator") is System.Windows.Shapes.Ellipse statusIndicator)
            {
                statusIndicator.Fill = isRecording
                    ? new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45)) // Red for recording
                    : new SolidColorBrush(Color.FromRgb(0x6C, 0x75, 0x7D)); // Gray for idle
            }

            if (FindDashboardElement("RecordingStatusDisplay") is TextBlock statusText)
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
            if (FindDashboardElement("RecordingStatusBadge") is Border statusBadge && FindDashboardElement("RecordingStatusBadgeText") is TextBlock badgeText)
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
            if (FindDashboardElement("RecordingDetailsPanel") is StackPanel detailsPanel && FindDashboardElement("RecordingIdlePanel") is StackPanel idlePanel)
            {
                if (recordingManager.IsRecording)
                {
                    detailsPanel.Visibility = Visibility.Visible;
                    idlePanel.Visibility = Visibility.Collapsed;

                    // Update recording details
                    if (FindDashboardElement("RecordingChannelText") is TextBlock channelTextBlock)
                        channelTextBlock.Text = recordingManager.ChannelName ?? "Unknown";

                    if (FindDashboardElement("RecordingStartedText") is TextBlock startedTextBlock)
                        startedTextBlock.Text = recordingManager.StartedDisplay;

                    if (FindDashboardElement("RecordingFileText") is TextBlock fileTextBlock)
                        fileTextBlock.Text = recordingManager.FileName ?? "--";

                    if (FindDashboardElement("RecordingPathText") is TextBlock pathTextBlock)
                        pathTextBlock.Text = recordingManager.FilePath ?? "--";

                    if (FindDashboardElement("RecordingDurationText") is TextBlock durationTextBlock)
                        durationTextBlock.Text = recordingManager.DurationDisplay;

                    if (FindDashboardElement("RecordingSizeText") is TextBlock sizeTextBlock)
                        sizeTextBlock.Text = recordingManager.SizeDisplay;

                    if (FindDashboardElement("RecordingBitrateText") is TextBlock bitrateTextBlock)
                        bitrateTextBlock.Text = recordingManager.BitrateDisplay;
                }
                else
                {
                    detailsPanel.Visibility = Visibility.Collapsed;
                    idlePanel.Visibility = Visibility.Visible;
                }
            }

            // Update output directory display
            if (FindDashboardElement("RecordingOutputDirectoryText") is TextBlock outputDirText)
            {
                string actualDirectory = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                outputDirText.Text = actualDirectory;
            }
        }

        internal async void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is Category category)
            {
                await LoadChannelsForCategoryAsync(category);
            }
            else
            {
                CancelCategoryLoad();
                _channels.Clear();
                ChannelsCountText = "0 channels";
                SelectedChannel = null;
            }
        }


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
                            EpgSchedule = existingChannel.EpgSchedule,
                            NextEpgRefreshUtc = existingChannel.NextEpgRefreshUtc,
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
                if (FindDashboardElement("FavoritesChannelsControl") is ItemsControl favoritesControl)
                {
                    favoritesControl.ItemsSource = channels;
                }

                // Update count and visibility
                UpdateFavoritesDisplay(channels.Count);

                // Load logos for favorites that don't have them
                ScheduleCatalogRefresh();
            }
            catch (Exception ex)
            {
                Log($"Error loading favorites: {ex.Message}\n");
            }
        }

        private void UpdateFavoritesDisplay(int count)
        {
            if (FindDashboardElement("FavoritesCountLabel") is TextBlock countLabel)
            {
                countLabel.Text = count == 0 ? "No favorites" :
                                 count == 1 ? "1 favorite" :
                                 $"{count} favorites";
            }

            if (FindDashboardElement("NoFavoritesMessage") is Border noFavoritesMessage)
            {
                noFavoritesMessage.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (FindDashboardElement("FavoritesChannelsControl") is ItemsControl favoritesControl)
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
                if (Navigation.ActivePage == DashboardPage.Favorites)
                {
                    LoadFavoritesPage();
                }
            });
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
        private void LoadProfileData()
        {
            try
            {
                // Update account information
                if (FindDashboardElement("UsernameText") is TextBlock usernameText)
                    usernameText.Text = Session.Username ?? "Unknown";

                if (FindDashboardElement("StatusText") is TextBlock statusText)
                    statusText.Text = Session.UserInfo?.status ?? "Unknown";

                if (FindDashboardElement("IsTrialText") is TextBlock isTrialText)
                    isTrialText.Text = Session.UserInfo?.is_trial ?? "Unknown";

                if (FindDashboardElement("ExpiryDateText") is TextBlock expiryText)
                {
                    if (Session.UserInfo?.exp_date != null && long.TryParse(Session.UserInfo.exp_date, out var expTimestamp))
                    {
                        var expiry = DateTimeOffset.FromUnixTimeSeconds(expTimestamp).DateTime;
                        expiryText.Text = expiry.ToString("MM/dd/yyyy hh:mm tt");
                    }
                    else
                        expiryText.Text = "Never";
                }

                if (FindDashboardElement("MaxConnectionsText") is TextBlock maxConnText)
                    maxConnText.Text = Session.UserInfo?.max_connections ?? "Unknown";

                if (FindDashboardElement("ActiveConnectionsText") is TextBlock activeConnText)
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
        private void ShowLoadingOverlay(string overlayName)
        {
            Dispatcher.Invoke(() =>
            {
                if (FindDashboardElement(overlayName) is Grid overlay)
                {
                    overlay.Visibility = Visibility.Visible;
                }
            });
        }

        private void HideLoadingOverlay(string overlayName)
        {
            Dispatcher.Invoke(() =>
            {
                if (FindDashboardElement(overlayName) is Grid overlay)
                {
                    overlay.Visibility = Visibility.Collapsed;
                }
            });
        }


        // View mode event handlers
        internal void ChannelsGridView_Click(object sender, RoutedEventArgs e)
        {
            ChannelsViewMode = ViewMode.Grid;
            UpdateChannelsViewButtons();
            UpdateChannelsViewVisibility();
        }

        internal void ChannelsListView_Click(object sender, RoutedEventArgs e)
        {
            ChannelsViewMode = ViewMode.List;
            UpdateChannelsViewButtons();
            UpdateChannelsViewVisibility();
        }

        internal void VodGridView_Click(object sender, RoutedEventArgs e)
        {
            VodViewMode = ViewMode.Grid;
            UpdateVodViewButtons();
            UpdateVodViewVisibility();
        }

        internal void VodListView_Click(object sender, RoutedEventArgs e)
        {
            VodViewMode = ViewMode.List;
            UpdateVodViewButtons();
            UpdateVodViewVisibility();
        }


        private void UpdateChannelsViewButtons()
        {
            if (FindDashboardElement("ChannelsGridViewBtn") is Button gridBtn)
                gridBtn.Background = IsChannelsGridView ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223247")) : Brushes.Transparent;
            if (FindDashboardElement("ChannelsListViewBtn") is Button listBtn)
                listBtn.Background = IsChannelsListView ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223247")) : Brushes.Transparent;
        }

        private void UpdateVodViewButtons()
        {
            if (FindDashboardElement("VodGridViewBtn") is Button gridBtn)
                gridBtn.Background = IsVodGridView ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223247")) : Brushes.Transparent;
            if (FindDashboardElement("VodListViewBtn") is Button listBtn)
                listBtn.Background = IsVodListView ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223247")) : Brushes.Transparent;
        }

        private void UpdateChannelsViewVisibility()
        {
            if (FindDashboardElement("ChannelsGridView") is ItemsControl gridScrollViewer)
                gridScrollViewer.Visibility = IsChannelsGridView ? Visibility.Visible : Visibility.Collapsed;
            if (FindDashboardElement("ChannelsListView") is ItemsControl listScrollViewer)
                listScrollViewer.Visibility = IsChannelsListView ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateVodViewVisibility()
        {
            // Update Movies view visibility
            if (FindDashboardElement("MoviesGridView") is ItemsControl moviesGridScrollViewer)
                moviesGridScrollViewer.Visibility = !IsSeriesCatalog && IsVodGridView ? Visibility.Visible : Visibility.Collapsed;
            if (FindDashboardElement("MoviesListView") is ItemsControl moviesListScrollViewer)
                moviesListScrollViewer.Visibility = !IsSeriesCatalog && IsVodListView ? Visibility.Visible : Visibility.Collapsed;

            // Update Series view visibility
            if (FindDashboardElement("SeriesGridView") is ItemsControl seriesGridScrollViewer)
                seriesGridScrollViewer.Visibility = IsSeriesCatalog && IsVodGridView ? Visibility.Visible : Visibility.Collapsed;
            if (FindDashboardElement("SeriesListView") is ItemsControl seriesListScrollViewer)
                seriesListScrollViewer.Visibility = IsSeriesCatalog && IsVodListView ? Visibility.Visible : Visibility.Collapsed;
        }


        internal void TileSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
            if (FindDashboardElement("TileSizeCombo") is ComboBox tileSizeCombo)
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

            if (FindDashboardElement("VodTileSizeCombo") is ComboBox vodTileSizeCombo)
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
