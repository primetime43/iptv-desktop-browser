using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using DesktopApp.Configuration;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Extensions.Logging.Abstractions;

Session.Mode = SessionMode.Xtream;
Session.Host = "example.test";
Session.Username = "test-user";
Session.CachingEnabled = true;
var passed = 0;
var origin = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

var h = Create();
h.Http.Response = Guide(
    Program("Later show", 20, 90, numeric: true),
    Program("Morning show", -10, 5),
    Program("Next show", 5, 15, numeric: true));
await h.Service.LoadEpgForChannelAsync(h.Channel);
Check(h.Channel.NowTitle == "Morning show" && h.Channel.NowDescription == "Morning show details", "Initial current program and description");
Check(h.Channel.EpgSchedule!.Programs!.Count == 3 && h.Cache.Writes == 1, "Full schedule is cached");
Check(h.Channel.EpgSchedule.ExpiresUtc > origin.AddMinutes(5).UtcDateTime, "Guide can remain cached beyond the current show");
var changed = new List<string?>();
h.Channel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
h.Clock.Now = origin.AddMinutes(5);
h.Channel.RefreshCurrentProgram(h.Clock.Now.UtcDateTime);
Check(h.Channel.NowTitle == "Next show" && h.Channel.NowDescription == "Next show details", "Display advances at the exact show boundary");
Check(changed.Contains(nameof(Channel.NowTitle)) && changed.Contains(nameof(Channel.NowTimeRange)), "Transition notifies UI bindings");
await h.Service.LoadEpgForChannelAsync(h.Channel);
Check(h.Http.Requests == 1, "Advancing programs uses the loaded guide without another request");

var reopened = new Channel { Id = h.Channel.Id, EpgChannelId = "epg-id" };
await h.Service.LoadEpgForChannelAsync(reopened);
Check(reopened.NowTitle == "Next show" && h.Http.Requests == 1, "Serialized cache selects the current program after reopening");
h.Clock.Now = origin.AddMinutes(15);
reopened.RefreshCurrentProgram(h.Clock.Now.UtcDateTime);
Check(reopened.NowTitle == null && reopened.NowDescription == null && reopened.NowTimeRange == null,
    "Guide gaps clear the old show rather than labeling a future show as current");
h.Clock.Now = origin.AddMinutes(20);
reopened.RefreshCurrentProgram(h.Clock.Now.UtcDateTime);
Check(reopened.NowTitle == "Later show", "Current display resumes at a future show's start");

var expiry = h.Channel.EpgSchedule.ExpiresUtc;
h.Clock.Now = new DateTimeOffset(expiry);
await h.Service.LoadEpgForChannelAsync(h.Channel);
Check(h.Http.Requests == 2, "Expired schedule is fetched again even if the cache returns it");

var legacy = Create();
legacy.Cache.Entries[Key(legacy.Channel)] = JsonSerializer.Serialize(new
{
    NowTitle = "Outdated snapshot", NowDescription = "Old details", NowTimeRange = "Old time",
    CachedAt = origin.UtcDateTime, LatestShowEndUtc = origin.AddHours(8).UtcDateTime
});
Check(!JsonSerializer.Deserialize<EpgData>(legacy.Cache.Entries[Key(legacy.Channel)])!.IsStillValid(origin.UtcDateTime),
    "Legacy snapshot is invalid even though the final guide entry is hours away");
legacy.Http.Response = Guide(Program("Fresh show", -5, 30));
await legacy.Service.LoadEpgForChannelAsync(legacy.Channel);
Check(legacy.Channel.NowTitle == "Fresh show" && legacy.Http.Requests == 1 && legacy.Cache.Writes == 1,
    "Legacy snapshot is replaced automatically");

var shortShow = Create();
shortShow.Http.Response = Guide(Program("Short show", -1, 2));
await shortShow.Service.LoadEpgForChannelAsync(shortShow.Channel);
Check(shortShow.Channel.EpgSchedule!.ExpiresUtc == origin.AddMinutes(2).UtcDateTime &&
    shortShow.Cache.LastLifetime == TimeSpan.FromMinutes(2), "Minimum cache duration never extends past guide coverage");
