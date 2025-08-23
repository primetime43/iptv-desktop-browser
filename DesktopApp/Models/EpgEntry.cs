using System;

namespace DesktopApp.Models;

public sealed class EpgEntry
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsNow(DateTime utcNow) => utcNow >= StartUtc && utcNow < EndUtc;
    public string TimeRangeLocal => $"{StartUtc.ToLocalTime():HH:mm} - {EndUtc.ToLocalTime():HH:mm}";
}
