using System;
using System.Diagnostics;
using System.Collections.Generic;
using DesktopApp.Models;
using System.IO;

namespace DesktopApp.Models;

public static class Session
{
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
    public static TimeSpan EpgRefreshInterval { get; set; } = TimeSpan.FromMinutes(30);
    public static event Action? EpgRefreshRequested;
    public static void RaiseEpgRefreshRequested()
    {
        if (Mode != SessionMode.Xtream) return; // no network EPG refresh in m3u mode yet
        LastEpgUpdateUtc = DateTime.UtcNow;
        try { EpgRefreshRequested?.Invoke(); } catch { }
    }

    public static string BaseUrl => $"{(UseSsl ? "https" : "http")}://{Host}:{Port}";

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

    private static string GetCurrentSessionKey()
    {
        if (Mode == SessionMode.M3u)
        {
            return $"m3u_{Environment.UserName}";
        }
        else
        {
            return $"{Environment.UserName}_{Host}_{Port}_{Username}";
        }
    }

    public static string BuildApi(string? action = null)
    {
        var core = $"{BaseUrl}/player_api.php?username={Uri.EscapeDataString(Username)}&password={Uri.EscapeDataString(Password)}";
        if (!string.IsNullOrWhiteSpace(action)) core += "&action=" + action;
        return core;
    }

    public static string BuildStreamUrl(int streamId, string extension = "ts")
        => $"{BaseUrl}/live/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{streamId}.{extension}";

    public static string BuildVodStreamUrl(int streamId, string extension = "mp4")
        => $"{BaseUrl}/movie/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{streamId}.{extension}";

    public static string BuildSeriesStreamUrl(int streamId, string extension = "mp4")
        => $"{BaseUrl}/series/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{streamId}.{extension}";

    public static ProcessStartInfo BuildPlayerProcess(string streamUrl, string title)
    {
        var player = PreferredPlayer;
        string exe;
        string argsTemplate;
        switch (player)
        {
            case PlayerKind.VLC:
                exe = string.IsNullOrWhiteSpace(PlayerExePath) ? (string.IsNullOrWhiteSpace(VlcPath) ? "vlc" : VlcPath!) : PlayerExePath!;
                argsTemplate = string.IsNullOrWhiteSpace(PlayerArgsTemplate) ? "\"{url}\" --meta-title=\"{title}\"" : PlayerArgsTemplate;
                break;
            case PlayerKind.MPCHC:
                exe = string.IsNullOrWhiteSpace(PlayerExePath) ? "mpc-hc64.exe" : PlayerExePath!;
                argsTemplate = string.IsNullOrWhiteSpace(PlayerArgsTemplate) ? "\"{url}\" /play" : PlayerArgsTemplate;
                break;
            case PlayerKind.MPV:
                exe = string.IsNullOrWhiteSpace(PlayerExePath) ? "mpv" : PlayerExePath!;
                argsTemplate = string.IsNullOrWhiteSpace(PlayerArgsTemplate) ? "--force-media-title=\"{title}\" \"{url}\"" : PlayerArgsTemplate;
                break;
            case PlayerKind.Custom:
            default:
                exe = string.IsNullOrWhiteSpace(PlayerExePath) ? "" : PlayerExePath!;
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
        var argsTemplate = string.IsNullOrWhiteSpace(FfmpegArgsTemplate) ? "-i \"{url}\" -c copy -f mpegts \"{output}\"" : FfmpegArgsTemplate;
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
