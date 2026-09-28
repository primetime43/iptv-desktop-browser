using DesktopApp.Controls;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
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
        var controls = Navigation.ActivePage switch
        {
            DashboardPage.LiveTv => new[] { "ChannelsGridView", "ChannelsListView" },
            DashboardPage.Favorites => ["FavoritesChannelsControl"],
            DashboardPage.Vod when !IsSeriesCatalog => ["MoviesGridView", "MoviesListView"],
            DashboardPage.Vod => ["SeriesGridView", "SeriesListView"],
            _ => []
        };
        foreach (var name in controls)
        {
            if (name.StartsWith("Channels") && LiveTv.IsLoadingChannels) continue;
            if ((name.StartsWith("Movies") && IsLoadingVodContent) || (name.StartsWith("Series") && IsLoadingSeriesContent)) continue;
            if (FindDashboardElement(name) is ItemsControl control)
                items.AddRange(CatalogViewport.Read(control).Where(i => Navigation.ActivePage == DashboardPage.Favorites || i.Item switch
                {
                    Channel channel => LiveTv.ChannelsView.Contains(channel),
                    VodContent movie => Catalog.MoviesView.Contains(movie),
                    SeriesContent series => Catalog.SeriesView.Contains(series),
                    _ => false
                }));
        }
        if (Navigation.ActivePage == DashboardPage.LiveTv && !LiveTv.IsLoadingChannels && SelectedChannel != null)
            AddSelected(SelectedChannel);
        if (Navigation.ActivePage == DashboardPage.Vod && !IsSeriesCatalog && !IsLoadingVodContent && SelectedVodContent != null)
            AddSelected(SelectedVodContent);
        if (Navigation.ActivePage == DashboardPage.Vod && IsSeriesCatalog && !IsLoadingSeriesContent && SelectedSeriesContent != null)
            AddSelected(SelectedSeriesContent);
        return items.OrderBy(i => i.Priority).DistinctBy(i => i.Item).ToList();

        void AddSelected(object selected)
        {
            var visible = items.FirstOrDefault(i => ReferenceEquals(i.Item, selected));
            items.Add(visible != null ? visible with { Priority = 0 } : new(selected, 0));
        }
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
        _upcomingEntries.ReplaceAll(upcoming);
    }

    private void StopCatalogLoading()
    {
        LayoutUpdated -= OnCatalogLayoutUpdated;
        _catalogRefreshTimer?.Stop();
        _catalogResources?.Dispose();
    }
}
