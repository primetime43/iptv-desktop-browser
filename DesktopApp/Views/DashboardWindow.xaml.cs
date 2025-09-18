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

namespace DesktopApp.Views
{
    public partial class DashboardWindow : Window, INotifyPropertyChanged
    {
        private RecordingStatusWindow? _recordingWindow;
        private VodWindow? _vodWindow; // new separate VOD window
        // NOTE: Duplicate recording fields and OnClosed removed. This is the consolidated file.
        // Collections / state
        private readonly HttpClient _http = new();
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

        public DashboardWindow()
        {
            InitializeComponent();
            DataContext = this;
            // User name display removed in new layout

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

            Loaded += async (_, __) =>
            {
                Session.EpgRefreshRequested += OnEpgRefreshRequested;

                if (Session.Mode == SessionMode.Xtream)
                {
                    _nextScheduledEpgRefreshUtc = DateTime.UtcNow + Session.EpgRefreshInterval;
                    _ = RunEpgSchedulerLoopAsync();
                    await LoadCategoriesAsync();
                    await LoadVodCategoriesAsync();
                    await LoadSeriesCategoriesAsync();
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

            if (string.IsNullOrWhiteSpace(SearchQuery))
                return true;

            return c.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase);
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
            // Non-global: just refresh existing collection views
            CategoriesCollectionView.Refresh();
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
            // Simple case-insensitive contains; can extend later
            var matches = _allChannelsIndex.Where(c => c.Name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (!string.IsNullOrWhiteSpace(c.NowTitle) && c.NowTitle.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                                           .Take(1000) // safeguard huge lists
                                           .ToList();
            foreach (var m in matches) _channels.Add(m);
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
                _allChannelsIndexLoading = true; SetGuideLoading(true);
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
            finally { _allChannelsIndexLoading = false; SetGuideLoading(false); }
        }

        // ===================== M3U XMLTV EPG =====================
        private void OnM3uEpgUpdated()
        {
            if (Session.Mode != SessionMode.M3u) return;
            Dispatcher.Invoke(() =>
            {
                LastEpgUpdateText = DateTime.UtcNow.ToLocalTime().ToString("g");
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
                // VOD access check
                if (FindName("VodNavButton") is Button vodBtn)
                {
                    vodBtn.IsEnabled = HasVodAccess;
                    vodBtn.Opacity = HasVodAccess ? 1.0 : 0.5;
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
        private void NavVod_Click(object sender, RoutedEventArgs e)
        {
            if (!HasVodAccess)
            {
                MessageBox.Show("VOD is not available for this account or connection type.", "VOD Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Open dedicated VOD window (single instance)
            if (_vodWindow == null || !_vodWindow.IsVisible)
            {
                _vodWindow = new VodWindow(this) { Owner = this };
                _vodWindow.Closed += (_, __) => _vodWindow = null;
                _vodWindow.Show();
            }
            else
            {
                _vodWindow.Activate();
            }
        }
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
            _categories.Clear(); foreach (var c in groups) _categories.Add(c);
            CategoriesCountText = _categories.Count + " categories";
            ApplySearch();
        }
        private async Task LoadCategoriesAsync()
        {
            if (Session.Mode != SessionMode.Xtream) return;
            try
            {
                var url = Session.BuildApi("get_live_categories"); Log($"GET {url}\n"); var json = await _http.GetStringAsync(url, _cts.Token); Log(json + "\n\n");
                var parsed = new List<Category>();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var el in doc.RootElement.EnumerateArray())
                            parsed.Add(new Category
                            {
                                Id = el.TryGetProperty("category_id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty,
                                Name = el.TryGetProperty("category_name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                ParentId = el.TryGetProperty("parent_id", out var pEl) ? pEl.GetInt32() : 0,
                                ImageUrl = el.TryGetProperty("category_image", out var imgEl) && imgEl.ValueKind == JsonValueKind.String ? imgEl.GetString() : null
                            });
                }
                catch (Exception ex) { Log("PARSE ERROR categories: " + ex.Message + "\n"); }
                _categories.Clear(); foreach (var c in parsed) _categories.Add(c); CategoriesCountText = $"{_categories.Count} categories";
                ApplySearch();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR: " + ex.Message + "\n"); }
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
                    _channels.Clear(); foreach (var c in list) _channels.Add(c);
                    ChannelsCountText = _channels.Count.ToString() + " channels";
                    _ = Task.Run(() => PreloadLogosAsync(_channels), _cts.Token);
                    foreach (var c in _channels) UpdateChannelEpgFromXmltv(c);
                }
                finally { SetGuideLoading(false); }
                ApplySearch();
                return;
            }
            SetGuideLoading(true);
            try
            {
                var url = Session.BuildApi("get_live_streams") + "&category_id=" + Uri.EscapeDataString(cat.Id);
                Log($"GET {url}\n"); var json = await _http.GetStringAsync(url, _cts.Token); Log(json + "\n\n");
                var parsed = new List<Channel>();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var el in doc.RootElement.EnumerateArray())
                            parsed.Add(new Channel
                            {
                                Id = el.TryGetProperty("stream_id", out var idEl) && idEl.TryGetInt32(out var sid) ? sid : 0,
                                Name = el.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                Logo = el.TryGetProperty("stream_icon", out var iconEl) ? iconEl.GetString() : null,
                                EpgChannelId = el.TryGetProperty("epg_channel_id", out var epgEl) ? epgEl.GetString() : null
                            });
                }
                catch (Exception ex) { Log("PARSE ERROR channels: " + ex.Message + "\n"); }
                _channels.Clear(); foreach (var c in parsed) _channels.Add(c);
                ChannelsCountText = _channels.Count.ToString() + " channels";
                _ = Task.Run(() => PreloadLogosAsync(parsed), _cts.Token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var tasks = _channels.Select(ch => Dispatcher.InvokeAsync(() => _ = EnsureEpgLoadedAsync(ch)).Task).ToList();
                        await Task.WhenAll(tasks);
                        if (Session.LastEpgUpdateUtc == null)
                        {
                            Session.LastEpgUpdateUtc = DateTime.UtcNow; Dispatcher.Invoke(() => LastEpgUpdateText = Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g"));
                        }
                    }
                    catch { }
                }, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR loading channels: " + ex.Message + "\n"); }
            finally { SetGuideLoading(false); }
            ApplySearch();
        }

        // ===================== Logos =====================
        private async Task PreloadLogosAsync(IEnumerable<Channel> channels)
        { try { await Task.WhenAll(channels.Where(c => !string.IsNullOrWhiteSpace(c.Logo)).Select(LoadLogoAsync)); } catch { } }
        private async Task LoadLogoAsync(Channel channel)
        {
            if (_cts.IsCancellationRequested) return; var url = channel.Logo; if (string.IsNullOrWhiteSpace(url)) return; if (_logoCache.TryGetValue(url, out var cached)) { channel.LogoImage = cached; return; }
            try
            {
                await _logoSemaphore.WaitAsync(_cts.Token);
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token); if (!resp.IsSuccessStatusCode) return;
                await using var ms = new MemoryStream(); await resp.Content.CopyToAsync(ms, _cts.Token); if (_cts.IsCancellationRequested) return; ms.Position = 0;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        _logoCache[url] = bmp;
                        channel.LogoImage = bmp;
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to load logo for {channel.Name}: {ex.Message}\n");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                Log($"Error loading logo from {url}: {ex.Message}\n");
            }
            finally
            {
                if (_logoSemaphore.CurrentCount < 6)
                    _logoSemaphore.Release();
            }
        }

        // ===================== Xtream EPG loading =====================
        private async Task EnsureEpgLoadedAsync(Channel ch, bool force = false)
        {
            if (Session.Mode != SessionMode.Xtream) return; if (_cts.IsCancellationRequested) return; if (!force && (ch.EpgLoaded || ch.EpgLoading)) return; if (!force && ch.EpgAttempts >= 3 && !string.IsNullOrEmpty(ch.NowTitle)) return; await LoadEpgCurrentOnlyAsync(ch, force);
        }
        private static string TryGetString(JsonElement el, params string[] names)
        { foreach (var n in names) if (el.TryGetProperty(n, out var p)) { if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? string.Empty; if (p.ValueKind == JsonValueKind.Number) return p.ToString(); } return string.Empty; }
        private async Task LoadEpgCurrentOnlyAsync(Channel ch, bool force)
        {
            if (Session.Mode != SessionMode.Xtream) return; ch.EpgLoading = true; ch.EpgAttempts++;
            try
            {
                if (_cts.IsCancellationRequested) { ch.EpgLoaded = true; ch.EpgLoading = false; return; }
                var url = Session.BuildApi("get_simple_data_table") + "&stream_id=" + ch.Id; Log($"GET {url} (current only, attempt {ch.EpgAttempts})\n");
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token); var json = await resp.Content.ReadAsStringAsync(_cts.Token); Log("(length=" + json.Length + ")\n\n"); if (_cts.IsCancellationRequested) return;
                var trimmed = json.AsSpan().TrimStart(); if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) { Log("EPG non-JSON response skipped\n"); ch.EpgLoaded = true; return; }
                var nowUtc = DateTime.UtcNow; bool found = false; EpgEntry? fallbackFirstFuture = null;
                try
                {
                    using var doc = JsonDocument.Parse(json); if (doc.RootElement.TryGetProperty("epg_listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                        foreach (var el in listings.EnumerateArray())
                        {
                            var start = GetUnix(el, "start_timestamp"); var end = GetUnix(el, "stop_timestamp"); if (start == DateTime.MinValue || end == DateTime.MinValue) continue;
                            string titleRaw = TryGetString(el, "title", "name", "programme", "program"); string descRaw = TryGetString(el, "description", "desc", "info", "plot", "short_description");
                            string title = DecodeMaybeBase64(titleRaw); string desc = DecodeMaybeBase64(descRaw);
                            bool nowFlag = el.TryGetProperty("now_playing", out var np) && (np.ValueKind == JsonValueKind.Number ? np.GetInt32() == 1 : (np.ValueKind == JsonValueKind.String && np.GetString() == "1")); bool isCurrent = nowFlag || (nowUtc >= start && nowUtc < end);
                            if (isCurrent && !string.IsNullOrWhiteSpace(title)) { ApplyNow(ch, title, desc, start, end); found = true; break; }
                            if (!isCurrent && start > nowUtc && fallbackFirstFuture == null && !string.IsNullOrWhiteSpace(title)) fallbackFirstFuture = new EpgEntry { StartUtc = start, EndUtc = end, Title = title, Description = desc };
                        }
                }
                catch (Exception ex) { Log("PARSE ERROR (current epg): " + ex.Message + "\n"); }
                if (!found && fallbackFirstFuture != null) { ApplyNow(ch, fallbackFirstFuture.Title, fallbackFirstFuture.Description ?? string.Empty, fallbackFirstFuture.StartUtc, fallbackFirstFuture.EndUtc); found = true; }
                if (!found) { ch.EpgLoaded = false; if (ch.EpgAttempts < 3 && !force) _ = Task.Delay(1500, _cts.Token).ContinueWith(t => { if (!t.IsCanceled) _ = Dispatcher.InvokeAsync(() => EnsureEpgLoadedAsync(ch)); }); }
                else ch.EpgLoaded = true;
            }
            catch (OperationCanceledException) { ch.EpgLoaded = true; }
            catch (Exception ex) { Log("ERROR loading epg: " + ex.Message + "\n"); ch.EpgLoaded = true; }
            finally { ch.EpgLoading = false; }
        }
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
        private void OpenSettings_Click(object sender, RoutedEventArgs e) { var settings = new SettingsWindow { Owner = this }; if (settings.ShowDialog() == true) _nextScheduledEpgRefreshUtc = DateTime.UtcNow + Session.EpgRefreshInterval; }
        private void Log(string text)
        {
            try
            {
                if (_isClosing)
                    return;

                // Output text handling removed in new layout
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
            CancelDebounce(); _isClosing = true; _cts.Cancel(); base.OnClosed(e); _cts.Dispose(); Session.EpgRefreshRequested -= OnEpgRefreshRequested; Session.M3uEpgUpdated -= OnM3uEpgUpdated; RecordingManager.Instance.PropertyChanged -= OnRecordingManagerChanged; if (!_logoutRequested) { if (Owner is MainWindow mw) { try { mw.Close(); } catch { } } Application.Current.Shutdown(); }
        }

        private void OnRecordingManagerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RecordingManager.RecordingChannelId))
            {
                UpdateChannelRecordingStatus();
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

        private void ChannelTile_Click(object sender, RoutedEventArgs e) { }

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
        }

        private async Task LoadVodPosterAsync(VodContent vod)
        {
            if (string.IsNullOrEmpty(vod.StreamIcon)) return;

            try
            {
                await _logoSemaphore.WaitAsync(_cts.Token);
                try
                {
                    var imageData = await _http.GetByteArrayAsync(vod.StreamIcon, _cts.Token);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new MemoryStream(imageData);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    vod.PosterImage = bitmap;
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

        private async Task LoadSeriesPosterAsync(SeriesContent series)
        {
            if (string.IsNullOrEmpty(series.StreamIcon)) return;

            try
            {
                await _logoSemaphore.WaitAsync(_cts.Token);
                try
                {
                    var imageBytes = await _http.GetByteArrayAsync(series.StreamIcon, _cts.Token);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        using var stream = new MemoryStream(imageBytes);
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        series.PosterImage = bitmap;
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
            if (_recordProcess != null) return; if (SelectedChannel == null) { Log("No channel selected to record.\n"); return; }
            string streamUrl = Session.Mode == SessionMode.M3u ? Session.PlaylistChannels.FirstOrDefault(p => p.Id == SelectedChannel.Id)?.StreamUrl ?? string.Empty : Session.BuildStreamUrl(SelectedChannel.Id, "ts");
            if (string.IsNullOrWhiteSpace(streamUrl)) { Log("Stream URL not found for recording.\n"); return; }
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
            if (_recordProcess.Start()) { try { _recordProcess.BeginOutputReadLine(); _recordProcess.BeginErrorReadLine(); } catch { } RecordingManager.Instance.Start(_currentRecordingFile, SelectedChannel.Name, SelectedChannel.Id, true); if (FindName("RecordBtnText") is TextBlock t) t.Text = "Stop"; }
            else { Log("Failed to start FFmpeg.\n"); _recordProcess.Dispose(); _recordProcess = null; }
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
            if (FindName("RecordBtnText") is TextBlock btnText) btnText.Text = "Record";
            RecordingManager.Instance.Stop();
            Log("Stopping recording...\n");

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
            SetSelectedNavButton(sender as Button);
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
            SetSelectedNavButton(sender as Button);
            LoadSettingsPage();
        }

        private void ShowPage(string pageName)
        {
            // Hide all pages
            if (FindName("LiveTvPage") is Grid liveTvPage) liveTvPage.Visibility = Visibility.Collapsed;
            if (FindName("VodPage") is Grid vodPage) vodPage.Visibility = Visibility.Collapsed;
            if (FindName("RecordingPage") is Grid recordingPage) recordingPage.Visibility = Visibility.Collapsed;
            if (FindName("SchedulerPage") is Grid schedulerPage) schedulerPage.Visibility = Visibility.Collapsed;
            if (FindName("ProfilePage") is Grid profilePage) profilePage.Visibility = Visibility.Collapsed;
            if (FindName("SettingsPage") is Grid settingsPage) settingsPage.Visibility = Visibility.Collapsed;

            // Show selected page
            if (FindName($"{pageName}Page") is Grid targetPage)
                targetPage.Visibility = Visibility.Visible;
        }

        private void SetSelectedNavButton(Button? selectedButton)
        {
            // Clear all nav button selections
            if (FindName("LiveTvNavButton") is Button liveTvBtn) liveTvBtn.Tag = null;
            if (FindName("VodNavButton") is Button vodBtn) vodBtn.Tag = null;
            if (FindName("RecordingNavButton") is Button recordingBtn) recordingBtn.Tag = null;
            if (FindName("SchedulerNavButton") is Button schedulerBtn) schedulerBtn.Tag = null;
            if (FindName("ProfileNavButton") is Button profileBtn) profileBtn.Tag = null;
            if (FindName("SettingsNavButton") is Button settingsBtn) settingsBtn.Tag = null;

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
        }

        private async void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is Category category)
            {
                await LoadChannelsForCategoryAsync(category);
                ShowChannelsView_Click(null, null);
            }
        }

        private void ShowCategoriesView_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("CategoriesScrollViewer") is ScrollViewer categoriesViewer)
                categoriesViewer.Visibility = Visibility.Visible;
            if (FindName("ChannelsScrollViewer") is ScrollViewer channelsViewer)
                channelsViewer.Visibility = Visibility.Collapsed;

            // Update button states
            if (FindName("CategoriesViewBtn") is Button categoriesBtn)
            {
                categoriesBtn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x47));
                categoriesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0xE6));
            }
            if (FindName("ChannelsViewBtn") is Button channelsBtn)
            {
                channelsBtn.Background = Brushes.Transparent;
                channelsBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0xB2, 0xC7));
            }
        }

