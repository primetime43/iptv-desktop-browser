using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopApp.Models;

namespace DesktopApp.Views;

public partial class VodWindow : Window
{
    private bool _isMoviesMode = true;
    private DateTime _lastVodClickTime;
    private DateTime _lastSeriesClickTime;

    public VodWindow()
    {
        InitializeComponent();
        UpdateContentTypeView();
    }

    public VodWindow(object dataContext) : this()
    {
        DataContext = dataContext;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Set DataContext from owner if not already set
        if (DataContext == null && Owner is DashboardWindow dash)
        {
            DataContext = dash.DataContext;
        }

        // Subscribe to series selection changes
        if (DataContext is DashboardWindow dashboard)
        {
            dashboard.PropertyChanged += Dashboard_PropertyChanged;
        }

        // Now that DataContext is set, update the content type view to initialize bindings
        UpdateContentTypeView();
    }

    private SeriesContent? _currentSubscribedSeries;

    private void Dashboard_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isMoviesMode && DataContext is DashboardWindow dashboard)
        {
            if (e.PropertyName == nameof(DashboardWindow.SelectedSeriesContent))
            {
                // Unsubscribe from previous series if any
                if (_currentSubscribedSeries != null)
                {
                    _currentSubscribedSeries.PropertyChanged -= Series_PropertyChanged;
                }

                // Subscribe to new series property changes
                _currentSubscribedSeries = dashboard.SelectedSeriesContent;
                if (_currentSubscribedSeries != null)
                {
                    _currentSubscribedSeries.PropertyChanged += Series_PropertyChanged;
                }

                // Start episode refresh, might need to wait for details to load
                RefreshSeriesEpisodes();
            }
        }
    }

    private void Series_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SeriesContent.DetailsLoaded) &&
            sender is SeriesContent series && series.DetailsLoaded)
        {
            // Details have been loaded, refresh episodes UI
            Dispatcher.Invoke(() => RefreshSeriesEpisodes());
        }
    }

    private void VodContent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not VodContent vod)
            return;

        if (DataContext is DashboardWindow dashboard)
        {
            // Check for double-click to launch player
            var now = DateTime.UtcNow;
            const int doubleClickMs = 400;

            if (dashboard.SelectedVodContent == vod && (now - _lastVodClickTime).TotalMilliseconds <= doubleClickMs)
            {
                // Double-click: launch player
                if (Owner is DashboardWindow dash)
                {
                    dash.GetType().GetMethod("TryLaunchVodInPlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                        .Invoke(dash, new object[] { vod });
                }
                _lastVodClickTime = DateTime.MinValue;
            }
            else
            {
                // Single-click: select VOD content
                dashboard.SelectedVodContent = vod;
                _lastVodClickTime = now;
            }
        }
    }

    private void MoviesTab_Click(object sender, RoutedEventArgs e)
    {
        _isMoviesMode = true;
        UpdateContentTypeView();
    }

    private void SeriesTab_Click(object sender, RoutedEventArgs e)
    {
        _isMoviesMode = false;
        UpdateContentTypeView();
    }

    private void UpdateContentTypeView()
    {
        if (DataContext is not DashboardWindow dashboard) return;

        if (_isMoviesMode)
        {
            MoviesView.Visibility = Visibility.Visible;
            SeriesView.Visibility = Visibility.Collapsed;
            MoviesTabBtn.Background = new SolidColorBrush(Color.FromRgb(0x34, 0x7D, 0xFF));
            MoviesTabBtn.Foreground = Brushes.White;
            SeriesTabBtn.Background = Brushes.Transparent;
            SeriesTabBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE6, 0xF2));
            ContentTypeLabel.Text = "Movies:";

            // Bind to VOD categories and content count
            CategoryCombo.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("VodCategories"));
            CategoryCombo.SetBinding(Selector.SelectedValueProperty, new System.Windows.Data.Binding("SelectedVodCategoryId"));
            ContentCountLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("VodCountText"));
        }
        else
        {
            MoviesView.Visibility = Visibility.Collapsed;
            SeriesView.Visibility = Visibility.Visible;
            SeriesTabBtn.Background = new SolidColorBrush(Color.FromRgb(0x34, 0x7D, 0xFF));
            SeriesTabBtn.Foreground = Brushes.White;
            MoviesTabBtn.Background = Brushes.Transparent;
            MoviesTabBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE6, 0xF2));
            ContentTypeLabel.Text = "TV Shows:";

            // Bind to Series categories and content count
            CategoryCombo.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("SeriesCategories"));
            CategoryCombo.SetBinding(Selector.SelectedValueProperty, new System.Windows.Data.Binding("SelectedSeriesCategoryId"));
            ContentCountLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SeriesCountText"));

            // Refresh episodes if a series is already selected
            RefreshSeriesEpisodes();
        }
    }

    private void SeriesContent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SeriesContent series)
            return;

        if (DataContext is DashboardWindow dashboard)
        {
            // Check for double-click to launch first episode
            var now = DateTime.UtcNow;
            const int doubleClickMs = 400;

            if (dashboard.SelectedSeriesContent == series && (now - _lastSeriesClickTime).TotalMilliseconds <= doubleClickMs)
            {
                // Double-click: play first available episode
                PlayFirstEpisode(series);
                _lastSeriesClickTime = DateTime.MinValue;
            }
            else
            {
                // Single-click: select series and show metadata + episodes
                dashboard.SelectedSeriesContent = series;
                _lastSeriesClickTime = now;

                // This will trigger LoadSeriesDetailsAsync through the property setter
                // which will load both metadata and episodes
            }
        }
    }

    private void PlayFirstEpisode(SeriesContent series)
    {
        // Play first available episode
        if (series.Seasons.Any() && series.Seasons.First().Episodes.Any())
        {
            var firstEpisode = series.Seasons.First().Episodes.First();
            PlayEpisode(firstEpisode);
        }
        // If no episodes are loaded yet, they will be loaded by the property setter
        // and we can try again when LoadSeriesEpisodes is called
    }

    private void RefreshSeriesEpisodes()
    {
        if (DataContext is not DashboardWindow dashboard || dashboard.SelectedSeriesContent == null)
        {
            SeasonsPanel.Children.Clear();
            return;
        }

        var series = dashboard.SelectedSeriesContent;

        // Only populate if details are already loaded, otherwise wait for DetailsLoaded event
        if (series.DetailsLoaded && series.Seasons.Any())
        {
            PopulateEpisodesUI(series);
        }
        else
        {
            // Clear existing episodes since details aren't loaded yet
            SeasonsPanel.Children.Clear();
        }
    }

    private void PopulateEpisodesUI(SeriesContent series)
    {
        // Clear existing seasons panel
        SeasonsPanel.Children.Clear();

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
            SeasonsPanel.Children.Add(seasonHeader);

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

                episodeButton.Click += (s, e) => PlayEpisode(episode);
                SeasonsPanel.Children.Add(episodeButton);
            }
        }
    }

    private void PlayEpisode(EpisodeContent episode)
    {
        if (Owner is DashboardWindow dash)
        {
            dash.GetType().GetMethod("TryLaunchEpisodeInPlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                .Invoke(dash, new object[] { episode });
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardWindow dashboard && dashboard.SelectedVodContent != null)
        {
            if (Owner is DashboardWindow dash)
            {
                dash.GetType().GetMethod("TryLaunchVodInPlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                    .Invoke(dash, new object[] { dashboard.SelectedVodContent });
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Unsubscribe from events
        if (DataContext is DashboardWindow dashboard)
        {
            dashboard.PropertyChanged -= Dashboard_PropertyChanged;
        }

        if (_currentSubscribedSeries != null)
        {
            _currentSubscribedSeries.PropertyChanged -= Series_PropertyChanged;
        }

        base.OnClosed(e);
    }
}
