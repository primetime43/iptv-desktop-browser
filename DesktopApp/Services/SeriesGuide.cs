using DesktopApp.Models;

namespace DesktopApp.Services;

public static class SeriesGuide
{
    public static string StripEpisodeMetadata(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        // Remove *** NEW indicator
        title = title.Replace("***", "").Trim();

        // Remove emojis and LIVE indicators
        title = title.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");

        // Remove common tags in brackets or parentheses
        // Matches: [NEW], (NEW), [REPEAT], (HD), etc.
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s*[\[\(](NEW|REPEAT|RERUN|ENCORE|HD|4K|CC|DVS)[\]\)]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove episode numbers like S01E05, 1x05, Ep. 5, Episode 5
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+S\d+E\d+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+\d+x\d+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+Ep\.?\s*\d+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+Episode\s+\d+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove standalone tags at the end like "NEW", "REPEAT", etc.
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+(NEW|REPEAT|RERUN|ENCORE|HD|4K|CC)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove dates in various formats (YYYY-MM-DD, MM/DD/YY, etc.)
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+\d{4}-\d{2}-\d{2}", "");
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+\d{1,2}/\d{1,2}/\d{2,4}", "");

        // Remove extra whitespace
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+", " ");

        return title.Trim();
    }

    public static IEnumerable<EpgEntry> FindEpisodes(IEnumerable<EpgEntry> entries, string name, DateTime nowUtc) =>
        entries.Where(e => e.StartUtc > nowUtc && StripEpisodeMetadata(e.Title).Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.StartUtc);
}