        private void ShowChannelsView_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("CategoriesScrollViewer") is ScrollViewer categoriesViewer)
                categoriesViewer.Visibility = Visibility.Collapsed;
            if (FindName("ChannelsScrollViewer") is ScrollViewer channelsViewer)
                channelsViewer.Visibility = Visibility.Visible;

            // Update button states
            if (FindName("CategoriesViewBtn") is Button categoriesBtn)
            {
                categoriesBtn.Background = Brushes.Transparent;
                categoriesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0xB2, 0xC7));
            }
            if (FindName("ChannelsViewBtn") is Button channelsBtn)
            {
                channelsBtn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x47));
                channelsBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0xE6));
            }
        }

        private async void CategoryTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is Category category)
            {
                await LoadChannelsForCategoryAsync(category);

                // Switch to channels view
                ShowChannelsView_Click(null, null);

                // Update category combo selection
                if (FindName("CategoryCombo") is ComboBox combo)
                {
                    combo.SelectedItem = category;
                }
            }
        }

        private async void VodCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedValue is string categoryId)
            {
                // Load content based on current view (Movies or Series)
                if (FindName("MoviesScrollViewer") is ScrollViewer moviesViewer && moviesViewer.Visibility == Visibility.Visible)
                {
                    await LoadVodContentAsync(categoryId);
                }
                else if (FindName("SeriesScrollViewer") is ScrollViewer seriesViewer && seriesViewer.Visibility == Visibility.Visible)
                {
                    await LoadSeriesContentAsync(categoryId);
                }
            }
        }

        private void ShowMoviesView_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("MoviesScrollViewer") is ScrollViewer moviesViewer)
                moviesViewer.Visibility = Visibility.Visible;
            if (FindName("SeriesScrollViewer") is ScrollViewer seriesViewer)
                seriesViewer.Visibility = Visibility.Collapsed;

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
            if (FindName("MoviesScrollViewer") is ScrollViewer moviesViewer)
                moviesViewer.Visibility = Visibility.Collapsed;
            if (FindName("SeriesScrollViewer") is ScrollViewer seriesViewer)
                seriesViewer.Visibility = Visibility.Visible;

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


        private void OpenRecordingScheduler_Click(object sender, RoutedEventArgs e)
        {
            var scheduler = new RecordingSchedulerWindow { Owner = this };
            scheduler.Show();
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

        private void VodTrailerButton_Click(object sender, RoutedEventArgs e)
        {
            // Could implement trailer functionality in the future
            MessageBox.Show("Trailer functionality not yet implemented.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
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

                // Initialize debug output for API call logging
                if (FindName("DebugOutputBox") is TextBox debugBox)
                {
                    if (string.IsNullOrEmpty(debugBox.Text))
                    {
                        debugBox.Text = $"[{DateTime.Now:HH:mm:ss}] Profile page loaded. API calls will be logged here.\n";
                        debugBox.Text += $"[{DateTime.Now:HH:mm:ss}] Connection Info - Server: {(Session.UseSsl ? "https" : "http")}://{Session.Host}:{Session.Port}, User: {Session.Username}, Mode: {Session.Mode}\n";
                    }
                }
            }
            catch (Exception ex)
            {
                if (FindName("DebugOutputBox") is TextBox debugBox)
                {
                    debugBox.Text += $"\nError loading profile data: {ex.Message}\n";
                }
            }
        }

        private async void RefreshProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (FindName("DebugOutputBox") is TextBox debugBox)
                    debugBox.Text += $"\n[{DateTime.Now:HH:mm:ss}] Refreshing profile data...\n";

                // Re-fetch user info if in Xtream mode
                if (Session.Mode == SessionMode.Xtream)
                {
                    var url = Session.BuildApi("get_account_info");
                    var response = await _http.GetStringAsync(url);
                    var userInfo = JsonSerializer.Deserialize<UserInfo>(response);

                    if (userInfo != null)
                    {
                        Session.UserInfo = userInfo;
                        if (FindName("DebugOutputBox") is TextBox debugBox2)
                            debugBox2.Text += $"[{DateTime.Now:HH:mm:ss}] Profile refreshed successfully.\n";
                    }
                }

                LoadProfileData();
            }
            catch (Exception ex)
            {
                if (FindName("DebugOutputBox") is TextBox debugBox)
                    debugBox.Text += $"[{DateTime.Now:HH:mm:ss}] Error refreshing profile: {ex.Message}\n";
            }
        }

        private void ClearDebugOutput_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("DebugOutputBox") is TextBox debugBox)
            {
                debugBox.Clear();
                debugBox.Text = $"[{DateTime.Now:HH:mm:ss}] Debug log cleared.\n";
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

                ValidateAllSettingsFields();
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

        public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
