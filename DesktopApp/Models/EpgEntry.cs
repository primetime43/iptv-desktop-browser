using System;

namespace DesktopApp.Models;

public sealed class EpgEntry
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsNow(DateTime utcNow) => utcNow >= StartUtc && utcNow < EndUtc;
    // 12-hour format with am/pm
    public string TimeRangeLocal => $"{StartUtc.ToLocalTime():h:mm tt} - {EndUtc.ToLocalTime():h:mm tt}";
}
