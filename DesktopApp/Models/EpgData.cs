namespace DesktopApp.Models;

public class EpgData
{
    // Null distinguishes old snapshot-only disk entries from a valid empty schedule.
    public List<EpgEntry>? Programs { get; set; }
    public DateTime CachedAt { get; set; }
    public DateTime ExpiresUtc { get; set; }

    public bool IsStillValid(DateTime? utcNow = null) =>
        Programs != null && (utcNow ?? DateTime.UtcNow) < ExpiresUtc;

    public EpgEntry? GetCurrentProgram(DateTime utcNow) => IsStillValid(utcNow)
        ? Programs!.Where(p => p.IsNow(utcNow) && !string.IsNullOrWhiteSpace(p.Title))
            .OrderByDescending(p => p.StartUtc).FirstOrDefault()
        : null;
}
