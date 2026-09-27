using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Extensions.Logging.Abstractions;

var passed = 0;
try
{
    Session.Mode = SessionMode.Xtream;
    Session.Host = "example.test";
    Session.Port = 80;
    Session.UseSsl = false;
    Session.Username = "test-user";
    Session.Password = "secret&# +";
    Session.CachingEnabled = true;

    var categories = Create("""[{"category_id":12,"category_name":"Movies","parent_id":0},null,{"category_id":"13","category_name":"Other","parent_id":"12"},false,{}]""");
    var movieCategories = await categories.Service.LoadVodCategoriesAsync();
    Check(movieCategories.Count == 3 && movieCategories[0].CategoryId == "12" && movieCategories[0].ParentId == "0", "Numeric category identifiers and malformed rows are tolerated");
    Check(movieCategories[1].CategoryId == "13" && movieCategories[2].CategoryName == "", "String identifiers and missing optional fields survive");
    await categories.Service.LoadVodCategoriesAsync();
    Check(categories.Http.Requests == 1 && categories.Cache.LastLifetime == TimeSpan.FromHours(1), "Movie categories reuse the one-hour cache");
    var seriesCategories = await categories.Service.LoadSeriesCategoriesAsync();
    await categories.Service.LoadSeriesCategoriesAsync();
    Check(seriesCategories[0].CategoryId == "12" && categories.Http.Requests == 2, "Series categories use tolerant parsing and a separate reusable cache");

    const string moviesJson = """
        [{"stream_id":123,"name":"Movie","stream_icon":"https://images.test/a.png","plot":"Plot","cast":"Cast","director":"Director","genre":"Drama","releaseDate":"2025-01-01","duration":7200,"rating":7.5,"country":"US","added":1700000000,"container_extension":"mkv"},
         {"stream_id":"124","name":false,"rating":null,"rating_5based":4.5,"release_date":"2024","plot":{},"cast":[],"country":true},
         null, false, {"stream_id":99999999999999,"releasedate":"2023"}, {"stream_id":"125","name":"Last valid movie"}]
        """;
    var movies = Create(moviesJson);
    var first = await movies.Service.LoadVodContentAsync("cat&+#");
    Check(first.Count == 4 && first[0].Id == 123 && first[1].Id == 124 && first[2].Id == 0 && first[3].Id == 125, "Numeric/string/overflow IDs and malformed rows do not lose valid movies");
    Check(first[0].Plot == "Plot" && first[0].Cast == "Cast" && first[0].Director == "Director" && first[0].Genre == "Drama" && first[0].StreamIcon!.EndsWith("a.png"), "Movie metadata and poster URL preserved");
    Check(first[0].Rating == "7.5" && first[0].Duration == "7200" && first[0].Added == "1700000000" && first[0].ContainerExtension == "mkv", "Numeric movie metadata and playback extension preserved");
    Check(first[1].Name == "0" && first[1].Country == "1" && first[1].Plot == null && first[1].Cast == null && first[1].Rating == "4.5", "Boolean fields, malformed optional values and fallback ratings preserved");
    Check(first[0].ReleaseDate == "2025-01-01" && first[1].ReleaseDate == "2024" && first[2].ReleaseDate == "2023", "All provider release-date spellings accepted");
    Check(first.All(v => v.CategoryId == "cat&+#") && movies.Http.LastUrl!.Contains("category_id=cat%26%2B%23") && movies.Http.LastUrl.Contains("password=secret%26%23%20%2B"), "Service URL builder escapes category and credentials exactly once");
    first[0].IsSelected = true;
    first[0].DetailsLoading = true;
    first[0].DetailsLoaded = true;
    first[0].Name = "Local edit";
    var second = await movies.Service.LoadVodContentAsync("cat&+#");
    Check(movies.Http.Requests == 1 && movies.Cache.LastLifetime == TimeSpan.FromMinutes(30), "Repeat movie category uses the thirty-minute cache");
    Check(second[0].Name == "Movie" && !second[0].IsSelected && !second[0].DetailsLoading && !second[0].DetailsLoaded && !ReferenceEquals(first[0], second[0]), "Cache recreates clean UI models without mutable selection or detail state");
    Check(movies.Cache.Entries.Keys.All(k => !k.Contains(Session.Password) && !k.Contains(Session.Username)), "Cache keys contain no plaintext credentials");
    movies.Cache.Now += TimeSpan.FromMinutes(31);
    await movies.Service.LoadVodContentAsync("cat&+#");
    Check(movies.Http.Requests == 2, "Expired movie catalog refetches");
    await movies.Service.LoadVodContentAsync("other");
    Check(movies.Http.Requests == 3, "Different categories never reuse one another's responses");
    foreach (var change in new Action[] { () => Session.Port = 8080, () => Session.UseSsl = true, () => Session.Host = "second.test", () => Session.Username = "second-user", () => Session.Password = "different" })
    {
        var requests = movies.Http.Requests;
        change();
        await movies.Service.LoadVodContentAsync("other");
        Check(movies.Http.Requests == requests + 1, "Cache isolated after changing endpoint or account identity");
    }

    var series = Create(Convert.ToBase64String(Encoding.UTF8.GetBytes("""[{"series_id":5,"name":"Show","cover":"https://images.test/show.png","plot":"Plot","cast":"Cast","director":"Director","genre":"Drama","releaseDate":"2024","rating":8.2,"last_modified":1234},{"series_id":"6","releasedate":"2023","rating_5based":4}]""")));
    var shows = await series.Service.LoadSeriesContentAsync("tv");
    await series.Service.LoadSeriesContentAsync("tv");
    Check(shows.Count == 2 && shows[0].Id == 5 && shows[1].Id == 6 && shows[0].LastModified == "1234", "Base64 series responses retain numeric/string identifiers and timestamps");
    Check(shows[0].Plot == "Plot" && shows[0].Cast == "Cast" && shows[0].Director == "Director" && shows[0].Genre == "Drama" && shows[0].StreamIcon!.EndsWith("show.png"), "Series metadata and cover survive migration");
    Check(shows[0].ReleaseDate == "2024" && shows[0].Rating == "8.2" && shows[1].Rating == "4" && series.Http.Requests == 1, "Series ratings and release aliases survive cached revisits");

    var details = Create("""{"info":{"plot":"Detailed plot","cast":null,"director":{},"rating_5based":4.2,"releasedate":"2020","duration":5400,"country":"US","backdrop_path":"backdrop","youtube_trailer":"trailer","tmdb_id":123,"imdb_id":"tt123","language":"en","bitrate":5000,"video":"h264","audio":"aac","played":"2","views":30},"movie_data":[{"stream_id":"999","name":"Wrong movie"},{"stream_id":"123","name":"Detailed movie"}]}""");
    var movie = new VodContent { Id = 123, Cast = "Catalog cast", Director = "Catalog director" };
    await details.Service.LoadVodDetailsAsync(movie);
    Check(movie.DetailsLoaded && movie.Name == "Detailed movie" && movie.Plot == "Detailed plot" && movie.Cast == "Catalog cast" && movie.Director == "Catalog director", "Details merge safely and support string IDs in movie_data arrays");
    Check(movie.ReleaseDate == "2020" && movie.Duration == "5400" && movie.Country == "US" && movie.Rating == "4.2", "Movie details retain tolerant common metadata");
    Check(movie.Backdrop == "backdrop" && movie.Trailer == "trailer" && movie.TmdbId == "123" && movie.ImdbId == "tt123" && movie.Language == "en" && movie.BitRate == "5000" && movie.VideoCodec == "h264" && movie.AudioCodec == "aac" && movie.Played == 2 && movie.Views == 30, "Extended movie metadata retained");
    await details.Service.LoadVodDetailsAsync(new VodContent { Id = 123 });
    Check(details.Http.Requests == 1 && details.Cache.LastLifetime == TimeSpan.FromHours(2), "Movie details reused for fresh catalog models");
    var objectMovie = Create("""{"info":[],"movie_data":{"stream_id":123,"name":"Object format"}}""");
    await objectMovie.Service.LoadVodDetailsAsync(movie);
    Check(movie.Name == "Object format" && movie.Plot == "Detailed plot", "Object movie_data accepted without clearing existing metadata");

    var episodes = Create("""
        {"info":{"plot":"Series details","cast":null,"rating":8.5},"episodes":{
          "2":[{"id":"23","episode_num":"3","title":"Third","container_extension":"mkv","info":{"plot":"Episode plot","duration":3600,"releasedate":"2025"}},null,{"id":21,"episode_num":1,"title":"First","info":false}],
          "1":[],"bad":[],"3":null}}
        """ );
    var show = new SeriesContent { Id = 5, Cast = "Existing cast" };
    await episodes.Service.LoadSeriesDetailsAsync(show);
    Check(show.DetailsLoaded && show.Plot == "Series details" && show.Cast == "Existing cast" && show.Rating == "8.5", "Series detail metadata merges safely");
    Check(show.Seasons.Count == 2 && show.Seasons[0].SeasonNumber == 1 && show.Seasons[0].EpisodeCount == 0 && show.Seasons[1].EpisodeCount == 2, "Seasons sorted, empty seasons retained and malformed seasons skipped");
    var eps = show.Seasons[1].Episodes;
    Check(eps[0].Id == 21 && eps[0].EpisodeNumber == 1 && eps[1].Id == 23 && eps[1].EpisodeNumber == 3 && eps.All(e => e.SeriesId == 5 && e.SeasonNumber == 2), "Episodes accept mixed IDs/numbers and sort correctly");
    Check(eps[1].Plot == "Episode plot" && eps[1].Duration == "3600" && eps[1].ReleaseDate == "2025" && eps[1].ContainerExtension == "mkv" && eps[0].ContainerExtension == "mp4", "Episode metadata and playback extension preserved");
    var flat = await episodes.Service.LoadEpisodesAsync("5");
    Check(flat.Count == 2 && episodes.Http.Requests == 1 && episodes.Cache.LastLifetime == TimeSpan.FromHours(2), "Episodes and series details share one cached response");

    foreach (var payload in new[] { "not json", "{\"error\":\"Access denied\"}" })
    {
        var invalid = Create(payload);
        await Throws<JsonException>(() => invalid.Service.LoadVodContentAsync("bad"));
        Check(invalid.Cache.Writes == 0, "Malformed/error catalogs never cached as successful empty results");
        invalid.Http.Response = moviesJson;
        Check((await invalid.Service.LoadVodContentAsync("bad")).Count == 4, "Failed catalog can be retried immediately");
    }
    var invalidDetails = Create("{\"error\":\"Unavailable\"}");
    var failedMovie = new VodContent { Id = 100, Plot = "Existing" };
    await Throws<JsonException>(() => invalidDetails.Service.LoadVodDetailsAsync(failedMovie));
    Check(!failedMovie.DetailsLoaded && failedMovie.Plot == "Existing" && invalidDetails.Cache.Writes == 0, "Failed details neither mark loaded nor overwrite existing metadata");

    var canceled = Create(moviesJson);
    using (var source = new CancellationTokenSource())
    {
        source.Cancel();
        await Throws<OperationCanceledException>(() => canceled.Service.LoadVodContentAsync("x", source.Token));
        Check(canceled.Http.Requests == 0 && canceled.Cache.Reads == 0, "Already-canceled requests do no work");
    }
    using (var source = new CancellationTokenSource())
    {
        canceled.Http.BeforeReturn = source.Cancel; // Simulate an HTTP implementation ignoring cancellation.
        await Throws<OperationCanceledException>(() => canceled.Service.LoadVodContentAsync("x", source.Token));
        Check(canceled.Cache.Writes == 0, "Canceled HTTP completion cannot populate cache or UI");
    }
    using (var source = new CancellationTokenSource())
    {
        details.Cache.BeforeReturn = source.Cancel;
        var target = new VodContent { Id = 123 };
        await Throws<OperationCanceledException>(() => details.Service.LoadVodDetailsAsync(target, source.Token));
        Check(!target.DetailsLoaded && target.Plot == null, "Canceled cache hits cannot apply detail state");
    }

    Session.CachingEnabled = false;
    var noCache = Create(moviesJson);
    await noCache.Service.LoadVodContentAsync("x");
    await noCache.Service.LoadVodContentAsync("x");
    Check(noCache.Http.Requests == 2 && noCache.Cache.Reads == 0 && noCache.Cache.Writes == 0, "Caching preference is respected");
    Session.Mode = SessionMode.M3u;
    Session.VodCategories.Add(new VodCategory { CategoryId = "local" });
    Session.SeriesCategories.Add(new SeriesCategory { CategoryId = "local" });
    Session.VodContent.AddRange([new VodContent { Id = 1, CategoryId = "local" }, new VodContent { Id = 2, CategoryId = "other" }]);
    Session.SeriesContent.Add(new SeriesContent { Id = 3, CategoryId = "local" });
    var local = Create("invalid");
    Check((await local.Service.LoadVodCategoriesAsync()).Count == 1 && (await local.Service.LoadSeriesCategoriesAsync()).Count == 1, "M3U category paths retained");
    Check((await local.Service.LoadVodContentAsync("local")).Single().Id == 1 && (await local.Service.LoadSeriesContentAsync("local")).Single().Id == 3 && local.Http.Requests == 0 && local.Cache.Reads == 0, "M3U catalogs filter local session data without network/cache access");
    Console.WriteLine($"Passed {passed} VOD regression checks.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

Harness Create(string response)
{
    var http = new FakeHttp { Response = response };
    var cache = new JsonCache();
    return new(new VodService(new SessionService(NullLogger<SessionService>.Instance), http, cache, NullLogger<VodService>.Instance), http, cache);
}
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    passed++;
}
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { passed++; return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

sealed record Harness(VodService Service, FakeHttp Http, JsonCache Cache);
sealed class FakeHttp : IHttpService
{
    public string Response { get; set; } = "";
    public int Requests { get; private set; }
    public string? LastUrl { get; private set; }
    public Action? BeforeReturn { get; set; }
    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        Requests++;
        LastUrl = url;
        BeforeReturn?.Invoke();
        return Task.FromResult(Response);
    }
    public Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

// Round-trip through JSON on reads to exercise the same data contract as disk caching.
sealed class JsonCache : ICacheService
{
    public Dictionary<string, (string Json, DateTimeOffset Expires)> Entries { get; } = new();
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public TimeSpan? LastLifetime { get; private set; }
    public Action? BeforeReturn { get; set; }
    public Task<T?> GetDataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        Reads++;
        BeforeReturn?.Invoke();
        return Task.FromResult(Entries.TryGetValue(key, out var entry) && entry.Expires > Now ? JsonSerializer.Deserialize<T>(entry.Json) : null);
    }
    public Task SetDataAsync<T>(string key, T data, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class
    {
        Entries[key] = (JsonSerializer.Serialize(data), Now + (expiration ?? TimeSpan.FromMinutes(30)));
        Writes++;
        LastLifetime = expiration;
        return Task.CompletedTask;
    }
    public Task<BitmapImage?> GetImageAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BitmapImage?> GetChannelLogoAsync(int channelId, string logoUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void ClearImageCache() { }
    public Task<bool> HasDataAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Entries.ContainsKey(key));
    public Task RemoveDataAsync(string key, CancellationToken cancellationToken = default) { Entries.Remove(key); return Task.CompletedTask; }
    public Task ClearExpiredDataAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearAllDataAsync(CancellationToken cancellationToken = default) { Entries.Clear(); return Task.CompletedTask; }
    public int ImageCacheCount => 0;
    public int DataCacheCount => Entries.Count;
    public long EstimatedMemoryUsage => 0;
    public event Action<string>? CacheOperationStatusChanged { add { } remove { } }
    public string CurrentCacheStatus => "Ready";
}
