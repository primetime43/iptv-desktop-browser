namespace DesktopApp.Configuration;

public class AppConfiguration
{
    public AppSettings AppSettings { get; set; } = new();
    public GitHubSettings GitHub { get; set; } = new();
    public ApiSettings Api { get; set; } = new();
    public HttpSettings Http { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public UISettings UI { get; set; } = new();
    public PlayerSettings Players { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public EpgSettings Epg { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public BatchProcessingSettings BatchProcessing { get; set; } = new();
    public M3uSettings M3u { get; set; } = new();
}

public class AppSettings
{
    public string ApplicationName { get; set; } = "IPTV Desktop Browser";
    public string Version { get; set; } = GetDefaultVersion();

    private static string GetDefaultVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "2.1.1";
    }
}

public class GitHubSettings
{
    public string RepositoryOwner { get; set; } = "primetime43";
    public string RepositoryName { get; set; } = "iptv-desktop-browser";
    public bool CheckForUpdates { get; set; } = true;

    public string ApiUrl => $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
}

public class ApiSettings
{
    public ApiEndpoints Endpoints { get; set; } = new();
    public ApiActions Actions { get; set; } = new();
    public StreamPaths StreamPaths { get; set; } = new();
    public DefaultExtensions DefaultExtensions { get; set; } = new();
}

public class ApiEndpoints
{
    public string PlayerApi { get; set; } = "player_api.php";
    public string PanelApi { get; set; } = "panel_api.php";
    public string GetPlaylist { get; set; } = "get.php";
}

public class ApiActions
{
    public string GetLiveCategories { get; set; } = "get_live_categories";
    public string GetLiveStreams { get; set; } = "get_live_streams";
    public string GetSimpleDataTable { get; set; } = "get_simple_data_table";
    public string GetVodCategories { get; set; } = "get_vod_categories";
    public string GetVodStreams { get; set; } = "get_vod_streams";
}

public class StreamPaths
{
    public string Live { get; set; } = "live";
    public string Movie { get; set; } = "movie";
    public string Series { get; set; } = "series";
}

public class DefaultExtensions
{
    public string LiveStream { get; set; } = "ts";
    public string VodStream { get; set; } = "mp4";
    public string SeriesStream { get; set; } = "mp4";
}

public class HttpSettings
{
    public int DefaultTimeoutSeconds { get; set; } = 30;
    public string UserAgent { get; set; } = GetDefaultUserAgent();
    public TimeoutSettings Timeouts { get; set; } = new();

    private static string GetDefaultUserAgent()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        var versionString = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "2.1.1";
        return $"IPTV-Desktop-Browser/{versionString}";
    }
}

public class TimeoutSettings
{
    public int LoginRequestSeconds { get; set; } = 12;
    public int M3uLoadSeconds { get; set; } = 30;
    public int XmltvLoadSeconds { get; set; } = 40;
}

public class NetworkSettings
{
    public DefaultPorts DefaultPorts { get; set; } = new();
    public Schemes Schemes { get; set; } = new();
}

public class DefaultPorts
{
    public int Http { get; set; } = 80;
    public int Https { get; set; } = 443;
}

public class Schemes
{
    public string Http { get; set; } = "http";
    public string Https { get; set; } = "https";
}

public class UISettings
{
    public UIColors Colors { get; set; } = new();
    public UIText Text { get; set; } = new();
}

public class UIColors
{
    public ColorRgb Info { get; set; } = new() { R = 108, G = 130, B = 152 };
    public ColorRgb Success { get; set; } = new() { R = 76, G = 209, B = 100 };
    public ColorRgb Error { get; set; } = new() { R = 229, G = 96, B = 96 };
}

public class ColorRgb
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
}

public class UIText
{
    public ModeText XtreamMode { get; set; } = new();
    public ModeText M3uMode { get; set; } = new();
}

public class ModeText
{
    public string Header { get; set; } = string.Empty;
    public string HelpHeader { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
}

public class PlayerSettings
{
    public PlayerConfig VLC { get; set; } = new();
    public PlayerConfig MPCHC { get; set; } = new();
    public PlayerConfig MPV { get; set; } = new();
    public PlayerConfig Custom { get; set; } = new();
}

public class PlayerConfig
{
    public string DefaultExecutable { get; set; } = string.Empty;
    public string DefaultArguments { get; set; } = string.Empty;
}

public class RecordingSettings
{
    public FFmpegSettings FFmpeg { get; set; } = new();
}

public class FFmpegSettings
{
    public string DefaultArguments { get; set; } = "-i \"{url}\" -c copy -f mpegts \"{output}\"";
}

public class EpgSettings
{
    public int RefreshIntervalMinutes { get; set; } = 30;
    public int MinCacheValidityMinutes { get; set; } = 30;
    public int MaxCacheValidityHours { get; set; } = 24;
    public int SmartCacheExpirationMinutesBeforeShowEnd { get; set; } = 5;
}

public class CacheSettings
{
    public bool Enabled { get; set; } = false;
    public CacheDurations Durations { get; set; } = new();
    public CacheKeyPrefixes KeyPrefixes { get; set; } = new();
}

public class CacheDurations
{
    public int CategoriesHours { get; set; } = 2;
    public int ChannelsMinutes { get; set; } = 30;
    public int EpgMinimumMinutes { get; set; } = 30;
    public int EpgMaximumHours { get; set; } = 24;
}

public class CacheKeyPrefixes
{
    public string LiveCategories { get; set; } = "live_categories";
    public string Channels { get; set; } = "channels";
    public string Epg { get; set; } = "epg";
    public string VodCategories { get; set; } = "vod_categories";
    public string VodStreams { get; set; } = "vod_streams";
}

public class BatchProcessingSettings
{
    public int EpgBatchSize { get; set; } = 10;
    public int DelayBetweenBatchesMilliseconds { get; set; } = 100;
}

public class M3uSettings
{
    public M3uDefaultCategories DefaultCategories { get; set; } = new();
    public string SessionKeyPrefix { get; set; } = "m3u_";
}

public class M3uDefaultCategories
{
    public string AllId { get; set; } = "all";
    public string AllName { get; set; } = "All Channels";
}
