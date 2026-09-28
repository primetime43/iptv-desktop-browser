using System.Collections.Concurrent;
using System.Reflection;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopApp.Controls;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class Program
{
    private static int _passed;
    [STAThread]
    private static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var frame = new DispatcherFrame();
        var work = RunAsync();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = work.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        try { work.GetAwaiter().GetResult(); Console.WriteLine($"Passed {_passed} catalog loading regression checks."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task RunAsync()
    {
        using (var queue = new PrioritizedLoadQueue(1, 3))
        {
            var selected = new Job("selected", 0);
            var visible = new Job("visible", 1);
            var nearby = new Job("nearby", 2);
            var dropped = new Job("dropped", 2);
            queue.Replace([nearby.Request, dropped.Request, visible.Request, selected.Request]);
            Check(selected.Starts == 1 && visible.Starts == 0 && queue.PendingCount == 2, "Selected first; queue capacity bounds pending work");
            selected.Finish();
            await Until(() => visible.Starts == 1);
            Check(nearby.Starts == 0 && dropped.Starts == 0, "Visible work starts before nearby work");
            visible.Finish();
            await Until(() => nearby.Starts == 1);
            nearby.Finish();
            await Until(() => queue.ActiveCount == 0);
            Check(dropped.Starts == 0, "Excess low-priority work is not queued");
        }

        using (var queue = new PrioritizedLoadQueue(2, 8))
        {
            var jobs = Enumerable.Range(0, 8).Select(i => new Job($"old-{i}", 2)).ToArray();
            queue.Replace(jobs.Select(j => j.Request));
            Check(queue.ActiveCount == 2 && jobs.Sum(j => j.Starts) == 2, "Only the configured number of jobs starts");
            var replacement = new Job("new selection", 0);
            queue.Replace([replacement.Request]);
            Check(jobs.Take(2).All(j => j.Token.IsCancellationRequested) && queue.PendingCount == 1,
                "Replacing the viewport cancels obsolete active jobs and discards obsolete pending jobs");
            jobs[0].Finish(); // Simulate a provider ignoring cancellation.
            jobs[1].Finish();
            await Until(() => replacement.Starts == 1);
            replacement.Finish();
            await Until(() => queue.ActiveCount == 0);
            Check(jobs.All(j => j.Applied == 0) && replacement.Applied == 1, "Late obsolete results never apply");
            Check(jobs.Skip(2).All(j => j.Starts == 0), "Discarded category work never reaches the provider");
        }

        using (var queue = new PrioritizedLoadQueue())
        {
            var first = new Job("shared", 1);
            var second = new Job("shared", 0);
            queue.Replace([first.Request, second.Request]);
            Check(first.Starts + second.Starts == 1, "Duplicate keys share one in-flight operation");
            second.Finish();
            await Until(() => queue.ActiveCount == 0);
            Check(first.Applied == 1 && second.Applied == 1, "Shared result reaches every current subscriber");
        }

        using (var queue = new PrioritizedLoadQueue())
        {
            var old = new Job("same channel", 1);
            var current = new Job("same channel", 0);
            queue.Replace([old.Request]);
            queue.Replace([current.Request]);
            Check(!old.Token.IsCancellationRequested && current.Starts == 0, "Retained demand is promoted without restarting its request");
            old.Finish();
            await Until(() => queue.ActiveCount == 0);
            Check(old.Applied == 0 && current.Applied == 1, "Only subscribers in the current viewport receive shared results");
        }

        using (var queue = new PrioritizedLoadQueue(1))
        {
            var old = new Job("A", 1);
            var next = new Job("A", 0);
            queue.Replace([old.Request]);
            queue.Cancel();
            queue.Replace([next.Request]);
            old.Finish();
            await Until(() => next.Starts == 1);
            next.Finish();
            await Until(() => queue.ActiveCount == 0);
            Check(old.Applied == 0 && next.Applied == 1, "Returning to a cancelled key starts a fresh request");
        }

        using (var lifetime = new CancellationTokenSource())
        using (var queue = new PrioritizedLoadQueue(lifetimeToken: lifetime.Token))
        {
            var job = new Job("closing", 0);
            queue.Replace([job.Request]);
            lifetime.Cancel();
            job.Finish();
            await Until(() => queue.ActiveCount == 0);
            queue.Replace([new Job("after close", 0).Request]);
            Check(job.Token.IsCancellationRequested && job.Applied == 0 && queue.ActiveCount == 0, "Window lifetime cancels work and blocks further starts");
        }

        await VerifyResources();
        await VerifySelectedDetails();
        await VerifySelectedGuide();
        await VerifyBackoffAndAccounts();
        await VerifyMemoryImages();
    }

    private static async Task VerifyBackoffAndAccounts()
    {
        var clock = new TestClock();
        var cache = DispatchProxy.Create<ICacheService, FakeService>();
        var channels = DispatchProxy.Create<IChannelService, FakeService>();
        var imageCalls = 0;
        var guideCalls = 0;
        ((FakeService)(object)cache).Handler = (_, _) =>
        {
            Interlocked.Increment(ref imageCalls);
            return Task.FromResult<BitmapImage?>(null);
        };
        ((FakeService)(object)channels).Handler = (_, _) =>
        {
            Interlocked.Increment(ref guideCalls);
            return Task.FromException(new IOException("Provider offline"));
        };
        using var loader = new CatalogResourceLoader(channels, cache, _ => { }, CancellationToken.None, clock);
        var channel = new Channel { Id = 1, Logo = "https://example.test/logo", EpgChannelId = "guide" };
        loader.Update([new(channel, 0)], "account-a", true);
        await Until(() => imageCalls == 1 && guideCalls == 1 && !channel.EpgLoading);
        await Task.Delay(20);
        loader.Update([new(channel, 0)], "account-a", true);
        await Task.Delay(20);
        Check(imageCalls == 1 && guideCalls == 1, "Repeated layout does not hammer failed image or guide endpoints");
        clock.Now = clock.Now.AddMinutes(2);
        loader.Update([new(channel, 0)], "account-a", true);
        await Until(() => imageCalls == 2 && guideCalls == 2 && !channel.EpgLoading);
        Check(channel.NextEpgRefreshUtc > clock.Now.UtcDateTime, "Failed guides receive a timed retry");
        var other = new Channel { Id = 1, Logo = channel.Logo, EpgChannelId = "guide" };
        loader.Update([new(other, 0)], "account-b", true);
        await Until(() => imageCalls == 3 && guideCalls == 3 && !other.EpgLoading);
        Check(true, "Account changes do not reuse another account's guide or retry state");
        var m3u = new Channel { Id = 2, EpgChannelId = "xmltv" };
        loader.Update([new(m3u, 0)], "playlist", false);
        await Task.Delay(20);
        Check(guideCalls == 3, "M3U guides do not issue Xtream requests");
    }

    private static async Task VerifyMemoryImages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iptv-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pixels = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(pixels));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var bytes = stream.ToArray();
            var http = DispatchProxy.Create<IHttpService, FakeService>();
            var calls = 0;
            ((FakeService)(object)http).Handler = (_, _) => { calls++; return Task.FromResult(bytes); };
            Session.CachingEnabled = false;
            var cache = new PersistentCacheService(http, new SessionService(NullLogger<SessionService>.Instance),
                NullLogger<PersistentCacheService>.Instance, directory);
            var first = await cache.GetImageAsync("https://example.test/image");
            var second = await cache.GetImageAsync("https://example.test/image");
            Check(first?.IsFrozen == true && ReferenceEquals(first, second) && calls == 1,
                "Disabling disk caching still downloads once and reuses an image in memory");
            Check(!Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(), "Disk-disabled image loading writes no cache files");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((FakeService)(object)http).Handler = (_, args) => Download(args);
            async Task<byte[]> Download(object?[]? args)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, (CancellationToken)args![1]!);
                return bytes;
            }
            using var stop = new CancellationTokenSource();
            var pending = cache.GetImageAsync("https://example.test/cancel", stop.Token);
            await entered.Task;
            stop.Cancel();
            try { await pending; throw new InvalidOperationException("Cancellation was swallowed"); }
            catch (OperationCanceledException) { Check(true, "Image cache propagates cancellation to release the queue slot"); }
        }
        finally
        {
            // This path is the unique temporary directory created by this test.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var subdir in Directory.EnumerateDirectories(directory)) Directory.Delete(subdir);
            Directory.Delete(directory);
        }
    }

    private static async Task VerifyResources()
    {
        var cache = DispatchProxy.Create<ICacheService, FakeService>();
        var channels = DispatchProxy.Create<IChannelService, FakeService>();
        var imageCalls = new ConcurrentQueue<CancellationToken>();
        var guideCalls = new ConcurrentQueue<(Channel, CancellationToken)>();
        var imageResponse = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guideResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((FakeService)(object)cache).Handler = (_, args) =>
        {
            imageCalls.Enqueue((CancellationToken)args![1]!);
            return imageResponse.Task;
        };
        ((FakeService)(object)channels).Handler = async (_, args) =>
        {
            var target = (Channel)args![0]!;
            guideCalls.Enqueue((target, (CancellationToken)args[1]!));
            await guideResponse.Task;
            target.EpgSchedule = new EpgData
            {
                Programs = [new() { Title = "Shared show", StartUtc = DateTime.UtcNow.AddMinutes(-1), EndUtc = DateTime.UtcNow.AddMinutes(10) }],
                ExpiresUtc = DateTime.UtcNow.AddMinutes(10)
            };
            target.NextEpgRefreshUtc = target.EpgSchedule.ExpiresUtc;
        };
        var uiThread = Environment.CurrentManagedThreadId;
        var notifications = 0;
        using var loader = new CatalogResourceLoader(channels, cache, _ =>
        {
            Check(Environment.CurrentManagedThreadId == uiThread, "Guide results apply on the UI context");
            notifications++;
        }, CancellationToken.None);
        var first = new Channel { Id = 42, EpgChannelId = "guide", Logo = "https://example.test/image" };
        var second = new Channel { Id = 42, EpgChannelId = "guide", Logo = first.Logo };
        var movie = new VodContent { StreamIcon = first.Logo };
        CatalogViewportItem[] snapshot = [new(first, 0), new(second, 1), new(movie, 2)];
        loader.Update(snapshot, "account", true);
        await Until(() => imageCalls.Count == 1 && guideCalls.Count == 1);
        loader.Update(snapshot, "account", true);
        Check(imageCalls.Count == 1 && guideCalls.Count == 1, "Repeated layouts share image and EPG requests across model instances");
        Check(first.EpgLoading && second.EpgLoading && first.NowTitle == null, "Loading models remain separate from background parsing");
        var image = new BitmapImage();
        image.Freeze();
        imageResponse.SetResult(image);
        guideResponse.SetResult();
        await Until(() => notifications == 2 && movie.PosterImage != null);
        Check(ReferenceEquals(first.LogoImage, second.LogoImage) && ReferenceEquals(first.LogoImage, movie.PosterImage), "Channel and movie cards share the same image result");
        Check(first.NowTitle == "Shared show" && ReferenceEquals(first.EpgSchedule, second.EpgSchedule) && !first.EpgLoading,
            "Selected and visible cards share one full guide");
        loader.Update([], "account", true);
        var revisited = new Channel { Id = 42, EpgChannelId = "guide" };
        loader.Update([new(revisited, 0)], "account", true);
        Check(revisited.NowTitle == "Shared show" && guideCalls.Count == 1, "Revisiting a channel reuses a valid in-memory guide");

        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((FakeService)(object)channels).Handler = async (_, args) =>
        {
            var target = (Channel)args![0]!;
            guideCalls.Enqueue((target, (CancellationToken)args[1]!));
            await late.Task;
            target.EpgSchedule = first.EpgSchedule;
        };
        var obsolete = new Channel { Id = 43, EpgChannelId = "guide" };
        loader.Update([new(obsolete, 1)], "account", true);
        await Until(() => guideCalls.Count == 2);
        loader.Update([], "account", true);
        late.SetResult();
        await Task.Delay(50);
        Check(guideCalls.Last().Item2.IsCancellationRequested && obsolete.EpgSchedule == null && !obsolete.EpgLoading,
            "Offscreen requests cancel and cannot mutate models even if their provider ignores cancellation");
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Expected load did not complete");
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
    }

    private sealed class Job(string key, int priority)
    {
        private readonly TaskCompletionSource<object?> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;
        public int Applied;
        public CancellationToken Token;
        public ViewportLoadRequest Request => new(key, priority, token => { Starts++; Token = token; return _response.Task; }, _ => Applied++);
        public void Finish() => _response.SetResult(key);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public class FakeService : DispatchProxy
{
    public Func<MethodInfo?, object?[]?, Task>? Handler;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler!(targetMethod, args);
}
