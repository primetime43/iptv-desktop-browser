using System.Collections.Concurrent;
using System.Reflection;
using DesktopApp.Models;
using DesktopApp.Services;

internal static partial class Program
{
    private static async Task VerifySelectedDetails()
    {
        await VerifyDetailsType<VodContent>((target, details) => target.ApplyDetails(details));
        await VerifyDetailsType<SeriesContent>((target, details) => target.ApplyDetails(details));

        var loader = new SelectedDetailsLoader();
        var movie = new VodContent { Id = 1 };
        var series = new SeriesContent { Id = 1 };
        var old = new DetailJob<VodContent>();
        var current = new DetailJob<SeriesContent>();
        var errors = 0;
        var oldTask = loader.LoadAsync(movie, old.Load, (t, d) => t.ApplyDetails(d), _ => errors++, () => true, CancellationToken.None);
        var currentTask = loader.LoadAsync(series, current.Load, (t, d) => t.ApplyDetails(d), _ => errors++, () => true, CancellationToken.None);
        Check(old.Token.IsCancellationRequested && !movie.DetailsLoading && series.DetailsLoading,
            "Switching movie/series tabs cancels the previous detail request even when IDs match");
        current.Finish("Series details");
        await currentTask;
        old.Finish("Old movie details");
        await oldTask;
        Check(series.Plot == "Series details" && movie.Plot == null && !movie.DetailsLoaded && errors == 0,
            "Late movie completion cannot mutate the bound model or replace series details");
    }

    private static async Task VerifyDetailsType<T>(Action<T, T> apply) where T : class, IWatchableContent, new()
    {
        var loader = new SelectedDetailsLoader();
        var a = new T { Id = 1, Cast = "Catalog cast" };
        var b = new T { Id = 2 };
        T? selected = a;
        var errors = 0;
        Task Start(T target, DetailJob<T> job) => loader.LoadAsync(target, job.Load, apply,
            _ => errors++, () => ReferenceEquals(selected, target), CancellationToken.None);

        var first = new DetailJob<T>();
        var firstTask = Start(a, first);
        await Start(a, new DetailJob<T>()); // Setter + panel both request the same details.
        Check(first.Starts == 1 && a.DetailsLoading && first.Target != a, "Duplicate detail requests are suppressed and service mutations use a detached model");
        selected = b;
        var middle = new DetailJob<T>();
        var middleTask = Start(b, middle);
        selected = a;
        var last = new DetailJob<T>();
        var lastTask = Start(a, last);
        Check(first.Token.IsCancellationRequested && middle.Token.IsCancellationRequested && !last.Token.IsCancellationRequested,
            "A-B-A details cancel both superseded requests");
        first.Finish("Outdated A");
        await firstTask;
        middle.Response.SetException(new InvalidOperationException("Stale failure"));
        await middleTask;
        Check(a.Plot == null && !a.DetailsLoaded && a.DetailsLoading && errors == 0,
            "Stale details and failures cannot alter the current model or reset its loading flag");
        last.Finish("Current A");
        await lastTask;
        Check(a.Plot == "Current A" && a.Cast == "Catalog cast" && a.DetailsLoaded && !a.DetailsLoading,
            "Current details merge once and preserve catalog metadata");

        selected = b;
        var pending = new DetailJob<T>();
        var pendingTask = Start(b, pending);
        selected = a; // Already loaded: selecting it still supersedes B.
        await Start(a, new DetailJob<T>());
        pending.Finish("Late B");
        await pendingTask;
        Check(pending.Token.IsCancellationRequested && b.Plot == null && !b.DetailsLoading,
            "Already-loaded details cancel pending work without another fetch");

        selected = b;
        var cancelled = new DetailJob<T>();
        var cancelledTask = Start(b, cancelled);
        loader.Cancel(); // Deselect, navigate away, or close the panel.
        Check(cancelled.Token.IsCancellationRequested && !b.DetailsLoading, "Explicit cancellation immediately resets the old model's loading flag");
        cancelled.Finish("Cancelled B");
        await cancelledTask;
        Check(!b.DetailsLoaded && b.Plot == null, "Ignored cancellation cannot commit details after the panel is cleared");

        var guarded = new DetailJob<T>();
        var guardedTask = Start(b, guarded);
        selected = null; // The result guard is required even without explicit cancellation.
        guarded.Finish("Wrong selection");
        await guardedTask;
        Check(b.Plot == null && !b.DetailsLoading, "Selection is rechecked before applying details and cleanup still completes");

        selected = b;
        var failed = new DetailJob<T>();
        var failedTask = Start(b, failed);
        failed.Response.SetException(new InvalidOperationException("Current failure"));
        await failedTask;
        Check(errors == 1 && !b.DetailsLoaded && !b.DetailsLoading, "Current errors surface and leave details retryable");
        using var lifetime = new CancellationTokenSource();
        var closing = new DetailJob<T>();
        var closingTask = loader.LoadAsync(b, closing.Load, apply, _ => errors++, () => true, lifetime.Token);
        lifetime.Cancel();
        closing.Finish("After closing");
        await closingTask;
        Check(closing.Token.IsCancellationRequested && b.Plot == null && !b.DetailsLoading && errors == 1,
            "Window lifetime cancels details without applying results or reporting stale errors");
        var retry = new DetailJob<T>();
        var retryTask = Start(b, retry);
        retry.Finish("Retried B");
        await retryTask;
        Check(b.Plot == "Retried B" && b.DetailsLoaded, "A cancelled or failed selection can be loaded again");
    }

