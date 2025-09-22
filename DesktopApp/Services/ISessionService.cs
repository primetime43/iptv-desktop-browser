using System.Diagnostics;
using DesktopApp.Models;

namespace DesktopApp.Services;

public interface ISessionService
{
    SessionMode Mode { get; set; }
    string Host { get; set; }
    int Port { get; set; }
    bool UseSsl { get; set; }
    string Username { get; set; }
    string Password { get; set; }
    UserInfo? UserInfo { get; set; }
    bool CachingEnabled { get; }

    List<PlaylistEntry> PlaylistChannels { get; }
    List<VodContent> VodContent { get; }
    List<VodCategory> VodCategories { get; }
    List<SeriesContent> SeriesContent { get; }
    List<SeriesCategory> SeriesCategories { get; }
    Dictionary<string, List<EpgEntry>> M3uEpgByChannel { get; }

    event Action? M3uEpgUpdated;

    void ResetM3u();
    void RaiseM3uEpgUpdated();
    string BuildApi(string action, params (string key, string value)[] additionalParams);
    string BuildStreamUrl(string streamId, string extension = "m3u8");
    string BuildVodStreamUrl(string streamId, string containerExtension);
    ProcessStartInfo BuildPlayerProcess(string streamUrl, string title = "IPTV Stream");
}