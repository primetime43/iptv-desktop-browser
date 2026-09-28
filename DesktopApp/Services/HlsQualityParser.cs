using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopApp.Services;

public sealed class StreamQualityOption(string label, Uri uri, string? manifest = null, long bandwidth = 0, int height = 0)
{
    public string Label { get; } = label;
    public Uri Uri { get; } = uri;
    public string? Manifest { get; } = manifest;
    public long Bandwidth { get; } = bandwidth;
    public int Height { get; } = height;
    // URLs and manifests may contain provider credentials. UI/debug labels must not expose them.
    public override string ToString() => Label;
}

public sealed class StreamQualityDiscovery(Uri source, IReadOnlyList<StreamQualityOption> options)
{
    public Uri Source { get; } = source;
    public IReadOnlyList<StreamQualityOption> Options { get; } = options;
}

public static class HlsQualityParser
{
    private static readonly Regex Attributes = new("(?<key>[A-Z0-9-]+)=(?<value>\"[^\"\\r\\n]*\"|[^,\\r\\n]+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static StreamQualityDiscovery Parse(string text, Uri source)
    {
        var lines = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').Split('\n').Select(l => l.Trim()).ToArray();
        if (lines.Length == 0 || lines[0] != "#EXTM3U") throw new FormatException("This stream does not advertise HLS qualities.");
        // These extensions require resolving variables or steering requests; leave them to the player.
        if (lines.Any(l => l.StartsWith("#EXT-X-DEFINE:") || l.StartsWith("#EXT-X-CONTENT-STEERING:")))
            throw new NotSupportedException("This playlist requires automatic quality selection.");
        var globals = lines.Where(l => l.StartsWith("#EXT-X-VERSION:") || l == "#EXT-X-INDEPENDENT-SEGMENTS" ||
            l.StartsWith("#EXT-X-START:") || l.StartsWith("#EXT-X-SESSION-KEY:") || l.StartsWith("#EXT-X-SESSION-DATA:")).ToArray();
        var media = lines.Where(l => l.StartsWith("#EXT-X-MEDIA:")).ToArray();
        var options = new List<StreamQualityOption>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:")) continue;
            var tag = lines[i];
            var attributes = ReadAttributes(tag);
            if (!attributes.TryGetValue("BANDWIDTH", out var bandwidthText) || !long.TryParse(bandwidthText, NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth) || bandwidth <= 0) continue;
            var next = i + 1;
            while (next < lines.Length && (lines[next].Length == 0 || lines[next].StartsWith('#') && !lines[next].StartsWith("#EXT"))) next++;
            if (next >= lines.Length || lines[next].StartsWith('#')) continue;
            try
            {
                var uri = Resolve(source, lines[next]);
                var height = 0;
                var label = "Stream";
                if (attributes.TryGetValue("RESOLUTION", out var resolution))
                {
                    var dimensions = resolution.Split('x');
                    if (dimensions.Length == 2 && int.TryParse(dimensions[0], out var width) && width > 0 &&
                        int.TryParse(dimensions[1], out height) && height > 0) label = $"{height}p";
                }
                var rate = bandwidth;
                if (attributes.TryGetValue("AVERAGE-BANDWIDTH", out var average) &&
                    long.TryParse(average, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) rate = parsed;
                label += rate >= 1_000_000 ? $" · {rate / 1_000_000d:0.##} Mbps" : $" · {rate / 1000d:0.#} kbps";
                if (attributes.TryGetValue("FRAME-RATE", out var fps) && double.TryParse(fps, NumberStyles.Float, CultureInfo.InvariantCulture, out var frameRate) && frameRate > 0 && frameRate <= 240)
                    label += $" · {frameRate:0.##} fps";
                var manifest = new StringBuilder("#EXTM3U\n");
                foreach (var global in globals) manifest.AppendLine(RebaseUris(global, source));
                foreach (var rendition in media)
                {
                    var values = ReadAttributes(rendition);
                    if (values.TryGetValue("TYPE", out var type) && values.TryGetValue("GROUP-ID", out var group) &&
                        attributes.TryGetValue(type, out var selectedGroup) && group == selectedGroup)
                        manifest.AppendLine(RebaseUris(rendition, source));
                }
                manifest.AppendLine(tag).AppendLine(uri.AbsoluteUri);
                options.Add(new StreamQualityOption(label, uri, manifest.ToString(), bandwidth, height));
            }
            catch (FormatException) { /* Keep valid variants if one URI is malformed or unsupported. */ }
        }
        return new StreamQualityDiscovery(source, options.DistinctBy(o => o.Manifest)
            .OrderByDescending(o => o.Height).ThenByDescending(o => o.Bandwidth).ToArray());
    }

    private static Dictionary<string, string> ReadAttributes(string line) => Attributes.Matches(line)
        .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value.Trim('"'), StringComparer.Ordinal);

    private static string RebaseUris(string line, Uri source) => Attributes.Replace(line, match =>
        match.Groups["key"].Value == "URI" ? $"URI=\"{Resolve(source, match.Groups["value"].Value.Trim('"')).AbsoluteUri}\"" : match.Value);

    private static Uri Resolve(Uri source, string value)
    {
        if (value.Contains("{$", StringComparison.Ordinal) || value.Contains('"') || value.Any(char.IsControl) ||
            !Uri.TryCreate(source, value, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("Unsupported playlist URI.");
        return uri;
    }
}
