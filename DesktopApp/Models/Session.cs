using System;
using System.Diagnostics;
using DesktopApp.Models;

namespace DesktopApp.Models;

public static class Session
{
    public static string Host { get; set; } = string.Empty;
    public static int Port { get; set; }
    public static bool UseSsl { get; set; }
    public static string Username { get; set; } = string.Empty;
    public static string Password { get; set; } = string.Empty;
    public static UserInfo? UserInfo { get; set; }

    // Legacy VLC path property (will still be honored as fallback for VLC player selection)
    public static string? VlcPath { get; set; }

    // Preferred external player settings
    public static PlayerKind PreferredPlayer { get; set; } = PlayerKind.VLC;
    // Optional explicit executable path for the selected player (if null we rely on PATH / shell association)
    public static string? PlayerExePath { get; set; }
    // Argument template supports tokens: {url} {title}
    public static string PlayerArgsTemplate { get; set; } = "\"{url}\""; // will be overridden by defaults per player kind when changed in UI

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
