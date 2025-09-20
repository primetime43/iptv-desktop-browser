namespace DesktopApp.Models;

public class EpgData
{
    public string? NowTitle { get; set; }
    public string? NowDescription { get; set; }
    public string? NowTimeRange { get; set; }
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The latest end time of any show in the cached EPG data.
    /// Cache should expire shortly before this time to fetch the next batch.
    /// </summary>
    public DateTime? LatestShowEndUtc { get; set; }

    /// <summary>
    /// Smart EPG cache expiration: valid until 5 minutes before the last show ends,
    /// with a minimum validity of 30 minutes and maximum of 24 hours.
    /// </summary>
    public bool IsStillValid()
    {
        var now = DateTime.UtcNow;

        // If we have EPG data with show times, use smart expiration
        if (LatestShowEndUtc.HasValue)
        {
            // Expire 5 minutes before the last show ends (to fetch next batch)
            var smartExpirationTime = LatestShowEndUtc.Value.AddMinutes(-5);

            // Ensure minimum validity of 30 minutes (in case shows are very short)
            var minimumExpirationTime = CachedAt.AddMinutes(30);

            // Ensure maximum validity of 24 hours (in case EPG data goes far into future)
            var maximumExpirationTime = CachedAt.AddHours(24);

            // Use the earliest of smart expiration or max, but not earlier than minimum
            var actualExpirationTime = smartExpirationTime > minimumExpirationTime
                ? (smartExpirationTime < maximumExpirationTime ? smartExpirationTime : maximumExpirationTime)
                : (minimumExpirationTime < maximumExpirationTime ? minimumExpirationTime : maximumExpirationTime);

            return now < actualExpirationTime;
        }

        // Fallback: traditional 30-minute expiration if no show end time available
        return now < CachedAt.AddMinutes(30);
    }
}