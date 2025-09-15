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

namespace DesktopApp.Views
{
    public partial class DashboardWindow : Window, INotifyPropertyChanged
    {
        private RecordingStatusWindow? _recordingWindow;
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
            UserNameText.Text = Session.Username;

            CategoriesCollectionView = CollectionViewSource.GetDefaultView(_categories);
            ChannelsCollectionView = CollectionViewSource.GetDefaultView(_channels);
            VodContentCollectionView = CollectionViewSource.GetDefaultView(_vodContent);
            CategoriesCollectionView.Filter = CategoriesFilter;
            ChannelsCollectionView.Filter = ChannelsFilter;
            VodContentCollectionView.Filter = VodContentFilter;

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
                }
                else
                {
                    LoadCategoriesFromPlaylist();
                    BuildPlaylistAllChannelsIndex();
                }

                UpdateViewVisibility();
                UpdateNavButtons();
            };
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
            if (CurrentViewKey == "categories")
                CategoriesCountText = _categories.Count(c => CategoriesFilter(c)).ToString() + " categories";
            else if (CurrentViewKey == "guide")
                ChannelsCountText = _channels.Count(c => ChannelsFilter(c)).ToString() + " channels";
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
            void StyleBtn(Button? b, bool active, bool enabled = true)
            {
                if (b == null) return;
                b.IsEnabled = enabled;
                if (!enabled)
                {
                    b.Background = System.Windows.Media.Brushes.Transparent;
                    b.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x19, 0x28));
                    b.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x73, 0x85));
                }
                else
                {
                    b.Background = active ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0x7D, 0xFF)) : System.Windows.Media.Brushes.Transparent;
                    b.BorderBrush = active ? b.Background : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x32, 0x47));
                    b.Foreground = active ? System.Windows.Media.Brushes.White : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xE6, 0xF2));
                }
            }
            StyleBtn(NavCategoriesBtn, CurrentViewKey == "categories");
            StyleBtn(NavGuideBtn, CurrentViewKey == "guide", IsGuideReady);
            StyleBtn(NavProfileBtn, CurrentViewKey == "profile");
            StyleBtn(NavOutputBtn, CurrentViewKey == "output");
        }

        private void UpdateViewVisibility()
        {
            if (CategoriesGrid == null) return;
            CategoriesGrid.Visibility = CurrentViewKey == "categories" ? Visibility.Visible : Visibility.Collapsed;
            GuideView.Visibility = CurrentViewKey == "guide" ? Visibility.Visible : Visibility.Collapsed;
            VodView.Visibility = CurrentViewKey == "vod" ? Visibility.Visible : Visibility.Collapsed;
            ProfileView.Visibility = CurrentViewKey == "profile" ? Visibility.Visible : Visibility.Collapsed;
            OutputView.Visibility = CurrentViewKey == "output" ? Visibility.Visible : Visibility.Collapsed;
        }
        private void NavCategories_Click(object sender, RoutedEventArgs e) { CurrentViewKey = "categories"; if (!IsGlobalSearchActive) { /* restore category view list remains as-is */ } }
        private void NavGuide_Click(object sender, RoutedEventArgs e) { CurrentViewKey = "guide"; ApplySearch(); }
        private void NavVod_Click(object sender, RoutedEventArgs e) => CurrentViewKey = "vod";
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
            if (GuideLoadingPanel == null || GuideScroll == null) return;
            GuideLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            GuideScroll.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
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
                if (_isClosing || OutputText == null)
                    return;

                OutputText.AppendText(text);
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
            CancelDebounce(); _isClosing = true; _cts.Cancel(); base.OnClosed(e); _cts.Dispose(); Session.EpgRefreshRequested -= OnEpgRefreshRequested; Session.M3uEpgUpdated -= OnM3uEpgUpdated; if (!_logoutRequested) { if (Owner is MainWindow mw) { try { mw.Close(); } catch { } } Application.Current.Shutdown(); }
        }
        private async void CategoryTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Category cat)
            {
                SelectedCategoryName = cat.Name;
                await LoadChannelsForCategoryAsync(cat);
                IsGuideReady = true;
                CurrentViewKey = "guide";
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
            }
            else
            {
                SelectedChannel = ch;
                _lastChannelClickedElement = fe;
                _lastChannelClickTime = now;
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
            if (Session.Mode != SessionMode.Xtream) return;
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

                _vodCategories.Clear();
                foreach (var c in parsed) _vodCategories.Add(c);
                Session.VodCategories.Clear();
                Session.VodCategories.AddRange(parsed);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR loading VOD categories: " + ex.Message + "\n"); }
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
                VodCountText = $"{_vodContent.Count} movies";
                VodContentCollectionView.Refresh();
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

        private void VodCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Handled by SelectedVodCategoryId property change
        }

        private void VodContent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not VodContent vod)
                return;

            TryLaunchVodInPlayer(vod);
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
            if (_recordProcess.Start()) { try { _recordProcess.BeginOutputReadLine(); _recordProcess.BeginErrorReadLine(); } catch { } RecordingManager.Instance.Start(_currentRecordingFile, SelectedChannel.Name); if (FindName("RecordBtnText") is TextBlock t) t.Text = "Stop"; }
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
            if (_recordProcess == null) StartRecording(); else StopRecording();
        }
        public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