    private static async Task VerifySelectedGuide()
    {
        var channels = DispatchProxy.Create<IChannelService, FakeService>();
        var cache = DispatchProxy.Create<ICacheService, FakeService>();
        var jobs = new ConcurrentDictionary<int, (Channel Target, CancellationToken Token, TaskCompletionSource Done)>();
        var finished = 0;
        ((FakeService)(object)channels).Handler = (_, args) => Fetch((Channel)args![0]!, (CancellationToken)args[1]!);
        async Task Fetch(Channel target, CancellationToken token)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            jobs[target.Id] = (target, token, done);
            await done.Task; // Deliberately ignore cancellation.
            var now = DateTime.UtcNow;
            target.EpgSchedule = new EpgData
            {
                CachedAt = now, ExpiresUtc = now.AddHours(2),
                Programs = [new EpgEntry { Title = target.Name, StartUtc = now.AddMinutes(10), EndUtc = now.AddHours(1) }]
            };
            Interlocked.Increment(ref finished);
        }
        var a = new Channel { Id = 1, Name = "A" };
        var b = new Channel { Id = 2, Name = "B" };
        Channel? selected = a;
        var displayed = "";
        using var loader = new CatalogResourceLoader(channels, cache, channel =>
        {
            if (ReferenceEquals(channel, selected)) displayed = channel.EpgSchedule!.Programs![0].Title;
        }, CancellationToken.None);
        loader.Update([new(a, 0)], "account", true);
        await Until(() => jobs.ContainsKey(1));
        selected = b;
        loader.Update([new(b, 0)], "account", true);
        await Until(() => jobs.ContainsKey(2));
        Check(jobs[1].Token.IsCancellationRequested && !a.EpgLoading, "Changing selected channel cancels obsolete guide demand");
        jobs[2].Done.SetResult();
        await Until(() => displayed == "B");
        jobs[1].Done.SetResult();
        await Until(() => finished == 2);
        await Task.Delay(30);
        Check(displayed == "B" && a.EpgSchedule == null, "A late guide cannot replace the new channel's upcoming list or mutate the old card");

        var c = new Channel { Id = 3, Name = "C" };
        selected = c;
        loader.Update([new(c, 0)], "account", true);
        await Until(() => jobs.ContainsKey(3));
        selected = null;
        displayed = "";
        loader.Update([], "account", true);
        jobs[3].Done.SetResult();
        await Until(() => finished == 3);
        await Task.Delay(30);
        Check(jobs[3].Token.IsCancellationRequested && displayed == "" && c.EpgSchedule == null,
            "Deselecting clears upcoming programs and rejects a late guide completion");

        var d = new Channel { Id = 4, Name = "D", EpgChannelId = "d" };
        var e = new Channel { Id = 5, Name = "E", EpgChannelId = "e" };
        selected = d;
        loader.Update([new(d, 0)], "account", true);
        await Until(() => jobs.ContainsKey(4));
        selected = e;
        // D is still visible, so its guide remains useful to its card.
        loader.Update([new(e, 0), new(d, 1)], "account", true);
        await Until(() => jobs.ContainsKey(5));
        jobs[5].Done.SetResult();
        await Until(() => displayed == "E");
        jobs[4].Done.SetResult();
        await Until(() => d.EpgSchedule != null);
        Check(!jobs[4].Token.IsCancellationRequested && displayed == "E",
            "A retained visible card can finish its guide without replacing the selected channel's upcoming programs");
    }

    private sealed class DetailJob<T> where T : IWatchableContent
    {
        public TaskCompletionSource<string> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public T? Target;
        public int Starts;
        public async Task Load(T target, CancellationToken token)
        {
            Token = token;
            Target = target;
            Starts++;
            target.Plot = await Response.Task;
            target.DetailsLoaded = true;
        }
        public void Finish(string plot) => Response.SetResult(plot);
    }
}
