using System;
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

    // Optional configurable VLC executable path. If null/empty, "vlc" on PATH or system association will be used.
    public static string? VlcPath { get; set; }

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
}
