using DesktopApp.Controls;
using DesktopApp.Models;
using System.Windows.Media.Imaging;
using System.Runtime.CompilerServices;

namespace DesktopApp.Services;

// Shares results between cards representing the same channel/image. All model
// changes happen on the UI context; disk access, decoding and parsing run off it.
public sealed class CatalogResourceLoader : IDisposable
{
    private sealed record GuideResult(EpgData? Schedule, DateTime? RetryAt);
    private readonly IChannelService _channels;
    private readonly ICacheService _cache;
    private readonly PrioritizedLoadQueue _queue;
    private readonly Action<Channel> _guideUpdated;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, GuideResult> _guides = new();
    private readonly Dictionary<string, DateTime> _imageRetry = new();
    private readonly ConditionalWeakTable<object, ImageState> _images = new();
    private sealed class ImageState { public string? Key; }
    private HashSet<Channel> _loading = [];
    private string? _account;

    public CatalogResourceLoader(IChannelService channels, ICacheService cache, Action<Channel> guideUpdated,
        CancellationToken lifetimeToken, TimeProvider? clock = null)
    {
        _channels = channels;
        _cache = cache;
        _guideUpdated = guideUpdated;
        _clock = clock ?? TimeProvider.System;
        _queue = new PrioritizedLoadQueue(4, 128, lifetimeToken);
    }

    public void Update(IEnumerable<CatalogViewportItem> items, string account, bool xtream)
    {
        if (_account != account)
        {
            Cancel();
            _guides.Clear();
            _imageRetry.Clear();
            _account = account;
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        var requests = new List<ViewportLoadRequest>();
        var loading = new HashSet<Channel>();
        foreach (var entry in items.OrderBy(i => i.Priority).DistinctBy(i => i.Item))
        {
            switch (entry.Item)
            {
                case Channel channel:
                    AddImage(entry, channel.Logo, channel.LogoImage, image => channel.LogoImage = image);
                    if (!xtream) break;
                    var key = account + ":epg:" + channel.Id;
                    if (_guides.TryGetValue(key, out var guide) &&
                        (guide.Schedule?.IsStillValid(now) == true || guide.RetryAt > now))
                    {
                        channel.EpgSchedule = guide.Schedule;
                        channel.NextEpgRefreshUtc = guide.RetryAt;
                    }
                    channel.RefreshCurrentProgram(now);
                    if (channel.EpgSchedule?.IsStillValid(now) == true || channel.NextEpgRefreshUtc > now) break;
                    // Selecting a channel can fetch its guide by stream ID even if
                    // the provider omitted the optional EPG identifier.
                    if (string.IsNullOrEmpty(channel.EpgChannelId) && entry.Priority != 0) break;
                    var snapshot = new Channel
                    {
                        Id = channel.Id, Name = channel.Name, EpgChannelId = channel.EpgChannelId,
                        EpgSchedule = channel.EpgSchedule
                    };
                    requests.Add(new(key, entry.Priority,
                        async token => await Task.Run(async () =>
                        {
                            token.ThrowIfCancellationRequested();
                            await _channels.LoadEpgForChannelAsync(snapshot, token);
                            token.ThrowIfCancellationRequested();
                            return (object)new GuideResult(snapshot.EpgSchedule, snapshot.NextEpgRefreshUtc);
                        }, token),
                        result =>
                        {
                            var loaded = result as GuideResult ?? new(null, null);
                            loaded = loaded with { RetryAt = loaded.RetryAt ?? loaded.Schedule?.ExpiresUtc ?? _clock.GetUtcNow().UtcDateTime.AddMinutes(1) };
                            _guides[key] = loaded;
                            Trim(_guides);
                            channel.EpgSchedule = loaded.Schedule;
                            channel.NextEpgRefreshUtc = loaded.RetryAt;
                            channel.RefreshCurrentProgram(_clock.GetUtcNow().UtcDateTime);
                            channel.EpgLoading = false;
                            _loading.Remove(channel);
                            _guideUpdated(channel);
                        }));
                    loading.Add(channel);
                    break;
                case VodContent movie:
                    AddImage(entry, movie.StreamIcon, movie.PosterImage, image => movie.PosterImage = image);
                    break;
                case SeriesContent series:
                    AddImage(entry, series.StreamIcon, series.PosterImage, image => series.PosterImage = image);
                    break;
            }
        }
        foreach (var channel in _loading.Except(loading)) channel.EpgLoading = false;
        foreach (var channel in loading) channel.EpgLoading = true;
        _loading = loading;
        _queue.Replace(requests);

        void AddImage(CatalogViewportItem entry, string? url, BitmapImage? current, Action<BitmapImage> apply)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var size = ThumbnailSize.Create(entry.ImageWidth, entry.ImageHeight);
            var key = $"{account}:image:{url}:{size.Width}x{size.Height}";
            var state = _images.GetOrCreateValue(entry.Item);
            if (current != null && state.Key == key) return;
            if (_imageRetry.TryGetValue(key, out var retry) && retry > now) return;
            requests.Add(new(key, entry.Priority,
                async token => await Task.Run(async () => (object?)await _cache.GetImageAsync(url, size.Width, size.Height, token), token),
                result =>
                {
                    if (result is BitmapImage image) { state.Key = key; apply(image); }
                    else { _imageRetry[key] = _clock.GetUtcNow().UtcDateTime.AddMinutes(1); Trim(_imageRetry); }
                }));
        }
    }

    private static void Trim<T>(Dictionary<string, T> entries)
    {
        while (entries.Count > 512) entries.Remove(entries.Keys.First());
    }

    public void Cancel()
    {
        _queue.Cancel();
        foreach (var channel in _loading) channel.EpgLoading = false;
        _loading.Clear();
    }

    public void Dispose() { Cancel(); _queue.Dispose(); }
}
