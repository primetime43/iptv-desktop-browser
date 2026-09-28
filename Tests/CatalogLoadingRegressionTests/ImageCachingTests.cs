using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopApp.Controls;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class Program
{
    private static async Task VerifyThumbnailCaching()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iptv-thumbnail-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Session.Mode = SessionMode.Xtream;
            Session.Host = "images.example.test";
            Session.Port = 80;
            Session.UseSsl = false;
            Session.Username = "account-a";
            Session.Password = "private-password";
            Session.CachingEnabled = false;
            var session = new SessionService(NullLogger<SessionService>.Instance);
            var http = DispatchProxy.Create<IHttpService, FakeService>();
            var calls = 0;
            var bytes = Png(1200, 800);
            ((FakeService)(object)http).Handler = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(bytes); };
            PersistentCacheService Create() => new(http, session, NullLogger<PersistentCacheService>.Instance, directory);
            var cache = Create();
            const string url = "https://images.example.test/logo.png?token=secret";

            var small = await cache.GetImageAsync(url, 64, 96);
            Check(small is { PixelWidth: 64, PixelHeight: 42, IsFrozen: true }, "Thumbnails fit the display bounds and preserve aspect ratio");
            var pixels = new byte[small!.PixelWidth * small.PixelHeight * 4];
            small.CopyPixels(pixels, small.PixelWidth * 4, 0);
            Check(!small.StreamSource.CanRead && pixels.Any(v => v != 0), "Frozen thumbnails render after the encoded source buffer is released");
            var repeat = await cache.GetImageAsync(url, 64, 96);
            Check(ReferenceEquals(small, repeat) && calls == 1, "Memory-only thumbnails download once and reuse the same bitmap");
            var big = await cache.GetImageAsync(url, 320, 320);
            Check(big is { PixelWidth: 320, PixelHeight: 213 } && !ReferenceEquals(big, small), "Larger display requests never reuse undersized thumbnails");
            Check(!Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(), "Memory-only thumbnails do not create disk files");

            var firstLogo = await cache.GetChannelLogoAsync(7, url);
            var requests = calls;
            var sameSource = await cache.GetChannelLogoAsync(99, url);
            Check(ReferenceEquals(firstLogo, sameSource) && calls == requests, "Different channel IDs sharing a source reuse the logo");
            var changed = await cache.GetChannelLogoAsync(7, url + "&version=2");
            Check(!ReferenceEquals(firstLogo, changed) && calls == requests + 1, "A changed source URL invalidates the logo for the same channel ID");
            Session.Username = "account-b";
            var otherAccount = await cache.GetChannelLogoAsync(7, url);
            Check(!ReferenceEquals(firstLogo, otherAccount) && calls == requests + 2, "Matching channel IDs and URLs never leak logos across accounts");
            foreach (var change in new Action[] { () => Session.Host = "other.test", () => Session.Port = 8080, () => Session.UseSsl = true, () => Session.Password = "changed" })
            {
                requests = calls;
                change();
                await cache.GetChannelLogoAsync(7, url);
                Check(calls == requests + 1, "Server, port, scheme and credentials isolate image caches");
            }

            // Disk hits use the same decoder/size policy and can create a larger variant without HTTP.
            Session.CachingEnabled = true;
            const string diskUrl = "https://images.example.test/disk.png";
            await cache.GetImageAsync(diskUrl, 64, 64);
            requests = calls;
            var reopened = Create();
            var diskImage = await reopened.GetImageAsync(diskUrl, 320, 320);
            Check(calls == requests && diskImage is { PixelWidth: 320, PixelHeight: 213 }, "Disk reuse survives service recreation and honors new display dimensions");
            Check(Directory.GetFiles(Path.Combine(directory, "Images")).All(p => !p.Contains("private-password") && !p.Contains("secret") && !p.Contains("account-")), "Image filenames contain no source credentials or account names");
            Session.Username = "account-c";
            await Create().GetImageAsync(diskUrl, 320, 320);
            Check(calls == requests + 1, "Disk images are also isolated across accounts");

            File.WriteAllBytes(Path.Combine(directory, "Images", "channel_logo_777.jpg"), bytes);
            requests = calls;
            await Create().GetChannelLogoAsync(777, "https://images.example.test/new-logo.png");
            Check(calls == requests + 1, "Legacy ID-only disk logos are not assigned to an arbitrary account");
            var corruptPath = Path.Combine(directory, "Images", ThumbnailImage.SourceKey(session, diskUrl) + ".img");
            File.WriteAllText(corruptPath, "broken image");
            requests = calls;
            Check(await Create().GetImageAsync(diskUrl, 64, 64) != null && calls == requests + 1, "Corrupt disk images are refetched");

            const string unwritableUrl = "https://images.example.test/blocked.png";
            Directory.CreateDirectory(Path.Combine(directory, "Images", ThumbnailImage.SourceKey(session, unwritableUrl) + ".img"));
            requests = calls;
            var memoryFallback = Create();
            var downloaded = await memoryFallback.GetImageAsync(unwritableUrl, 64, 64);
            Check(downloaded != null && ReferenceEquals(downloaded, await memoryFallback.GetImageAsync(unwritableUrl, 64, 64)) && calls == requests + 1,
                "Disk write failures still return and memory-cache successful downloads");

            Session.CachingEnabled = false;
            var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            ((FakeService)(object)http).Handler = (_, _) => { Interlocked.Increment(ref calls); return gate.Task; };
            requests = calls;
            var one = cache.GetImageAsync(url + "&concurrent", 64, 64);
            var two = cache.GetImageAsync(url + "&concurrent", 64, 64);
            gate.SetResult(bytes);
            Check(ReferenceEquals(await one, await two) && calls == requests + 1, "Concurrent identical thumbnails share the first cached download");
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            try { await cache.GetImageAsync(url + "&concurrent", 64, 64, stop.Token); throw new InvalidOperationException("Ignored cancellation"); }
            catch (OperationCanceledException) { Check(true, "Cancelled requests cannot return cached images"); }

            var budget = new ThumbnailMemoryCache();
            var large = ThumbnailImage.Decode(bytes, ThumbnailSize.Create(1200, 800));
            for (var i = 0; i < 40; i++) budget.Store(i.ToString(), large);
            Check(budget.Count < 40 && budget.Bytes <= 64 * 1024 * 1024, "Decoded-pixel memory budget evicts images before the entry-count limit");
            Check(!budget.TryGetValue("0", out _) && budget.TryGetValue("39", out _), "Eviction retains recent thumbnails");
            var tiny = ThumbnailImage.Decode(Png(1, 1), ThumbnailSize.Create(320, 320));
            Check(tiny.PixelWidth == 1 && tiny.PixelHeight == 1, "Small sources are not enlarged in memory");
            for (var i = 0; i < 600; i++) budget.Store("tiny" + i, tiny);
            Check(budget.Count == 500, "Thumbnail memory cache also enforces the entry limit");

            ((FakeService)(object)http).Handler = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(bytes); };
            var fallback = new CacheService(http, NullLogger<CacheService>.Instance, session);
            var fallbackImage = await fallback.GetImageAsync(url, 64, 64);
            Check(fallbackImage is { PixelWidth: 64, PixelHeight: 42 } &&
                ReferenceEquals(fallbackImage, await fallback.GetImageAsync(url, 64, 64)),
                "The alternate memory cache also sizes and reuses thumbnails");
            var vod = new VodService(session, http, cache, NullLogger<VodService>.Instance);
            var movie = new VodContent { StreamIcon = url };
            var series = new SeriesContent { StreamIcon = url };
            await vod.LoadVodPosterAsync(movie);
            await vod.LoadSeriesPosterAsync(series);
            Check(movie.PosterImage != null && ReferenceEquals(movie.PosterImage, series.PosterImage),
                "Both poster service methods use shared memory caching with disk caching disabled");

            await VerifyThumbnailResize(bytes);
        }
        finally
        {
            // Only this test's freshly-created unique directory is removed.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var subdir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.Delete(subdir);
            Directory.Delete(directory);
        }
    }

    private static async Task VerifyThumbnailResize(byte[] bytes)
    {
        var cache = DispatchProxy.Create<ICacheService, FakeService>();
        var channels = DispatchProxy.Create<IChannelService, FakeService>();
        var sizes = new List<(int Width, int Height)>();
        ((FakeService)(object)cache).Handler = (_, args) =>
        {
            var size = new ThumbnailSize((int)args![1]!, (int)args[2]!);
            lock (sizes) sizes.Add((size.Width, size.Height));
            return Task.FromResult<BitmapImage?>(ThumbnailImage.Decode(bytes, size));
        };
        var movie = new VodContent { StreamIcon = "https://images.example.test/poster.png" };
        using var loader = new CatalogResourceLoader(channels, cache, _ => { }, CancellationToken.None);
        loader.Update([new(movie, 1, 64, 96)], "account", false);
        await Until(() => movie.PosterImage?.PixelWidth == 64);
        loader.Update([new(movie, 1, 320, 320)], "account", false);
        await Until(() => movie.PosterImage?.PixelWidth == 320);
        Check(sizes.SequenceEqual([(64, 96), (320, 320)]), "Resizing a visible card requests a suitably sized replacement thumbnail");
        loader.Update([new(movie, 1, 320, 320)], "account", false);
        await Task.Delay(30);
        Check(sizes.Count == 2, "Repeated layout does not reload an unchanged thumbnail size");
    }

    private static byte[] Png(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
