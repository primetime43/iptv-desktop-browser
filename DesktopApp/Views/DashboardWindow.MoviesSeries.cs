using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopApp.Models;
using DesktopApp.ViewModels;

namespace DesktopApp.Views;

public partial class DashboardWindow
{


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
                _ = Catalog.SelectSeriesAsync(series);
                _lastSeriesClickTime = now;
            }
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
                _ = Catalog.SelectMovieAsync(vod);
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
                _ = Catalog.SelectSeriesAsync(series);
            }
            catch (Exception ex)
            {
                Log("Failed to load series: " + ex.Message + "\n");
                MessageBox.Show(this, "Unable to load series details.",
                    "Series Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

}
