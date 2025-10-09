using System;
using System.Diagnostics;
using System.Collections.Generic;
using DesktopApp.Configuration;
using DesktopApp.Models;
using System.IO;

namespace DesktopApp.Models;

public static class Session
{
    // Configuration settings - initialized by App.xaml.cs
    private static ApiSettings? _apiConfig;
    private static PlayerSettings? _playerConfig;
    private static RecordingSettings? _recordingConfig;
    private static EpgSettings? _epgConfig;
    private static M3uSettings? _m3uConfig;
    private static NetworkSettings? _networkConfig;

    public static void InitializeConfiguration(ApiSettings apiConfig, PlayerSettings playerConfig, RecordingSettings recordingConfig, EpgSettings epgConfig, M3uSettings m3uConfig, NetworkSettings networkConfig)
    {
        _apiConfig = apiConfig ?? throw new ArgumentNullException(nameof(apiConfig));
        _playerConfig = playerConfig ?? throw new ArgumentNullException(nameof(playerConfig));
        _recordingConfig = recordingConfig ?? throw new ArgumentNullException(nameof(recordingConfig));
        _epgConfig = epgConfig ?? throw new ArgumentNullException(nameof(epgConfig));
        _m3uConfig = m3uConfig ?? throw new ArgumentNullException(nameof(m3uConfig));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
    }

    public static SessionMode Mode { get; set; } = SessionMode.Xtream; // default existing behavior

    public static string Host { get; set; } = string.Empty;
    public static int Port { get; set; }
    public static bool UseSsl { get; set; }
    public static string Username { get; set; } = string.Empty;
    public static string Password { get; set; } = string.Empty;
    public static UserInfo? UserInfo { get; set; }

    // M3U playlist data (used when Mode == SessionMode.M3u)
    public static List<PlaylistEntry> PlaylistChannels { get; set; } = new();

    // VOD content data
    public static List<VodContent> VodContent { get; set; } = new();
    public static List<VodCategory> VodCategories { get; set; } = new();

    // Series content data
    public static List<SeriesContent> SeriesContent { get; set; } = new();
    public static List<SeriesCategory> SeriesCategories { get; set; } = new();