shortShow.Clock.Now = origin.AddMinutes(2);
shortShow.Channel.RefreshCurrentProgram(shortShow.Clock.Now.UtcDateTime);
Check(shortShow.Channel.NowTitle == null && !shortShow.Channel.EpgLoaded, "Final show disappears exactly when the guide expires");
shortShow.Http.Response = Guide(Program("Replacement show", 2, 40));
await shortShow.Service.LoadEpgForChannelAsync(shortShow.Channel);
Check(shortShow.Channel.NowTitle == "Replacement show" && shortShow.Http.Requests == 2, "Refresh obtains the next guide after coverage ends");

var flags = Create();
flags.Http.Response = Guide(
    Program("Actual show", -10, 10),
    Program("Old flagged show", -30, -20, nowPlaying: 1),
    Program("Future flagged show", 20, 30, nowPlaying: 1),
    new { start_timestamp = long.MaxValue, stop_timestamp = long.MaxValue, title = "Invalid timestamp" },
    new { start_timestamp = false, stop_timestamp = 123, title = "Invalid type" },
    Program("Backwards interval", 10, 5));
await flags.Service.LoadEpgForChannelAsync(flags.Channel);
Check(flags.Channel.NowTitle == "Actual show", "Provider now_playing flags cannot override time boundaries");
Check(flags.Channel.EpgSchedule!.Programs!.Count == 3, "Malformed timestamps and reversed intervals do not discard valid programs");

var future = Create();
future.Http.Response = Guide(Program("Not yet airing", 10, 20));
await future.Service.LoadEpgForChannelAsync(future.Channel);
Check(future.Channel.NowTitle == null && future.Channel.EpgLoaded, "Future-only guide has no current program");
future.Clock.Now = origin.AddMinutes(10);
future.Channel.RefreshCurrentProgram(future.Clock.Now.UtcDateTime);
Check(future.Channel.NowTitle == "Not yet airing" && future.Http.Requests == 1, "Future-only schedule becomes current without a request");

var empty = Create();
empty.Http.Response = Guide();
await empty.Service.LoadEpgForChannelAsync(empty.Channel);
Check(empty.Channel.EpgSchedule!.IsStillValid(origin.UtcDateTime) && empty.Channel.NowTitle == null,
    "Empty schedule is distinct from an invalid legacy entry");

var maxAge = Create();
maxAge.Settings.MaxCacheValidityHours = 1;
maxAge.Http.Response = Guide(Program("Long show", -10, 600));
await maxAge.Service.LoadEpgForChannelAsync(maxAge.Channel);
Check(maxAge.Channel.EpgSchedule!.ExpiresUtc == origin.AddHours(1).UtcDateTime, "Configured maximum guide lifetime is honored");

var outage = Create();
outage.Http.Response = Guide(Program("Ending show", -10, 2));
await outage.Service.LoadEpgForChannelAsync(outage.Channel);
outage.Clock.Now = origin.AddMinutes(3);
outage.Http.Error = new HttpRequestException("Provider unavailable");
await outage.Service.LoadEpgForChannelAsync(outage.Channel);
Check(outage.Channel.NowTitle == null && !outage.Channel.EpgLoaded, "Network failure cannot leave an expired show on screen");
Check(outage.Channel.NextEpgRefreshUtc == outage.Clock.Now.AddMinutes(1).UtcDateTime, "Failure schedules a bounded retry");

var oldChannelList = Create();
oldChannelList.Cache.Entries[$"channels_{Session.Host}_{Session.Username}_category"] = JsonSerializer.Serialize(new[]
{
    new Channel { Id = 1, EpgChannelId = "epg-id", NowTitle = "Old cached channel label", EpgLoaded = true }
});
var channels = await oldChannelList.Service.LoadChannelsForCategoryAsync(new Category { Id = "category", Name = "Category" });
Check(channels[0].NowTitle == null && !channels[0].EpgLoaded, "Cached channel lists cannot preserve snapshot labels or block EPG loading");

