using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

internal static partial class Program
{
    private const string QualityMaster = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-INDEPENDENT-SEGMENTS
        #EXT-X-SESSION-KEY:METHOD=AES-128,URI="keys/key.bin?token=secret"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English, stereo",DEFAULT=YES,URI="audio/en.m3u8?token=secret"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",URI="subs/en.m3u8"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="other",NAME="Unused",URI="audio/other.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=2800000,AVERAGE-BANDWIDTH=2500000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS="avc1.4d001f,mp4a.40.2",AUDIO="aac",SUBTITLES="subs"
        low/index.m3u8?token=secret
        #EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080,AUDIO="aac"
        /high/index.m3u8?token=secret
        #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=50000,URI="iframe.m3u8"
        """;

    private static void VerifyPlaybackQuality()
    {
        var source = new Uri("https://provider.invalid/redirected/master.m3u8?auth=secret");
        var parsed = HlsQualityParser.Parse(QualityMaster, source);
        Check(parsed.Options.Count == 2 && parsed.Options[0].Height == 1080 && parsed.Options[1].Height == 720,
            "Quality parser sorts advertised variants by resolution and ignores iframe playlists");
        var low = parsed.Options[1];
        Check(low.Label.Contains("720p") && low.Label.Contains("2.5 Mbps") && low.Label.Contains("29.97 fps"),
            "Quality labels show resolution, average bandwidth, and frame rate");
        Check(!low.ToString().Contains("secret") && !low.ToString().Contains("provider.invalid"), "Quality labels do not expose stream credentials");
        Check(low.Uri.AbsoluteUri == "https://provider.invalid/redirected/low/index.m3u8?token=secret", "Relative variants resolve against the final master URL");
        Check(low.Manifest!.Contains("https://provider.invalid/redirected/audio/en.m3u8?token=secret") &&
            low.Manifest.Contains("https://provider.invalid/redirected/subs/en.m3u8") &&
            low.Manifest.Contains("https://provider.invalid/redirected/keys/key.bin?token=secret"),
            "Selected-quality manifests preserve and rebase separate audio, subtitles, and session keys");
        Check(!low.Manifest.Contains("high/index") && !low.Manifest.Contains("audio/other") && !low.Manifest.Contains("iframe.m3u8"),
            "A selected master excludes other qualities and unrelated rendition groups");
        Check(HlsQualityParser.Parse(low.Manifest, new Uri("http://127.0.0.1/master.m3u8")).Options.Single().Uri == low.Uri,
            "Rewritten master remains valid and pins exactly one stream variant");
        var media = HlsQualityParser.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nsegment.ts", source);
        Check(media.Options.Count == 0, "Single-quality media playlists advertise no invented alternatives");
        var malformed = HlsQualityParser.Parse("""
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=bad
            invalid.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=10
            file:///c:/private
            #EXT-X-STREAM-INF:BANDWIDTH=20
            #EXT-X-STREAM-INF:BANDWIDTH=300000
            //cdn.invalid/valid.m3u8
            """, source);
        Check(malformed.Options.Count == 1 && malformed.Options[0].Uri.AbsoluteUri == "https://cdn.invalid/valid.m3u8",
            "Malformed and unsupported variants are skipped without losing valid alternatives");
        try { HlsQualityParser.Parse("<html>error</html>", source); Check(false, "Expected non-playlist rejection"); }
        catch (FormatException) { Check(true, "Non-HLS responses cannot become quality choices"); }
        try { HlsQualityParser.Parse("#EXTM3U\n#EXT-X-DEFINE:NAME=\"token\",VALUE=\"secret\"", source); Check(false, "Expected variable rejection"); }
        catch (NotSupportedException) { Check(true, "Unsupported HLS variables defer safely to automatic playback"); }

        AwaitSettings(VerifyQualityDiscoveryAsync(source));
        VerifyQualityDialog(source, parsed);
        AwaitSettings(VerifyPlaybackManifestServerAsync(low));
    }

    private static async Task VerifyQualityDiscoveryAsync(Uri source)
    {
        var handler = new QualityHttpHandler { Body = QualityMaster, FinalUri = source };
        using var http = new HttpClient(handler);
        var service = new StreamQualityService(http);
        var discovery = await service.DiscoverAsync(new Uri("https://provider.invalid/original.m3u8"), default);
        Check(discovery.Source == source && discovery.Options[1].Uri.AbsolutePath == "/redirected/low/index.m3u8", "Discovery uses the HTTP redirect destination for relative references");
        handler.Body = new string('x', StreamQualityService.MaximumManifestBytes + 1);
        try { await service.DiscoverAsync(source, default); Check(false, "Expected oversized rejection"); }
        catch (FormatException) { Check(true, "Discovery refuses large media bodies before buffering them"); }
        handler.Body = "<html>Login required</html>";
        try { await service.DiscoverAsync(source, default); Check(false, "Expected HTML rejection"); }
        catch (FormatException) { Check(true, "Provider error pages cannot become playable variants"); }
        handler.Body = QualityMaster; handler.Status = HttpStatusCode.Unauthorized;
        try { await service.DiscoverAsync(source, default); Check(false, "Expected HTTP rejection"); }
        catch (HttpRequestException) { Check(true, "HTTP errors are exposed to the fallback UI"); }
        handler.Status = HttpStatusCode.OK;
        var binary = new QualityCountingStream(Encoding.UTF8.GetBytes(new string('x', 100000)));
        handler.Stream = binary;
        try { await service.DiscoverAsync(source, default); Check(false, "Expected binary rejection"); }
        catch (FormatException) { Check(binary.BytesRead <= 4096 && binary.Disposed, "Unbounded media responses are stopped after the first small chunk and disposed"); }
        var oversized = new QualityCountingStream(Encoding.UTF8.GetBytes("#EXTM3U\n" + new string('x', StreamQualityService.MaximumManifestBytes + 8192)));
        handler.Stream = oversized;
        try { await service.DiscoverAsync(source, default); Check(false, "Expected streamed-size rejection"); }
        catch (FormatException) { Check(oversized.BytesRead <= StreamQualityService.MaximumManifestBytes + 4096 && oversized.Disposed,
            "Chunked responses without Content-Length still enforce the playlist limit"); }
        handler.Stream = null;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await service.DiscoverAsync(source, cancellation.Token); Check(false, "Expected cancellation"); }
        catch (OperationCanceledException) { Check(true, "Quality discovery honors cancellation"); }
    }

    private static void VerifyQualityDialog(Uri source, StreamQualityDiscovery discovery)
    {
        var original = new Uri("https://provider.invalid/live/user/password/1.ts");
        var service = new QualityServiceFake();
        using var model = new PlaybackQualityViewModel("Selected channel", original, source, service);
        var pending = model.ReloadCommand.ExecuteAsync(null);
        Check(model.IsLoading && !model.ReloadCommand.CanExecute(null) && model.PlayCommand.CanExecute(null),
            "Original playback remains available during quality discovery without duplicate reloads");
        service.Requests.Last().Complete(discovery); AwaitSettings(pending);
        Check(model.Qualities.Count == 3 && model.SelectedQuality == model.Qualities[0] && !model.IsLoading,
            "Discovery offers Automatic plus advertised variants without silently choosing a quality");
        model.SelectedQuality = model.Qualities[2];
        bool? accepted = null; model.CloseRequested += value => accepted = value;
        model.PlayCommand.Execute(null);
        Check(accepted == true && model.Result?.Height == 720 && model.Result.Manifest != null, "Play returns the chosen quality with its audio-preserving master");
        Check(!model.PlayCommand.CanExecute(null), "A completed quality picker cannot launch twice");

        using var failed = new PlaybackQualityViewModel("Movie", original, source, service);
        pending = failed.ReloadCommand.ExecuteAsync(null);
        service.Requests.Last().Fail(); AwaitSettings(pending);
        Check(failed.Status.Contains("could not") && !failed.Status.Contains("secret") && failed.SelectedQuality!.Uri == original,
            "Discovery failures expose safe status and preserve the original URL");
        pending = failed.ReloadCommand.ExecuteAsync(null);
        service.Requests.Last().Complete(new StreamQualityDiscovery(source, [])); AwaitSettings(pending);
        Check(failed.Qualities.Count == 1 && failed.Status.Contains("no alternate"), "Single-quality streams remain playable with an explicit explanation");
        pending = failed.ReloadCommand.ExecuteAsync(null);
        service.Requests.Last().Fail(); AwaitSettings(pending);
        Check(failed.SelectedQuality!.Uri == original, "A failed retry restores the original URL rather than an old redirected master");
        pending = failed.ReloadCommand.ExecuteAsync(null);
        var late = service.Requests.Last();
        failed.CancelCommand.Execute(null);
        late.Complete(discovery); AwaitSettings(pending);
        Check(late.Token.IsCancellationRequested && failed.Result == null && failed.Qualities.Count == 1,
            "Closing the quality picker cancels discovery and ignores providers that return late");

        using var immediate = new PlaybackQualityViewModel("Episode", original, source, service);
        pending = immediate.ReloadCommand.ExecuteAsync(null);
        late = service.Requests.Last(); immediate.PlayCommand.Execute(null);
        late.Complete(discovery); AwaitSettings(pending);
        Check(immediate.Result?.Uri == original && late.Token.IsCancellationRequested,
            "Playing Automatic during discovery immediately selects the original stream and cancels pending work");

        using var bound = new PlaybackQualityViewModel("Bound show", original, source, service);
        var window = new PlaybackQualityWindow(bound);
        var content = (FrameworkElement)window.Content; content.DataContext = bound;
        using var host = new HwndSource(new HwndSourceParameters("Quality picker fixture")
        { Width = 520, Height = 400, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
        host.RootVisual = content; Layout(content, 520, 400);
        pending = bound.ReloadCommand.ExecuteAsync(null); service.Requests.Last().Complete(discovery); AwaitSettings(pending);
        Layout(content, 520, 400);
        var list = (ListBox)window.FindName("QualityList"); list.SelectedIndex = 1;
        Layout(content, 520, 400);
        Check(bound.SelectedQuality?.Height == 1080 && ReferenceEquals(((Button)window.FindName("PlayButton")).Command, bound.PlayCommand),
            "Compiled quality dialog binds selection and playback command without the dashboard");
        window.Close();
    }

    private static async Task VerifyPlaybackManifestServerAsync(StreamQualityOption option)
    {
        using var server = new PlaybackManifestServer();
        var uri = server.Publish(option.Manifest!);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode && body == option.Manifest && response.Content.Headers.ContentType?.MediaType == "application/vnd.apple.mpegurl",
            "Loopback endpoint serves the selected master unchanged with an HLS content type");
        Check(uri.IsLoopback && !uri.AbsoluteUri.Contains("secret") && response.Headers.CacheControl?.NoStore == true,
            "Player URL contains no provider credentials and manifests are not cacheable");
        using var missing = await client.GetAsync(new Uri(uri, "/not-a-playlist"));
        Check(missing.StatusCode == HttpStatusCode.NotFound, "Loopback endpoint serves only registered random paths");
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri));
        Check(head.IsSuccessStatusCode && (await head.Content.ReadAsByteArrayAsync()).Length == 0,
            "Player HEAD probes work without returning a body");
        for (var i = 0; i < 32; i++) server.Publish(option.Manifest!);
        using var evicted = await client.GetAsync(uri);
        Check(evicted.StatusCode == HttpStatusCode.NotFound, "In-memory playlist retention is bounded");
        server.Dispose();
        try { server.Publish(option.Manifest!); Check(false, "Expected disposed server rejection"); }
        catch (ObjectDisposedException) { Check(true, "Window closure disposes the playlist host"); }
    }

    private sealed class QualityHttpHandler : HttpMessageHandler
    {
        public string Body = "";
        public Uri? FinalUri;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Stream? Stream;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = Stream != null ? new StreamContent(Stream) : new StringContent(Body),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, FinalUri ?? request.RequestUri) });
        }
    }
    private sealed class QualityCountingStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public int BytesRead;
        public bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = _inner.Read(buffer, offset, count); BytesRead += read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = await _inner.ReadAsync(buffer, token); BytesRead += read; return read; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class QualityRequest(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<StreamQualityDiscovery> Completion = new();
        public void Complete(StreamQualityDiscovery result) => Completion.SetResult(result);
        public void Fail() => Completion.SetException(new IOException("https://provider.invalid/user/secret failed"));
    }
    private sealed class QualityServiceFake : IStreamQualityService
    {
        public List<QualityRequest> Requests = new();
        public Task<StreamQualityDiscovery> DiscoverAsync(Uri source, CancellationToken token)
        {
            var request = new QualityRequest(token); Requests.Add(request); return request.Completion.Task;
        }
    }
}
