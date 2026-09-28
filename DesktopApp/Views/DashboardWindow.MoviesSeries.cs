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
            if (FindDashboardElement("VodDetailsPlaceholder") is TextBlock placeholder)
            {
                placeholder.Text = "Select a movie or series to view details";
                placeholder.Visibility = Visibility.Visible;
            }

            if (FindDashboardElement("VodDetailsContent") is StackPanel content)
            {
                content.Visibility = Visibility.Collapsed;
            }

            // Hide episodes section when clearing
            HideEpisodesUI();

            // Hide actions panel when clearing
            if (FindDashboardElement("VodActionsPanel") is StackPanel actionsPanel)
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
                if (FindDashboardElement("VodDetailsPlaceholder") is TextBlock placeholder)
                {
                    placeholder.Text = "Loading details...";
                    placeholder.Visibility = Visibility.Visible;
                }

                if (FindDashboardElement("VodDetailsContent") is StackPanel content)
                    content.Visibility = Visibility.Collapsed;

                // Start loading details in background (fire and forget)
                _ = LoadVodDetailsAsync(vod);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error showing VOD details: {ex.Message}");
                // Show error state
                if (FindDashboardElement("VodDetailsPlaceholder") is TextBlock placeholder)
                {
                    placeholder.Text = "Failed to load details";
                    placeholder.Visibility = Visibility.Visible;
                }
                if (FindDashboardElement("VodDetailsContent") is StackPanel content)
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
            if (_isClosing || _cts.IsCancellationRequested || IsSeriesCatalog ||
                !ReferenceEquals(vod, SelectedVodContent)) return;
            // Hide placeholder, show content
            if (FindDashboardElement("VodDetailsPlaceholder") is TextBlock placeholder)
                placeholder.Visibility = Visibility.Collapsed;

            if (FindDashboardElement("VodDetailsContent") is StackPanel content)
                content.Visibility = Visibility.Visible;

            // Set title
            if (FindDashboardElement("VodDetailsTitle") is TextBlock title)
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
            if (FindDashboardElement("VodDetailsYear") is TextBlock year)
                year.Text = !string.IsNullOrWhiteSpace(vod.ReleaseDate) ? vod.DisplayYear : "";

            if (FindDashboardElement("VodDetailsDuration") is TextBlock duration)
                duration.Text = !string.IsNullOrWhiteSpace(vod.Duration) ? vod.DisplayDuration : "";

            if (FindDashboardElement("VodDetailsRating") is TextBlock rating)
                rating.Text = !string.IsNullOrWhiteSpace(vod.Rating) ? vod.Rating : "";

            if (FindDashboardElement("VodDetailsGenre") is TextBlock genre)
                genre.Text = !string.IsNullOrWhiteSpace(vod.Genre) ? vod.Genre : "";

            if (FindDashboardElement("VodDetailsCast") is TextBlock cast)
                cast.Text = !string.IsNullOrWhiteSpace(vod.Cast) ? vod.Cast : "";

            if (FindDashboardElement("VodDetailsDirector") is TextBlock director)
                director.Text = !string.IsNullOrWhiteSpace(vod.Director) ? vod.Director : "";

            if (FindDashboardElement("VodDetailsPlot") is TextBlock plot)
                plot.Text = !string.IsNullOrWhiteSpace(vod.Plot) ? vod.Plot : "No plot available";

            // Hide episodes section for movies
            HideEpisodesUI();

            // Show play button for movies only
            if (FindDashboardElement("VodActionsPanel") is StackPanel actionsPanel)
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
                if (FindDashboardElement("VodDetailsPlaceholder") is TextBlock placeholder)
                {
                    placeholder.Text = "Loading details...";
                    placeholder.Visibility = Visibility.Visible;
                }

                if (FindDashboardElement("VodDetailsContent") is StackPanel content)
                    content.Visibility = Visibility.Collapsed;

                // Start loading details in background (fire and forget)
                _ = LoadSeriesDetailsAsync(series);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error showing series details: {ex.Message}");
                // Show error state
                if (FindDashboardElement("VodDetailsPlaceholder") is TextBlock placeholder)
                {
                    placeholder.Text = "Failed to load details";
                    placeholder.Visibility = Visibility.Visible;
                }
                if (FindDashboardElement("VodDetailsContent") is StackPanel content)
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
            if (_isClosing || _cts.IsCancellationRequested || !IsSeriesCatalog ||
                !ReferenceEquals(series, SelectedSeriesContent)) return;
            // Hide placeholder, show content
            if (FindDashboardElement("VodDetailsPlaceholder") is TextBlock placeholder)
                placeholder.Visibility = Visibility.Collapsed;

            if (FindDashboardElement("VodDetailsContent") is StackPanel content)
                content.Visibility = Visibility.Visible;

            // Set title
            if (FindDashboardElement("VodDetailsTitle") is TextBlock title)
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
            if (FindDashboardElement("VodDetailsYear") is TextBlock year)
                year.Text = !string.IsNullOrWhiteSpace(series.ReleaseDate) ? series.DisplayYear : "";

            if (FindDashboardElement("VodDetailsDuration") is TextBlock duration)
                duration.Text = series.SeasonCount > 0 ? series.DisplayDuration : "";

            if (FindDashboardElement("VodDetailsRating") is TextBlock rating)
                rating.Text = !string.IsNullOrWhiteSpace(series.Rating) ? series.Rating : "";

            if (FindDashboardElement("VodDetailsGenre") is TextBlock genre)
                genre.Text = !string.IsNullOrWhiteSpace(series.Genre) ? series.Genre : "";

            if (FindDashboardElement("VodDetailsCast") is TextBlock cast)
                cast.Text = !string.IsNullOrWhiteSpace(series.Cast) ? series.Cast : "";

            if (FindDashboardElement("VodDetailsDirector") is TextBlock director)
                director.Text = !string.IsNullOrWhiteSpace(series.Director) ? series.Director : "";

            if (FindDashboardElement("VodDetailsPlot") is TextBlock plot)
                plot.Text = !string.IsNullOrWhiteSpace(series.Plot) ? series.Plot : "No plot available";

            // Hide play button for TV shows (use episodes instead)
            if (FindDashboardElement("VodActionsPanel") is StackPanel actionsPanel)
                actionsPanel.Visibility = Visibility.Collapsed;

            // Show episodes section for series and populate it
            PopulateEpisodesUI(series);
        }

        private void PopulateEpisodesUI(SeriesContent series)
        {
            // Show episodes section
            if (FindDashboardElement("EpisodesSection") is Border episodesSection)
                episodesSection.Visibility = Visibility.Visible;

            // Clear existing episodes
            if (FindDashboardElement("SeasonsPanel") is StackPanel seasonsPanel)
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
            if (FindDashboardElement("EpisodesSection") is Border episodesSection)
                episodesSection.Visibility = Visibility.Collapsed;

            // Clear episodes
            if (FindDashboardElement("SeasonsPanel") is StackPanel seasonsPanel)
                seasonsPanel.Children.Clear();
        }

        internal void VodPlayButton_Click(object sender, RoutedEventArgs e)
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
        internal void VodCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo) return;
            // Keep the selection used by filters and request guards in sync with the shared picker.
            if (IsSeriesCatalog)
                SelectedSeriesCategoryId = (combo.SelectedItem as SeriesCategory)?.CategoryId ?? string.Empty;
            else
                SelectedVodCategoryId = (combo.SelectedItem as VodCategory)?.CategoryId ?? string.Empty;
        }

        internal void ShowMoviesView_Click(object sender, RoutedEventArgs e) => ChangeCatalogType(CatalogContentType.Movies);
        internal void ShowSeriesView_Click(object sender, RoutedEventArgs e) => ChangeCatalogType(CatalogContentType.Series);

        private void ChangeCatalogType(CatalogContentType type)
        {
            if (Catalog.ContentType == type) ApplyCatalogView();
            else Catalog.ContentType = type;
        }

        private void ApplyCatalogView()
        {
            UpdateVodViewVisibility();
            if (FindDashboardElement("MoviesViewBtn") is Button moviesButton)
            {
                moviesButton.Background = !IsSeriesCatalog ? new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x47)) : Brushes.Transparent;
                moviesButton.Foreground = !IsSeriesCatalog ? new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0xE6)) : new SolidColorBrush(Color.FromRgb(0x9D, 0xB2, 0xC7));
            }
            if (FindDashboardElement("SeriesViewBtn") is Button seriesButton)
            {
                seriesButton.Background = IsSeriesCatalog ? new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x47)) : Brushes.Transparent;
                seriesButton.Foreground = IsSeriesCatalog ? new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0xE6)) : new SolidColorBrush(Color.FromRgb(0x9D, 0xB2, 0xC7));
            }
            if (FindDashboardElement("VodCategoryCombo") is ComboBox combo)
                combo.ItemsSource = IsSeriesCatalog ? Catalog.SeriesCategoriesView : Catalog.MovieCategoriesView;
            ClearVodDetailsPanel();
            RefreshCatalogResources();
        }
        internal void SeriesContent_Click(object sender, RoutedEventArgs e)
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


        private async Task LoadVodCategoriesAsync()
        {
            if (Session.Mode != SessionMode.Xtream)
            {
                HasVodAccess = false;
                return;
            }
            try
            {
                var parsed = await _vodService.LoadVodCategoriesAsync(_cts.Token);

                // Populate local collection for UI binding
                _vodCategories.ReplaceAll(parsed);

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
                var parsed = await _vodService.LoadSeriesCategoriesAsync(_cts.Token);

                _seriesCategories.ReplaceAll(parsed);
                Session.SeriesCategories.Clear();
                Session.SeriesCategories.AddRange(parsed);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("ERROR loading series categories: " + ex.Message + "\n");
            }
        }

        private void CancelVodRequests()
        {
            _vodCategoryLoader.Cancel();
            _seriesCategoryLoader.Cancel();
            _detailsLoader.Cancel();
            IsLoadingVodContent = false;
            IsLoadingSeriesContent = false;
            HideLoadingOverlay("MoviesLoadingOverlay");
            HideLoadingOverlay("SeriesLoadingOverlay");
            ClearVodDetailsPanel();
        }

        private async Task LoadVodContentAsync(string categoryId)
        {
            if (Session.Mode != SessionMode.Xtream || string.IsNullOrEmpty(categoryId)) return;

            // Set loading state
            IsLoadingVodContent = true;
            RefreshCatalogResources();

            // Show toast notification
            ShowToast("📽️ Loading Movies", "Fetching movie list...", "#347DFF");

            ShowLoadingOverlay("MoviesLoadingOverlay");
            var previousCatalog = Session.VodContent.ToArray();
            await _vodCategoryLoader.LoadAsync(
                async token =>
                {
                    var parsed = await _vodService.LoadVodContentAsync(categoryId, token);
                    return await Task.Run(() => (Parsed: parsed,
                        Catalog: previousCatalog.Where(v => v.CategoryId != categoryId).Concat(parsed).ToList()), token);
                },
                result =>
            {
                // Add to session
                Session.VodContent.Clear();
                Session.VodContent.AddRange(result.Catalog);

                // Update UI collection
                _vodContent.ReplaceAll(result.Parsed);
                var filteredVodCount = ((CollectionView)VodContentCollectionView).Count;
                VodCountText = $"{filteredVodCount} movies";

                // Show success toast
                ShowToast("✅ Movies Loaded", $"Loaded {filteredVodCount} movie{(filteredVodCount != 1 ? "s" : "")}", "#28A745");
            }, ex =>
            {
                Log("ERROR loading VOD content: " + ex.Message + "\n");
                ShowToast("❌ Loading Failed", "Failed to load movies", "#DC3545");
            }, () =>
            {
                IsLoadingVodContent = false;
                HideLoadingOverlay("MoviesLoadingOverlay");
                ScheduleCatalogRefresh();
            }, () => !IsSeriesCatalog && SelectedVodCategoryId == categoryId, _cts.Token);
        }

        private void OnVodCategoryChanged()
        {
            _vodCategoryLoader.Cancel();
            _vodContent.Clear();
            ClearVodDetailsPanel();
            VodCountText = "0 movies";
            if (!string.IsNullOrEmpty(SelectedVodCategoryId))
            {
                _ = LoadVodContentAsync(SelectedVodCategoryId);
            }
            else
            {
                IsLoadingVodContent = false;
                HideLoadingOverlay("MoviesLoadingOverlay");
                VodContentCollectionView.Refresh();
                RefreshCatalogResources();
            }
        }

        private void OnSeriesCategoryChanged()
        {
            _seriesCategoryLoader.Cancel();
            _seriesContent.Clear();
            ClearVodDetailsPanel();
            SeriesCountText = "0 series";
            if (!string.IsNullOrEmpty(SelectedSeriesCategoryId))
            {
                _ = LoadSeriesContentAsync(SelectedSeriesCategoryId);
            }
            else
            {
                IsLoadingSeriesContent = false;
                SeriesContentCollectionView.Refresh();
                RefreshCatalogResources();
            }
        }

        private async Task LoadSeriesContentAsync(string categoryId)
        {
            // Set loading state
            IsLoadingSeriesContent = true;
            RefreshCatalogResources();

            // Show toast notification
            ShowToast("📺 Loading TV Shows", "Fetching series list...", "#347DFF");

            await _seriesCategoryLoader.LoadAsync(
                token => _vodService.LoadSeriesContentAsync(categoryId, token),
                parsed =>
            {
                // Add to session
                Session.SeriesContent.Clear();
                Session.SeriesContent.AddRange(parsed);

                // Update UI collection
                _seriesContent.ReplaceAll(parsed);
                var filteredSeriesCount = ((CollectionView)SeriesContentCollectionView).Count;
                SeriesCountText = $"{filteredSeriesCount} series";
                ScheduleCatalogRefresh();

                // Show success toast
                ShowToast("✅ TV Shows Loaded", $"Loaded {filteredSeriesCount} series", "#28A745");
            }, ex =>
            {
                Log("ERROR loading series content: " + ex.Message + "\n");
                ShowToast("❌ Loading Failed", "Failed to load TV shows", "#DC3545");
            }, () =>
            {
                IsLoadingSeriesContent = false;
                ScheduleCatalogRefresh();
            }, () => IsSeriesCatalog && SelectedSeriesCategoryId == categoryId, _cts.Token);
        }


        private Task LoadSeriesDetailsAsync(SeriesContent content)
        {
            if (Session.Mode != SessionMode.Xtream) return Task.CompletedTask;
            return _detailsLoader.LoadAsync(content,
                (snapshot, token) => _vodService.LoadSeriesDetailsAsync(snapshot, token),
                (target, details) => target.ApplyDetails(details),
                ex => Log($"ERROR loading details: {ex.Message}\n"),
                () => IsSeriesCatalog && ReferenceEquals(content, SelectedSeriesContent), _cts.Token);
        }

        private void VodCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Handled by SelectedVodCategoryId property change
        }

        internal void VodContent_Click(object sender, RoutedEventArgs e)
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

        private Task LoadVodDetailsAsync(VodContent content)
        {
            if (Session.Mode != SessionMode.Xtream) return Task.CompletedTask;
            return _detailsLoader.LoadAsync(content,
                (snapshot, token) => _vodService.LoadVodDetailsAsync(snapshot, token),
                (target, details) => target.ApplyDetails(details),
                ex => Log($"ERROR loading details: {ex.Message}\n"),
                () => !IsSeriesCatalog && ReferenceEquals(content, SelectedVodContent), _cts.Token);
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
            // Series episodes are now shown inline in the VodPage
            // This method will load the series details and switch to the series view
            try
            {
                Log($"Loading series: {series.Name}\n");

                // Switch to VOD page and series view
                ShowPage(DashboardPage.Vod);
                SetSelectedNavButton(FindDashboardElement("VodNavBtn") as Button);

                // Switch to series view if not already there
                ShowSeriesView_Click(null!, null!);

                // Select the series - this will trigger episode loading
                SelectedSeriesContent = series;
            }
            catch (Exception ex)
            {
                Log("Failed to load series: " + ex.Message + "\n");
                MessageBox.Show(this, "Unable to load series details.",
                    "Series Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

}
