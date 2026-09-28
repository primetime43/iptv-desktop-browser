using System.IO;
using System.Net.Http;
using System.Text;

namespace DesktopApp.Services;

public interface IStreamQualityService
{
    Task<StreamQualityDiscovery> DiscoverAsync(Uri source, CancellationToken token);
}

public sealed class StreamQualityService(HttpClient http) : IStreamQualityService
{
    public const int MaximumManifestBytes = 256 * 1024;
    public async Task<StreamQualityDiscovery> DiscoverAsync(Uri source, CancellationToken token)
    {
        if (source.Scheme is not ("http" or "https")) throw new NotSupportedException("Quality discovery requires an HTTP stream.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumManifestBytes) throw new FormatException("This stream is not a small HLS playlist.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumManifestBytes) throw new FormatException("The playlist exceeds the discovery limit.");
            buffer.Write(chunk, 0, read);
            if (buffer.Length >= 16 && !Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)Math.Min(buffer.Length, 64)).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
                throw new FormatException("This stream does not advertise HLS qualities.");
        }
        timeout.Token.ThrowIfCancellationRequested();
        // Use the final URL after redirects so relative variant/audio/key URIs remain valid.
        var finalUri = response.RequestMessage?.RequestUri ?? source;
        return HlsQualityParser.Parse(Encoding.UTF8.GetString(buffer.ToArray()), finalUri);
    }
}
