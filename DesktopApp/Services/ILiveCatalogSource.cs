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
public sealed class LiveCatalogSource(IChannelService channels, HttpClient http) : ILiveCatalogSource
{
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
        if (category.Id == FavoritesId)
        {
            var favorites = Session.GetFavoriteChannels().ToArray();
            return await Task.Run(() => favorites.Select(f =>
            {
                token.ThrowIfCancellationRequested();
                var entry = isPlaylist ? playlist.FirstOrDefault(p => p.Id == f.Id) : null;
                return new Channel { Id = f.Id, Name = entry?.Name ?? f.Name, Logo = entry?.Logo ?? f.Logo,
                    EpgChannelId = entry?.TvgId ?? f.EpgChannelId, IsFavorite = true };
            }).ToList(), token);
        }
        if (!isPlaylist) return await channels.LoadChannelsForCategoryAsync(category, token);
        return await Task.Run(() => playlist.Where(p => Group(p.Category) == category.Id)
            .Select(p => new Channel { Id = p.Id, Name = p.Name, Logo = p.Logo, EpgChannelId = p.TvgId }).ToList(), token);
    }

    public Task<List<Channel>> LoadAllChannelsAsync(CancellationToken token)
    {
        if (Session.Mode == SessionMode.M3u)
        {
            var playlist = Session.PlaylistChannels.ToArray();
            return Task.Run(() => playlist.Select((p, i) => new Channel { Id = p.Id, Number = i + 1,
                Name = p.Name, Logo = p.Logo, EpgChannelId = p.TvgId }).ToList(), token);
        }
        var url = Session.BuildApi("get_live_streams");
        return Task.Run(async () =>
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
                    Logo = Text(item, "stream_icon"), EpgChannelId = Text(item, "epg_channel_id") });
            }
            return result;
        }, token);
    }

    private static string Group(string? category) => string.IsNullOrWhiteSpace(category) ? "Other" : category;
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : string.Empty;
}
