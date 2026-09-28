using System.Diagnostics;
using System.Windows.Controls;
using DesktopApp.Models;

namespace DesktopApp.Views;

public partial class DashboardWindow
{
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

}
