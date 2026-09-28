using System.Diagnostics;
using System.Windows;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

namespace DesktopApp.Views;

public partial class DashboardWindow
{
    private PlaybackManifestServer? _playbackManifests;

    internal void ChooseChannelQuality_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedChannel is not { } channel) return;
        var original = ChannelPlaybackUrl(channel);
        var discovery = Session.Mode == SessionMode.Xtream ? Session.BuildStreamUrl(channel.Id, "m3u8") : original;
        PlayStream(original, channel.Name, discovery, () => ReferenceEquals(SelectedChannel, channel));
    }

    private static string ChannelPlaybackUrl(Channel channel) => Session.Mode == SessionMode.M3u
        ? Session.PlaylistChannels.FirstOrDefault(p => p.Id == channel.Id)?.StreamUrl ?? string.Empty
        : Session.BuildStreamUrl(channel.Id, "ts");

    private void ChooseMovieQuality(VodContent movie)
    {
        var url = Session.BuildVodStreamUrl(movie.Id, string.IsNullOrEmpty(movie.ContainerExtension) ? "mp4" : movie.ContainerExtension);
        PlayStream(url, movie.Name, url, () => ReferenceEquals(Catalog.SelectedMovie, movie));
    }

    private void ChooseEpisodeQuality(EpisodeContent episode)
    {
        var series = Catalog.SelectedSeries;
        var url = Session.BuildSeriesStreamUrl(episode.Id, string.IsNullOrEmpty(episode.ContainerExtension) ? "mp4" : episode.ContainerExtension);
        PlayStream(url, episode.DisplayTitle, url, () => series != null && ReferenceEquals(Catalog.SelectedSeries, series));
    }

    // All playback entry points use the same player launcher. Only an explicit
    // quality action performs discovery, so normal playback adds no network delay.
    private void PlayStream(string original, string title, string? discovery = null, Func<bool>? isCurrent = null)
    {
        if (_isClosing) return;
        try
        {
            if (string.IsNullOrWhiteSpace(original)) throw new InvalidOperationException("Stream URL not found.");
            var url = original;
            if (discovery != null)
            {
                if (!Uri.TryCreate(original, UriKind.Absolute, out var originalUri) ||
                    !Uri.TryCreate(discovery, UriKind.Absolute, out var sourceUri)) throw new InvalidOperationException("Invalid stream URL.");
                using var model = new PlaybackQualityViewModel(title, originalUri, sourceUri, new StreamQualityService(_http));
                if (new PlaybackQualityWindow(model) { Owner = this }.ShowDialog() != true || model.Result is not { } choice ||
                    _isClosing || isCurrent?.Invoke() == false) return;
                url = choice.Manifest != null
                    ? (_playbackManifests ??= new PlaybackManifestServer()).Publish(choice.Manifest).AbsoluteUri
                    : choice.Uri.AbsoluteUri;
            }
            var process = Session.BuildPlayerProcess(url, title);
            if (string.IsNullOrWhiteSpace(process.FileName))
            {
                MessageBox.Show(this, "Player executable not set. Open Settings and configure a path.", "Player Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Process.Start(process);
            Log($"Launching player: {Session.PreferredPlayer}\n");
        }
        catch (Exception ex)
        {
            Log($"Failed to launch player: {ex.Message}\n");
            MessageBox.Show(this, "Unable to start playback. Check player settings or try the original stream.", "Player Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
