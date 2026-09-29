using System.Reflection;
using System.Text.Json;
using DesktopApp.Configuration;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class Program
{
    private static async Task VerifyBackgroundCatalogs()
    {
        Session.Mode = SessionMode.Xtream;
        Session.Host = "example.test";
        Session.CachingEnabled = true;
        var uiThread = Environment.CurrentManagedThreadId;
        var http = DispatchProxy.Create<IHttpService, FakeService>();
        var cache = DispatchProxy.Create<ICacheService, FakeService>();
        List<Channel>? savedChannels = null;
        var httpThread = 0;
        var cacheThread = 0;
        var writeThread = 0;
        var added = DateTimeOffset.UtcNow.AddDays(-1);
        var payload = JsonSerializer.Serialize(Enumerable.Range(1, 10000).Select(i => new { stream_id = i, name = "Channel " + i, added = added.ToUnixTimeSeconds() }));
        ((FakeService)(object)http).Handler = (_, args) =>
        {
            httpThread = Environment.CurrentManagedThreadId;
            return Task.FromResult(((string)args![0]!).Contains("get_live_categories")
                ? "[{\"category_id\":\"1\",\"category_name\":\"News\",\"parent_id\":0}]" : payload);
        };
        ((FakeService)(object)cache).Handler = (method, args) =>
        {
            if (method!.Name == nameof(ICacheService.SetDataAsync))
            {
                writeThread = Environment.CurrentManagedThreadId;
                if (args![1] is List<Channel> channels) savedChannels = channels;
                return Task.CompletedTask;
            }
            cacheThread = Environment.CurrentManagedThreadId;
            return method.GetGenericArguments()[0] == typeof(List<Channel>)
                ? Task.FromResult(savedChannels) : Task.FromResult<List<Category>?>(null);
        };
        var service = new ChannelService(new SessionService(NullLogger<SessionService>.Instance), http, cache,
            NullLogger<ChannelService>.Instance, new ApiSettings(), new CacheSettings(), new BatchProcessingSettings(),
            new M3uSettings(), new EpgSettings());
        var result = await service.LoadChannelsForCategoryAsync(new Category { Id = "1" });
        Check(result.Count == 10000 && result[9999].Name == "Channel 10000", "Large live catalogs preserve every parsed row");
        Check(httpThread != uiThread && cacheThread != uiThread && writeThread != uiThread,
            "Even synchronous HTTP and cache completions prepare live catalogs off the UI thread");
        var notifications = 0;
        result[0].PropertyChanged += (_, _) => notifications++;
        var revisit = await service.LoadChannelsForCategoryAsync(new Category { Id = "1" });
        Check(!ReferenceEquals(revisit[0], result[0]) && notifications == 0,
            "Cache-hit preparation creates detached models without background changes to bound channels");
        Check(result[0].AddedUtc?.ToUnixTimeSeconds() == added.ToUnixTimeSeconds() && revisit[0].AddedUtc == result[0].AddedUtc,
            "Provider channel dates survive parsing and detached cache-hit preparation");
        var categories = await service.LoadCategoriesAsync();
        Check(categories.Single().Name == "News" && httpThread != uiThread && writeThread != uiThread,
            "Category parsing and cache persistence also run in background work");
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        try { await service.LoadCategoriesAsync(stopped.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(true, "Cancelled catalog preparation cannot start"); }
    }
}
