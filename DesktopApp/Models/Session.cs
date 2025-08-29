using System;
using System.Diagnostics;
using System.Collections.Generic;
using DesktopApp.Models;

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

    // M3U playlist data (used when Mode == M3u)
    public static List<PlaylistEntry> PlaylistChannels { get; set; } = new();

    public static void ResetM3u()
    {
        PlaylistChannels.Clear();
        UserInfo = null;
        Username = string.Empty;
        Password = string.Empty;
    }

    // Legacy VLC path property (will still be honored as fallback for VLC player selection)
    public static string? VlcPath { get; set; }

    // Preferred external player settings
    public static PlayerKind PreferredPlayer { get; set; } = PlayerKind.VLC;
    // Optional explicit executable path for the selected player (if null we rely on PATH / shell association)
    public static string? PlayerExePath { get; set; }
    // Argument template supports tokens: {url} {title}
    public static string PlayerArgsTemplate { get; set; } = "\"{url}\""; // will be overridden by defaults per player kind when changed in UI

    // EPG refresh tracking (only meaningful for Xtream mode currently)
    public static DateTime? LastEpgUpdateUtc { get; set; }
    public static TimeSpan EpgRefreshInterval { get; set; } = TimeSpan.FromMinutes(30);
    public static event Action? EpgRefreshRequested;
    public static void RaiseEpgRefreshRequested()
    {
        if (Mode != SessionMode.Xtream) return; // no EPG in m3u mode yet
        LastEpgUpdateUtc = DateTime.UtcNow;
        try { EpgRefreshRequested?.Invoke(); } catch { }
    }

    public static string BaseUrl => $"{(UseSsl ? "https" : "http")}://{Host}:{Port}";

    public static string BuildApi(string? action = null)
    {
        var core = $"{BaseUrl}/player_api.php?username={Uri.EscapeDataString(Username)}&password={Uri.EscapeDataString(Password)}";
        if (!string.IsNullOrWhiteSpace(action)) core += "&action=" + action;
        return core;
    }

    // Build direct live stream URL (Xtream Codes style). Default extension .ts; some servers support .m3u8.
    public static string BuildStreamUrl(int streamId, string extension = "ts")
        => $"{BaseUrl}/live/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{streamId}.{extension}";

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
                exe = string.IsNullOrWhiteSpace(PlayerExePath) ? "mpc-hc64.exe" : PlayerExePath!; // user can point to mpc-hc.exe 32-bit
                argsTemplate = string.IsNullOrWhiteSpace(PlayerArgsTemplate) ? "\"{url}\" /play" : PlayerArgsTemplate;
                break;
            case PlayerKind.MPV:
                exe = string.IsNullOrWhiteSpace(PlayerExePath) ? "mpv" : PlayerExePath!;
                argsTemplate = string.IsNullOrWhiteSpace(PlayerArgsTemplate) ? "--force-media-title=\"{title}\" \"{url}\"" : PlayerArgsTemplate;
                break;
            case PlayerKind.Custom:
            default:
                exe = string.IsNullOrWhiteSpace(PlayerExePath) ? "" : PlayerExePath!; // must be provided
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
