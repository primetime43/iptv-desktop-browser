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

namespace DesktopApp.Views;

public partial class DashboardWindow
{
        // ===== Index building for playlist mode (M3U) =====
        private async Task BuildPlaylistAllChannelsIndexAsync()
        {
            if (Session.Mode != SessionMode.M3u)
                return;

            var token = _cts.Token;
            var playlist = Session.PlaylistChannels.ToArray();
            var index = await Task.Run(() => playlist.Select((p, i) => new Channel
            {
                Id = p.Id,
                Number = i + 1,
                Name = p.Name,
                Logo = p.Logo,
                EpgChannelId = p.TvgId
            }).ToList(), token);
            token.ThrowIfCancellationRequested();
            _allChannelsIndex = index;
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
            if (IsGlobalSearchActive) CancelCategoryLoad();
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
            if (IsGlobalSearchActive) CancelCategoryLoad();
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

                _ = EnsureAllChannelsIndexAndFilterAsync();
                return; // filtering will happen async
            }
            // Non-global: just refresh existing collection views (but not categories)
            ChannelsCollectionView.Refresh();
            VodContentCollectionView.Refresh();
            SeriesContentCollectionView.Refresh();
            CategoriesCountText = _categories.Count(c => CategoriesFilter(c)).ToString() + " categories";
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
                    await BuildPlaylistAllChannelsIndexAsync();
            }
            await FilterGlobalChannelsAsync();
        }

        private async Task FilterGlobalChannelsAsync()
        {
            if (!IsGlobalSearchActive || _cts.IsCancellationRequested || _allChannelsIndex == null) return;
            var query = SearchQuery.Trim();
            var index = _allChannelsIndex;
            await _globalSearchLoader.LoadAsync(token => Task.Run(() =>
            {
                var numeric = int.TryParse(query, out var number);
                var matches = new List<Channel>();
                foreach (var channel in index)
                {
                    token.ThrowIfCancellationRequested();
                    if (numeric ? channel.Number == number :
                        channel.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        channel.NowTitle?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                        matches.Add(channel);
                    if (matches.Count == 1000) break;
                }
                return matches;
            }, token), matches =>
            {
                for (var i = 0; i < matches.Count; i++) matches[i].Number = i + 1;
                _channels.ReplaceAll(matches);
                UpdateChannelsFavoriteStatus();
                ChannelsCountText = $"{matches.Count} channels";
                ScheduleCatalogRefresh();
            }, ex => Log($"ERROR filtering channels: {ex.Message}\n"), () => { },
                () => IsGlobalSearchActive && SearchQuery.Trim() == query && ReferenceEquals(index, _allChannelsIndex), _cts.Token);
        }
        private async Task LoadAllChannelsIndexAsync()
        {
            if (_allChannelsIndexLoading || _allChannelsIndexLoaded || Session.Mode != SessionMode.Xtream) return;
            var token = _cts.Token;
            try
            {
                _allChannelsIndexLoading = true;
                OnPropertyChanged(nameof(IsSearchLoading));
                SetGuideLoading(true);
                var url = Session.BuildApi("get_live_streams"); Log($"GET {url} (index all channels)\n");
                var json = await _http.GetStringAsync(url, token); Log("(length=" + json.Length + ")\n\n");
                var prepared = await Task.Run(() =>
                {
                    var list = new List<Channel>();
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                token.ThrowIfCancellationRequested();
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
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log("PARSE ERROR all channels index: " + ex.Message + "\n"); }
                    return list;
                }, token);
                token.ThrowIfCancellationRequested();
                _allChannelsIndex = prepared;
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
                if (FindDashboardElement("VodNavButton") is Button vodBtn)
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
            ShowPage(Navigation.ActivePage);
        }

        // ===================== Categories / Channels =====================
        private async Task LoadCategoriesFromPlaylistAsync()
        {
            var token = _cts.Token;
            var playlist = Session.PlaylistChannels.ToArray();
            var groups = await Task.Run(() => playlist.GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? "Other" : p.Category)
                .OrderBy(g => g.Key)
                .Select(g => new Category { Id = g.Key, Name = g.Key, ParentId = 0, ImageUrl = null }).ToList(), token);
            token.ThrowIfCancellationRequested();

            // Add Favorites as the first category
            groups.Insert(0, new Category { Id = "⭐ Favorites", Name = "⭐ Favorites" });
            _categories.ReplaceAll(groups);
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

                categories.Insert(0, new Category { Id = "⭐ Favorites", Name = "⭐ Favorites" });
                _categories.ReplaceAll(categories);

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
        private void CancelCategoryLoad()
        {
            _categoryLoader.Cancel();
            _catalogResources?.Cancel();
            SetGuideLoading(false);
            HideLoadingOverlay("ChannelsLoadingOverlay");
        }

        private async Task LoadChannelsForCategoryAsync(Category cat)
        {
            if (IsGlobalSearchActive || _isClosing || _cts.IsCancellationRequested)
            {
                CancelCategoryLoad();
                return;
            }

            await _categoryLoader.LoadAsync(
                async token =>
                {
                    ShowLoadingOverlay("ChannelsLoadingOverlay");
                    SetGuideLoading(true);
                    // Do not leave the previous category visible if this request fails.
                    _channels.Clear();
                    ChannelsCountText = "0 channels";
                    SelectedChannel = null;
                    _catalogResources?.Cancel();
                    Log($"Loading channels for category: {cat.Name}\n");
                    var favorites = Session.GetFavoriteChannels().Select(f => f.Id).ToHashSet();
                    List<Channel> channels;
                    if (cat.Id == "⭐ Favorites") channels = GetFavoriteCategoryChannels();
                    else if (Session.Mode == SessionMode.M3u)
                    {
                        var playlist = Session.PlaylistChannels.ToArray();
                        channels = await Task.Run(() => playlist
                            .Where(p => (string.IsNullOrWhiteSpace(p.Category) ? "Other" : p.Category) == cat.Id)
                            .Select(p => new Channel { Id = p.Id, Name = p.Name, Logo = p.Logo, EpgChannelId = p.TvgId })
                            .ToList(), token);
                    }
                    else channels = await _channelService.LoadChannelsForCategoryAsync(cat, token);
                    return await Task.Run(() =>
                    {
                        for (var i = 0; i < channels.Count; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            channels[i].Number = i + 1;
                            channels[i].IsFavorite = favorites.Contains(channels[i].Id);
                        }
                        return channels;
                    }, token);
                },
                channels =>
                {
                    _channels.ReplaceAll(channels);
                    ChannelsCountText = cat.Id == "⭐ Favorites"
                        ? $"{channels.Count} favorite channels" : $"{channels.Count} channels";
                    Log($"Loaded {channels.Count} channels for category: {cat.Name}\n");
                    // Layout determines visible and nearby resource demand.
                    ScheduleCatalogRefresh();
                    if (Session.Mode == SessionMode.M3u)
                        UpdateChannelsEpgFromXmltvBatch(channels);
                    var count = ((CollectionView)ChannelsCollectionView).Count;
                    ChannelsCountText = cat.Id == "⭐ Favorites" ? $"{count} favorite channels" : $"{count} channels";
                },
                ex => Log("ERROR loading channels: " + ex.Message + "\n"),
                () =>
                {
                    SetGuideLoading(false);
                    HideLoadingOverlay("ChannelsLoadingOverlay");
                },
                () => !_isClosing && !IsGlobalSearchActive && ReferenceEquals(CategoryCombo.SelectedItem, cat),
                _cts.Token);
        }

        private static List<Channel> GetFavoriteCategoryChannels()
        {
            return Session.GetFavoriteChannels().Select(favorite =>
            {
                var playlistChannel = Session.Mode == SessionMode.M3u
                    ? Session.PlaylistChannels.FirstOrDefault(p => p.Id == favorite.Id) : null;
                return new Channel
                {
                    Id = favorite.Id,
                    Name = playlistChannel != null ? playlistChannel.Name : favorite.Name,
                    Logo = playlistChannel != null ? playlistChannel.Logo : favorite.Logo,
                    EpgChannelId = playlistChannel != null ? playlistChannel.TvgId : favorite.EpgChannelId,
                    IsFavorite = true
                };
            }).ToList();
        }

}
