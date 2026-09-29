using System.Net.Http;
using System.Text.Json;
using DesktopApp.Models;

namespace DesktopApp.Services;

public interface ILiveCatalogSource
{
    Task<List<Category>> LoadCategoriesAsync(CancellationToken token);
    Task<List<Channel>> LoadChannelsAsync(Category category, CancellationToken token);
    Task<List<Channel>> LoadAllChannelsAsync(CancellationToken token);
    HashSet<int> GetFavoriteIds();
}

// Keeps account state and provider URL construction out of the page model.
public sealed class LiveCatalogSource(IChannelService channels, HttpClient http, ChannelHistoryStore? history = null) : ILiveCatalogSource
{
    private readonly ChannelHistoryStore _history = history ?? new ChannelHistoryStore();
    public const string FavoritesId = "⭐ Favorites";
    public HashSet<int> GetFavoriteIds() => Session.GetFavoriteChannels().Select(f => f.Id).ToHashSet();

    public async Task<List<Category>> LoadCategoriesAsync(CancellationToken token)
    {
        List<Category> result;
        if (Session.Mode == SessionMode.M3u)
        {
            var playlist = Session.PlaylistChannels.ToArray();
            result = await Task.Run(() => playlist.Select(p => Group(p.Category)).Distinct().OrderBy(g => g)
                .Select(g => new Category { Id = g, Name = g }).ToList(), token);
        }
        else result = await channels.LoadCategoriesAsync(token);
        return new[] { new Category { Id = FavoritesId, Name = FavoritesId } }.Concat(result).ToList();
    }

    public async Task<List<Channel>> LoadChannelsAsync(Category category, CancellationToken token)
    {
        var playlist = Session.PlaylistChannels.ToArray();
        var isPlaylist = Session.Mode == SessionMode.M3u;
        var account = CaptureAccount();
        if (category.Id == FavoritesId)
        {
            var favorites = Session.GetFavoriteChannels().ToArray();
            var result = await Task.Run(() => favorites.Select(f =>
            {
                token.ThrowIfCancellationRequested();
                var entry = isPlaylist ? playlist.FirstOrDefault(p => p.Id == f.Id) : null;
                return new Channel { Id = f.Id, Name = entry?.Name ?? f.Name, Logo = entry?.Logo ?? f.Logo,
                    EpgChannelId = entry?.TvgId ?? f.EpgChannelId, IsFavorite = true };
            }).ToList(), token);
            return await TrackAsync(account, null, result, isPlaylist, playlist, token);
        }
        var loaded = !isPlaylist ? await channels.LoadChannelsForCategoryAsync(category, token) :
            await Task.Run(() => playlist.Where(p => Group(p.Category) == category.Id)
                .Select(p => new Channel { Id = p.Id, Name = p.Name, Logo = p.Logo, EpgChannelId = p.TvgId }).ToList(), token);
        return await TrackAsync(account, "category:" + category.Id, loaded, isPlaylist, playlist, token);
    }

    public async Task<List<Channel>> LoadAllChannelsAsync(CancellationToken token)
    {
        var account = CaptureAccount();
        if (Session.Mode == SessionMode.M3u)
        {
            var playlist = Session.PlaylistChannels.ToArray();
            var result = await Task.Run(() => playlist.Select((p, i) => new Channel { Id = p.Id, Number = i + 1,
                Name = p.Name, Logo = p.Logo, EpgChannelId = p.TvgId }).ToList(), token);
            return await TrackAsync(account, "all", result, true, playlist, token);
        }
        var url = Session.BuildApi("get_live_streams");
        var loaded = await Task.Run(async () =>
        {
            var json = await http.GetStringAsync(url, token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a channel list.");
            var result = new List<Channel>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (item.ValueKind != JsonValueKind.Object || !int.TryParse(Text(item, "stream_id"), out var id)) continue;
                result.Add(new Channel { Id = id, Number = result.Count + 1, Name = Text(item, "name"),
                    Logo = Text(item, "stream_icon"), EpgChannelId = Text(item, "epg_channel_id"),
                    AddedUtc = Channel.ParseAddedDate(Text(item, "added")) });
            }
            return result;
        }, token);
        return await TrackAsync(account, "all", loaded, false, [], token);
    }

    private static string CaptureAccount() => Session.Mode == SessionMode.M3u
        ? ChannelHistoryStore.Key("m3u", Session.PlaylistSource)
        : ChannelHistoryStore.Key("xtream", Session.BaseUrl, Session.Username);

    private Task<List<Channel>> TrackAsync(string account, string? scope, List<Channel> result,
        bool isPlaylist, PlaylistEntry[] playlist, CancellationToken token) => Task.Run(() =>
    {
        // Playlist row IDs change when entries are inserted or reordered. Use the
        // stream URL for identity, which the store hashes before persistence.
        var identities = playlist.ToDictionary(p => p.Id, p => p.StreamUrl);
        _history.Apply(account, scope, result, ch => isPlaylist && identities.TryGetValue(ch.Id, out var url)
            && !string.IsNullOrEmpty(url) ? url : ch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
        return result;
    }, token);

    private static string Group(string? category) => string.IsNullOrWhiteSpace(category) ? "Other" : category;
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : string.Empty;
}
