namespace DesktopApp.Models;

public class EpgData
{
    public string? NowTitle { get; set; }
    public string? NowDescription { get; set; }
    public string? NowTimeRange { get; set; }
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    // EPG data is considered valid for 15 minutes
    public bool IsStillValid()
    {
        var fifteenMinutesAgo = DateTime.UtcNow.AddMinutes(-15);
        return CachedAt > fifteenMinutesAgo;
    }
}