using System.Diagnostics;
using DesktopApp.Models;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public class SessionService : ISessionService
{
    private readonly ILogger<SessionService> _logger;

    public SessionService(ILogger<SessionService> logger)
    {
        _logger = logger;
    }

    public SessionMode Mode { get; set; } = SessionMode.Xtream;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseSsl { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public UserInfo? UserInfo { get; set; }

    public List<PlaylistEntry> PlaylistChannels { get; } = new();
    public List<VodContent> VodContent { get; } = new();
    public List<VodCategory> VodCategories { get; } = new();
    public List<SeriesContent> SeriesContent { get; } = new();
    public List<SeriesCategory> SeriesCategories { get; } = new();
    public Dictionary<string, List<EpgEntry>> M3uEpgByChannel { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action? M3uEpgUpdated;

    public void ResetM3u()
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
        _logger.LogInformation("M3U session data reset");
    }

    public void RaiseM3uEpgUpdated()
    {
        try
        {
            M3uEpgUpdated?.Invoke();
            _logger.LogDebug("M3U EPG updated event raised");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error raising M3U EPG updated event");
        }
    }

    public string BuildApi(string action, params (string key, string value)[] additionalParams)
    {
        var protocol = UseSsl ? "https" : "http";
        var portPart = (Port != 0 && Port != (UseSsl ? 443 : 80)) ? $":{Port}" : "";
        var url = $"{protocol}://{Host}{portPart}/player_api.php?username={Username}&password={Password}&action={action}";

        foreach (var (key, value) in additionalParams)
        {
            url += $"&{key}={value}";
        }

        return url;
    }

    public string BuildStreamUrl(string streamId, string extension = "m3u8")
    {
        var protocol = UseSsl ? "https" : "http";
        var portPart = (Port != 0 && Port != (UseSsl ? 443 : 80)) ? $":{Port}" : "";
        return $"{protocol}://{Host}{portPart}/live/{Username}/{Password}/{streamId}.{extension}";
    }

    public string BuildVodStreamUrl(string streamId, string containerExtension)
    {
        var protocol = UseSsl ? "https" : "http";
        var portPart = (Port != 0 && Port != (UseSsl ? 443 : 80)) ? $":{Port}" : "";
        return $"{protocol}://{Host}{portPart}/movie/{Username}/{Password}/{streamId}.{containerExtension}";
    }

    public ProcessStartInfo BuildPlayerProcess(string streamUrl, string title = "IPTV Stream")
    {
        // Simplified implementation - would need proper player configuration in production
        var processInfo = new ProcessStartInfo
        {
            FileName = "vlc",
            Arguments = $"\"{streamUrl}\" --meta-title=\"{title}\"",
            UseShellExecute = true,
            CreateNoWindow = false
        };

        return processInfo;
    }
}