Session.CachingEnabled = false;
var noDisk = Create();
noDisk.Http.Response = Guide(Program("First show", -5, 5), Program("Second show", 5, 40));
await noDisk.Service.LoadEpgForChannelAsync(noDisk.Channel);
noDisk.Clock.Now = origin.AddMinutes(5);
await noDisk.Service.LoadEpgForChannelAsync(noDisk.Channel);
Check(noDisk.Channel.NowTitle == "Second show" && noDisk.Http.Requests == 1 && noDisk.Cache.Reads == 0 && noDisk.Cache.Writes == 0,
    "Loaded schedule advances in memory with disk caching disabled");

var noGuideId = Create();
noGuideId.Channel.EpgChannelId = null;
noGuideId.Http.Response = Guide(Program("Selected channel guide", -5, 10));
await noGuideId.Service.LoadEpgForChannelAsync(noGuideId.Channel);
Check(noGuideId.Http.Requests == 1 && noGuideId.Channel.NowTitle == "Selected channel guide",
    "Selected channels can load the shared guide by stream ID without an optional EPG identifier");

Console.WriteLine($"Passed {passed} EPG regression checks.");

Harness Create()
{
    var clock = new ManualClock { Now = origin };
    var cache = new JsonCache();
    var http = new FakeHttp();
    var settings = new EpgSettings();
    var service = new ChannelService(new SessionService(NullLogger<SessionService>.Instance), http, cache,
        NullLogger<ChannelService>.Instance, new ApiSettings(), new CacheSettings(), new BatchProcessingSettings(),
        new M3uSettings(), settings, clock);
    return new(clock, cache, http, settings, service, new Channel { Id = 1, Name = "Test channel", EpgChannelId = "epg-id" });
}

object Program(string title, int startMinutes, int endMinutes, bool numeric = false, int nowPlaying = 0) => new
{
    title = Convert.ToBase64String(Encoding.UTF8.GetBytes(title)),
    description = Convert.ToBase64String(Encoding.UTF8.GetBytes(title + " details")),
    start_timestamp = numeric ? (object)origin.AddMinutes(startMinutes).ToUnixTimeSeconds() : origin.AddMinutes(startMinutes).ToUnixTimeSeconds().ToString(),
    stop_timestamp = numeric ? (object)origin.AddMinutes(endMinutes).ToUnixTimeSeconds() : origin.AddMinutes(endMinutes).ToUnixTimeSeconds().ToString(),
    now_playing = nowPlaying
};
string Guide(params object[] programs) => JsonSerializer.Serialize(new { epg_listings = programs });
string Key(Channel channel) => $"epg_{Session.Host}_{Session.Username}_{channel.Id}";
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    passed++;
}

sealed record Harness(ManualClock Clock, JsonCache Cache, FakeHttp Http, EpgSettings Settings, ChannelService Service, Channel Channel);
sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class FakeHttp : IHttpService
{
    public string Response { get; set; } = "";
    public Exception? Error { get; set; }
    public int Requests { get; private set; }
    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests++;
        return Error == null ? Task.FromResult(Response) : Task.FromException<string>(Error);
    }
    public Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

// Round-trip JSON on each read to exercise disk-cache semantics. Return expired
// data deliberately: EPG validity must be enforced even when a cache bypasses TTL.
sealed class JsonCache : ICacheService
{
    public Dictionary<string, string> Entries { get; } = new();
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public TimeSpan? LastLifetime { get; private set; }
    public Task<T?> GetDataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        Reads++;
        return Task.FromResult(Entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : null);
    }
    public Task SetDataAsync<T>(string key, T data, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class
    {
        Entries[key] = JsonSerializer.Serialize(data);
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
