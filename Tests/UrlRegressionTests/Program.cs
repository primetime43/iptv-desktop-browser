using System.Net;
using DesktopApp.Configuration;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Extensions.Logging.Abstractions;

var passed = 0;
var service = new SessionService(NullLogger<SessionService>.Instance);
Session.Host = "example.test";
Session.Port = 8080;
Session.UseSsl = false;

foreach (var credential in new[] { "ordinary", "a&b#c", "a+b=c?d/e", "100% literal%26", " spaced ü密碼 " })
{
    Session.Username = credential;
    Session.Password = credential;
    foreach (var endpoint in new[] { "player_api.php", "panel_api.php", "get.php" })
    {
        var loginUrl = XtreamUrlBuilder.BuildApi(Session.BaseUrl, endpoint, credential, credential);
        CheckQuery(loginUrl, credential, credential, 2);
    }

    // Parse the URI as a server would: delimiters must not introduce extra parameters or a fragment.
    var browseUrl = service.BuildApi("get_live_streams", ("category_id", "cat&=#+% ü"));
    var query = CheckQuery(browseUrl, credential, credential, 4);
    Check(query["action"] == "get_live_streams" && query["category_id"] == "cat&=#+% ü",
        "Browsing parameters round trip");
    Check(browseUrl == Session.BuildApi("get_live_streams", ("category_id", "cat&=#+% ü")),
        "Static and injected sessions use the same URL");

    foreach (var streamUrl in new[]
    {
        service.BuildStreamUrl("42"), service.BuildVodStreamUrl("42", "mp4"),
        Session.BuildStreamUrl(42), Session.BuildVodStreamUrl(42), Session.BuildSeriesStreamUrl(42)
    })
    {
        var uri = new Uri(streamUrl);
        var segments = uri.AbsolutePath.Split('/');
        Check(uri.Fragment == "" && uri.Query == "" && segments.Length == 5 &&
            Uri.UnescapeDataString(segments[2]) == credential && Uri.UnescapeDataString(segments[3]) == credential,
            "Playback credentials remain individual path segments");
    }
}

var unusualQuery = CheckQuery(service.BuildApi("action&#", ("key&=#", "value&=#")),
    Session.Username, Session.Password, 4);
Check(unusualQuery["action"] == "action&#" && unusualQuery["key&=#"] == "value&=#",
    "Action and parameter names are encoded");

var api = new ApiSettings();
api.Endpoints.PlayerApi = "custom/player.php";
api.StreamPaths.Live = "custom-live";
api.StreamPaths.Movie = "custom-movie";
api.DefaultExtensions.LiveStream = "custom-ts";
Session.InitializeConfiguration(api, new PlayerSettings(), new RecordingSettings(), new EpgSettings(),
    new M3uSettings(), new NetworkSettings());
foreach (var (ssl, port) in new[] { (false, 0), (false, 80), (false, 8080), (true, 0), (true, 443), (true, 8443) })
{
    Session.UseSsl = ssl;
    Session.Port = port;
    var uri = new Uri(service.BuildApi("get_live_categories"));
    Check(uri.Scheme == (ssl ? "https" : "http") && uri.Port == (port == 0 ? (ssl ? 443 : 80) : port) &&
        uri.AbsolutePath == "/custom/player.php", "Configured endpoint, scheme and port retained");
}
Check(new Uri(service.BuildStreamUrl("42")).AbsolutePath.StartsWith("/custom-live/") &&
    service.BuildStreamUrl("42").EndsWith("/42.m3u8") && Session.BuildStreamUrl(42).EndsWith("/42.custom-ts"),
    "Configured live path and existing extension defaults retained");
Check(new Uri(service.BuildVodStreamUrl("42", "mkv")).AbsolutePath.StartsWith("/custom-movie/") &&
    service.BuildVodStreamUrl("42", "mkv").EndsWith("/42.mkv"), "Configured VOD path retained");
Console.WriteLine($"Passed {passed} URL regression checks.");

Dictionary<string, string> CheckQuery(string url, string username, string password, int count)
{
    var uri = new Uri(url);
    var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
    Check(uri.Fragment == "" && query.Count == count && query["username"] == username && query["password"] == password,
        "Credentials survive query parsing without extra parameters or fragments");
    return query;
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
}
