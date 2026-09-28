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
        private void ClearVodDetailsPanel() => Catalog.Details.Clear();

        // Catalog navigation and loading remain in the shell during the gradual migration.
        internal void VodCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo) return;
            // Keep the selection used by filters and request guards in sync with the shared picker.
            if (IsSeriesCatalog)
                SelectedSeriesCategoryId = (combo.SelectedItem as SeriesCategory)?.CategoryId ?? string.Empty;
            else
                SelectedVodCategoryId = (combo.SelectedItem as VodCategory)?.CategoryId ?? string.Empty;
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
                _ = Catalog.Details.SelectAsync(series, _cts.Token);
                _lastSeriesClickTime = now;
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
                _ = Catalog.Details.SelectAsync(vod, _cts.Token);
                _lastVodClickTime = now;
            }
        }

        private DateTime _lastVodClickTime;
        private DateTime _lastSeriesClickTime;

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

                // Switch to series view if not already there
                if (Catalog.ContentType != CatalogContentType.Series)
                    Catalog.ContentType = CatalogContentType.Series;

                // Select the series - this will trigger episode loading
                _ = Catalog.Details.SelectAsync(series, _cts.Token);
            }
            catch (Exception ex)
            {
                Log("Failed to load series: " + ex.Message + "\n");
                MessageBox.Show(this, "Unable to load series details.",
                    "Series Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

}