    // XMLTV EPG data for M3U mode. Keyed by tvg-id (case-insensitive). Each list is sorted by StartUtc.
    public static Dictionary<string, List<EpgEntry>> M3uEpgByChannel { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static event Action? M3uEpgUpdated; // raised after full XMLTV load
    public static void RaiseM3uEpgUpdated() { try { M3uEpgUpdated?.Invoke(); } catch { } }

    public static void ResetM3u()
    {
        PlaylistChannels.Clear();
        M3uEpgByChannel.Clear();
        VodContent.Clear();
        VodCategories.Clear();
        SeriesContent.Clear();
        SeriesCategories.Clear();
        UserInfo = null;
        Username = string.Empty;
        Password = string.Empty;
    }

    // Legacy VLC path property (will still be honored as fallback for VLC player selection)
    public static string? VlcPath { get; set; }

    // Preferred external player settings
    public static PlayerKind PreferredPlayer { get; set; } = PlayerKind.VLC;
    public static string? PlayerExePath { get; set; }
    public static string PlayerArgsTemplate { get; set; } = "\"{url}\""; // will be overridden by defaults per player kind when changed in UI

    // Recording (FFmpeg)
    public static string? FfmpegPath { get; set; } // path to ffmpeg executable
    public static string? RecordingDirectory { get; set; } // base folder for recordings
    public static string FfmpegArgsTemplate { get; set; } = "-i \"{url}\" -c copy -f mpegts \"{output}\""; // tokens: {url} {output} {title}

    // Caching settings
    public static bool CachingEnabled { get; set; } = false; // disabled by default

    // Favorites events and caching
    public static event Action? FavoritesChanged;
    private static List<FavoriteChannel>? _cachedFavorites = null;
    private static string? _lastSessionKey = null;

    // EPG refresh tracking (only meaningful for Xtream mode currently)
    public static DateTime? LastEpgUpdateUtc { get; set; }
    private static TimeSpan? _epgRefreshIntervalOverride = null;
    public static TimeSpan EpgRefreshInterval
    {
        get => _epgRefreshIntervalOverride ?? (_epgConfig != null
            ? TimeSpan.FromMinutes(_epgConfig.RefreshIntervalMinutes)
            : TimeSpan.FromMinutes(30));
        set => _epgRefreshIntervalOverride = value;
    }
    public static event Action? EpgRefreshRequested;
    public static void RaiseEpgRefreshRequested()
    {
        if (Mode != SessionMode.Xtream) return; // no network EPG refresh in m3u mode yet
        LastEpgUpdateUtc = DateTime.UtcNow;
        try { EpgRefreshRequested?.Invoke(); } catch { }
    }

    public static string BaseUrl
    {
        get
        {
            var scheme = UseSsl
                ? (_networkConfig?.Schemes.Https ?? "https")
                : (_networkConfig?.Schemes.Http ?? "http");
            return $"{scheme}://{Host}:{Port}";
        }
    }

    // Favorites methods
    public static bool IsFavoriteChannel(int channelId)
    {
        var favorites = GetFavoriteChannels();
        return favorites.Any(f => f.Id == channelId);
    }

    public static void AddFavoriteChannel(Channel channel)
    {
        var store = new FavoritesStore();
        store.AddFavorite(GetCurrentSessionKey(), channel);

        // Invalidate cache
        _cachedFavorites = null;
        _lastSessionKey = null;

        FavoritesChanged?.Invoke();
    }

    public static void RemoveFavoriteChannel(int channelId)
    {
        var store = new FavoritesStore();
        store.RemoveFavorite(GetCurrentSessionKey(), channelId);

        // Invalidate cache
        _cachedFavorites = null;
        _lastSessionKey = null;

        FavoritesChanged?.Invoke();
    }

    public static void ToggleFavoriteChannel(Channel channel)
    {
        if (IsFavoriteChannel(channel.Id))
            RemoveFavoriteChannel(channel.Id);
        else
            AddFavoriteChannel(channel);
    }

    public static List<FavoriteChannel> GetFavoriteChannels()
    {
        var currentSessionKey = GetCurrentSessionKey();

        // Check if cache is valid for current session
        if (_cachedFavorites != null && _lastSessionKey == currentSessionKey)
        {
            return _cachedFavorites;
        }

        // Cache miss or session changed - load from store
        var store = new FavoritesStore();
        _cachedFavorites = store.GetForCurrentSession(currentSessionKey);
        _lastSessionKey = currentSessionKey;

        return _cachedFavorites;
    }

    public static bool ExportFavorites(string exportPath)
    {
        var store = new FavoritesStore();
        return store.ExportFavorites(GetCurrentSessionKey(), exportPath);
    }

    public static int ImportFavorites(string importPath)
    {
        var store = new FavoritesStore();
        var result = store.ImportFavorites(GetCurrentSessionKey(), importPath);

        if (result > 0)
        {
            // Invalidate cache to force reload
            _cachedFavorites = null;
            _lastSessionKey = null;

            // Notify that favorites have changed
            FavoritesChanged?.Invoke();
        }

        return result;
    }

    private static string GetCurrentSessionKey()
    {
        if (Mode == SessionMode.M3u)
        {
            var prefix = _m3uConfig?.SessionKeyPrefix ?? "m3u_";
            return $"{prefix}{Environment.UserName}";
        }
        else
        {
            return $"{Environment.UserName}_{Host}_{Port}_{Username}";
        }
    }

    public static string BuildApi(string? action = null)
    {
        var playerApiEndpoint = _apiConfig?.Endpoints.PlayerApi ?? "player_api.php";
        var core = $"{BaseUrl}/{playerApiEndpoint}?username={Uri.EscapeDataString(Username)}&password={Uri.EscapeDataString(Password)}";
        if (!string.IsNullOrWhiteSpace(action)) core += "&action=" + action;
        return core;
    }

    public static string BuildStreamUrl(int streamId, string? extension = null)
    {
        var ext = extension ?? _apiConfig?.DefaultExtensions.LiveStream ?? "ts";
        var livePath = _apiConfig?.StreamPaths.Live ?? "live";
        return $"{BaseUrl}/{livePath}/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{streamId}.{ext}";
    }

    public static string BuildVodStreamUrl(int streamId, string? extension = null)
    {
        var ext = extension ?? _apiConfig?.DefaultExtensions.VodStream ?? "mp4";
        var moviePath = _apiConfig?.StreamPaths.Movie ?? "movie";
        return $"{BaseUrl}/{moviePath}/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{streamId}.{ext}";
    }

    public static string BuildSeriesStreamUrl(int streamId, string? extension = null)
    {
        var ext = extension ?? _apiConfig?.DefaultExtensions.SeriesStream ?? "mp4";
        var seriesPath = _apiConfig?.StreamPaths.Series ?? "series";
        return $"{BaseUrl}/{seriesPath}/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{streamId}.{ext}";
    }

    public static ProcessStartInfo BuildPlayerProcess(string streamUrl, string title)
    {
        var player = PreferredPlayer;
        string exe;
        string argsTemplate;
        switch (player)
        {
            case PlayerKind.VLC:
                exe = string.IsNullOrWhiteSpace(PlayerExePath)
                    ? (string.IsNullOrWhiteSpace(VlcPath)
                        ? (_playerConfig?.VLC.DefaultExecutable ?? "vlc")
                        : VlcPath!)
                    : PlayerExePath!;
                argsTemplate = string.IsNullOrWhiteSpace(PlayerArgsTemplate)
                    ? (_playerConfig?.VLC.DefaultArguments ?? "\"{url}\" --meta-title=\"{title}\"")
                    : PlayerArgsTemplate;
                break;
            case PlayerKind.MPCHC:
                exe = string.IsNullOrWhiteSpace(PlayerExePath)
                    ? (_playerConfig?.MPCHC.DefaultExecutable ?? "mpc-hc64.exe")
                    : PlayerExePath!;
                argsTemplate = string.IsNullOrWhiteSpace(PlayerArgsTemplate)
                    ? (_playerConfig?.MPCHC.DefaultArguments ?? "\"{url}\" /play")
                    : PlayerArgsTemplate;
                break;
            case PlayerKind.MPV:
                exe = string.IsNullOrWhiteSpace(PlayerExePath)
                    ? (_playerConfig?.MPV.DefaultExecutable ?? "mpv")
                    : PlayerExePath!;
                argsTemplate = string.IsNullOrWhiteSpace(PlayerArgsTemplate)
                    ? (_playerConfig?.MPV.DefaultArguments ?? "--force-media-title=\"{title}\" \"{url}\"")
                    : PlayerArgsTemplate;
                break;
            case PlayerKind.Custom:
            default:
                exe = string.IsNullOrWhiteSpace(PlayerExePath)
                    ? (_playerConfig?.Custom.DefaultExecutable ?? "")
                    : PlayerExePath!;
                argsTemplate = PlayerArgsTemplate;
                break;
        }
        var safeTitle = (title ?? string.Empty).Replace("\"", "'");
        string args = (argsTemplate ?? string.Empty).Replace("{url}", streamUrl).Replace("{title}", safeTitle);

        return new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = string.IsNullOrWhiteSpace(PlayerExePath),
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };
    }

    public static ProcessStartInfo? BuildFfmpegRecordProcess(string streamUrl, string title, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(FfmpegPath) || !File.Exists(FfmpegPath)) return null;
        var safeTitle = (title ?? string.Empty).Replace("\"", "'");
        var argsTemplate = string.IsNullOrWhiteSpace(FfmpegArgsTemplate)
            ? (_recordingConfig?.FFmpeg.DefaultArguments ?? "-i \"{url}\" -c copy -f mpegts \"{output}\"")
            : FfmpegArgsTemplate;
        var args = argsTemplate.Replace("{url}", streamUrl).Replace("{title}", safeTitle).Replace("{output}", outputPath);
        return new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
    }
}

public enum PlayerKind
{
    VLC,
    MPCHC,
    MPV,
    Custom
}

public enum SessionMode
{
    Xtream,
    M3u
}

public class PlaylistEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // group-title
    public string? Logo { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public string? TvgId { get; set; }
    public string? TvgName { get; set; }
}
