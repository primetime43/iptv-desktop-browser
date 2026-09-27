using DesktopApp.Controls;
using DesktopApp.Models;
using DesktopApp.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DesktopApp.Views;

public partial class DashboardWindow
{
    private CatalogResourceLoader? _catalogResources;
    private DispatcherTimer? _catalogRefreshTimer;

    private void InitializeCatalogLoading()
    {
        _catalogResources = new CatalogResourceLoader(_channelService, _cacheService, channel =>
        {
            if (channel.EpgSchedule != null && Session.LastEpgUpdateUtc == null)
            {
                Session.LastEpgUpdateUtc = DateTime.UtcNow;
                LastEpgUpdateText = Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g");
            }
            if (ReferenceEquals(channel, SelectedChannel))
            {
                UpdateSelectedGuide();
                var programs = channel.EpgSchedule?.Programs;
                if (programs != null) _scheduler.CheckForNewEpisodes(channel.Id, programs);
            }
        }, _cts.Token);
        _catalogRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(75) };
        _catalogRefreshTimer.Tick += (_, _) =>
        {
            _catalogRefreshTimer.Stop();
            RefreshCatalogResources();
        };
        LayoutUpdated += OnCatalogLayoutUpdated;
    }

    private void OnCatalogLayoutUpdated(object? sender, EventArgs e) => ScheduleCatalogRefresh();

    private void ScheduleCatalogRefresh()
    {
        if (!_isClosing && !_cts.IsCancellationRequested && _catalogRefreshTimer?.IsEnabled == false)
            _catalogRefreshTimer.Start();
    }

    private List<CatalogViewportItem> ReadCatalogViewport()
    {
        var items = new List<CatalogViewportItem>();
        foreach (var name in new[] { "ChannelsGridView", "ChannelsListView", "MoviesGridView", "MoviesListView", "SeriesGridView", "SeriesListView", "FavoritesChannelsControl" })
        {
            if ((name.StartsWith("Movies") && IsLoadingVodContent) || (name.StartsWith("Series") && IsLoadingSeriesContent)) continue;
            if (FindName(name) is ItemsControl control) items.AddRange(CatalogViewport.Read(control));
        }
        if ((ChannelsGridView.IsVisible || ChannelsListView.IsVisible) && SelectedChannel != null)
            items.Add(new(SelectedChannel, 0));
        if (!IsLoadingVodContent && (MoviesGridView.IsVisible || MoviesListView.IsVisible) && SelectedVodContent != null)
            items.Add(new(SelectedVodContent, 0));
        if (!IsLoadingSeriesContent && (SeriesGridView.IsVisible || SeriesListView.IsVisible) && SelectedSeriesContent != null)
            items.Add(new(SelectedSeriesContent, 0));
        return items.OrderBy(i => i.Priority).DistinctBy(i => i.Item).ToList();
    }

    private void RefreshCatalogResources()
    {
        if (_isClosing || _cts.IsCancellationRequested) return;
        _catalogResources?.Update(ReadCatalogViewport(),
            $"{Session.Mode}:{Session.BaseUrl}:{Session.Username}", Session.Mode == SessionMode.Xtream);
        UpdateSelectedGuide();
    }

    private void UpdateSelectedGuide()
    {
        if (Session.Mode != SessionMode.Xtream) return;
        UpdateSelectedNowPlaying();
        var now = DateTime.UtcNow;
        var upcoming = SelectedChannel?.EpgSchedule is { } schedule && schedule.IsStillValid(now)
            ? schedule.Programs!.Where(p => p.StartUtc > now).OrderBy(p => p.StartUtc).Take(10).ToList() : [];
        if (_upcomingEntries.SequenceEqual(upcoming)) return;
        _upcomingEntries.Clear();
        foreach (var program in upcoming) _upcomingEntries.Add(program);
    }

    private void StopCatalogLoading()
    {
        LayoutUpdated -= OnCatalogLayoutUpdated;
        _catalogRefreshTimer?.Stop();
        _catalogResources?.Dispose();
    }
}
