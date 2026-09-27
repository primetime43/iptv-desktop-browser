using System.Globalization;
using System.Text.Json;
using DesktopApp.Models;

namespace DesktopApp.Services;

public partial class VodService
{
    private static JsonElement Property(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;

    // Xtream providers vary scalar types; malformed optional metadata must not discard a catalog.
    private static string? Text(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Property(parent, name);
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => null
            };
            if (text != null) return text;
        }
        return null;
    }

    private static int? Number(JsonElement parent, string name) =>
        int.TryParse(Text(parent, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Null) return [];
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a catalog array.");
        return root.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object);
    }

    private static List<VodCategory> ParseVodCategories(JsonElement root) => Rows(root).Select(row => new VodCategory
    {
        CategoryId = Text(row, "category_id") ?? "",
        CategoryName = Text(row, "category_name") ?? "",
        ParentId = Text(row, "parent_id")
    }).ToList();

    private static List<SeriesCategory> ParseSeriesCategories(JsonElement root) => Rows(root).Select(row => new SeriesCategory
    {
        CategoryId = Text(row, "category_id") ?? "",
        CategoryName = Text(row, "category_name") ?? "",
        ParentId = Text(row, "parent_id")
    }).ToList();

    private static List<VodContent> ParseVodContent(JsonElement root, string categoryId) => Rows(root).Select(row => new VodContent
    {
        Id = Number(row, "stream_id") ?? 0,
        Name = Text(row, "name") ?? "",
        CategoryId = categoryId,
        StreamIcon = Text(row, "stream_icon"),
        Plot = Text(row, "plot"),
        Cast = Text(row, "cast"),
        Director = Text(row, "director"),
        Genre = Text(row, "genre"),
        ReleaseDate = Text(row, "releaseDate", "release_date", "releasedate"),
        Duration = Text(row, "duration"),
        Rating = Text(row, "rating", "rating_5based"),
        Country = Text(row, "country"),
        Added = Text(row, "added"),
        ContainerExtension = Text(row, "container_extension") ?? "mp4"
    }).ToList();

    private static List<SeriesContent> ParseSeriesContent(JsonElement root, string categoryId) => Rows(root).Select(row => new SeriesContent
    {
        Id = Number(row, "series_id") ?? 0,
        Name = Text(row, "name") ?? "",
        CategoryId = categoryId,
        StreamIcon = Text(row, "cover"),
        Plot = Text(row, "plot"),
        Cast = Text(row, "cast"),
        Director = Text(row, "director"),
        Genre = Text(row, "genre"),
        ReleaseDate = Text(row, "releaseDate", "release_date", "releasedate"),
        Rating = Text(row, "rating", "rating_5based"),
        LastModified = Text(row, "last_modified")
    }).ToList();

    private static VodContent ParseVodDetails(JsonElement root, int id)
    {
        var info = Property(root, "info");
        var movieData = Property(root, "movie_data");
        if (root.ValueKind != JsonValueKind.Object ||
            (info.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) &&
             movieData.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
            throw new JsonException("Expected movie details.");
        var details = new VodContent
        {
            Id = id,
            Plot = Text(info, "plot"),
            Cast = Text(info, "cast"),
            Director = Text(info, "director"),
            Genre = Text(info, "genre"),
            ReleaseDate = Text(info, "releasedate", "release_date", "releaseDate"),
            Rating = Text(info, "rating", "rating_5based"),
            Duration = Text(info, "duration"),
            Country = Text(info, "country"),
            Backdrop = Text(info, "backdrop_path"),
            Trailer = Text(info, "youtube_trailer"),
            TmdbId = Text(info, "tmdb_id"),
            ImdbId = Text(info, "imdb_id"),
            Language = Text(info, "language"),
            BitRate = Text(info, "bitrate"),
            VideoCodec = Text(info, "video"),
            AudioCodec = Text(info, "audio"),
            Played = Number(info, "played"),
            Views = Number(info, "views")
        };
        var movies = movieData.ValueKind == JsonValueKind.Array ? Rows(movieData) : new[] { movieData };
        foreach (var movie in movies)
        {
            if (Number(movie, "stream_id") != id) continue;
            details.Name = Text(movie, "name") ?? "";
            break;
        }
        return details;
    }

    private static SeriesContent ParseSeriesDetails(JsonElement root, int id)
    {
        var info = Property(root, "info");
        var episodes = Property(root, "episodes");
        if (root.ValueKind != JsonValueKind.Object ||
            (info.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) &&
             episodes.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
            throw new JsonException("Expected series details.");
        var details = new SeriesContent
        {
            Id = id,
            Plot = Text(info, "plot"),
            Cast = Text(info, "cast"),
            Director = Text(info, "director"),
            Genre = Text(info, "genre"),
            Rating = Text(info, "rating", "rating_5based")
        };
        if (episodes.ValueKind != JsonValueKind.Object) return details;
        foreach (var season in episodes.EnumerateObject())
        {
            if (!int.TryParse(season.Name, out var seasonNumber) || season.Value.ValueKind != JsonValueKind.Array) continue;
            var items = Rows(season.Value).Select(row => new EpisodeContent
            {
                Id = Number(row, "id") ?? 0,
                SeriesId = id,
                SeasonNumber = seasonNumber,
                EpisodeNumber = Number(row, "episode_num") ?? 0,
                Name = Text(row, "title") ?? "",
                Plot = Text(Property(row, "info"), "plot"),
                Duration = Text(Property(row, "info"), "duration"),
                ReleaseDate = Text(Property(row, "info"), "releasedate", "release_date", "releaseDate"),
                ContainerExtension = Text(row, "container_extension") ?? "mp4"
            }).OrderBy(e => e.EpisodeNumber).ToList();
            details.Seasons.Add(new SeasonInfo { SeasonNumber = seasonNumber, Episodes = items, EpisodeCount = items.Count });
        }
        details.Seasons = details.Seasons.OrderBy(s => s.SeasonNumber).ToList();
        return details;
    }
}
