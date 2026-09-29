using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

internal static partial class Program
{
    private static void VerifyRecentlyAddedChannels()
    {
        VerifyChannelDiscoveryHistory();
        VerifyRecentChannelSource();
        VerifyRecentChannelFiltering();
    }

    private static void VerifyChannelDiscoveryHistory()
    {
        using var fixture = new ChannelHistoryFixture();
        var now = fixture.Clock.Now;
        var seconds = now.ToUnixTimeSeconds().ToString();
        Check(Channel.ParseAddedDate(seconds) == now && Channel.ParseAddedDate(now.ToUnixTimeMilliseconds().ToString()) == now,
            "Added dates accept Unix seconds and milliseconds");
        Check(Channel.ParseAddedDate("2030-01-15T02:00:00+02:00") == now,
            "Added date strings normalize timezone offsets");
        Check(new string?[] { null, "", "junk", "0", "-1", "9223372036854775807", "{}" }.All(s => Channel.ParseAddedDate(s) == null),
            "Missing and invalid provider dates do not break loading");
        List<Channel> initial = [new() { Id = 1 }, new() { Id = 2, AddedUtc = now.AddDays(-1) },
            new() { Id = 3, AddedUtc = now.AddDays(-8) }, new() { Id = 4, AddedUtc = now.AddDays(1) }];
        fixture.Apply("account/password?secret", "category:a", initial);
        Check(!initial[0].IsRecentlyAdded && initial[0].DiscoveredUtc == null, "First visit establishes a quiet baseline for undated channels");
        Check(initial[1].IsRecentlyAdded && initial[1].RecentlyAddedText.StartsWith("Added by provider"), "Provider dates highlight recent additions on the first visit");
        Check(!initial[2].IsRecentlyAdded && !initial[3].IsRecentlyAdded && initial[3].AddedUtc == null,
            "Old and future provider dates cannot produce first-visit badges");
        fixture.Clock.Now = now.AddDays(1);
        List<Channel> revisit = [new() { Id = 1 }, new() { Id = 2 }, new() { Id = 5 }, new() { Id = 6, AddedUtc = now.AddDays(-20) }];
        fixture.Apply("account/password?secret", "category:a", revisit);
        Check(!revisit[0].IsRecentlyAdded && revisit[1].AddedUtc == initial[1].AddedUtc, "Known channels retain provider dates across snapshots");
        Check(revisit[2].IsRecentlyAdded && revisit[2].DiscoveredUtc == fixture.Clock.Now && revisit[2].RecentlyAddedText.StartsWith("First discovered"),
            "Later undated additions use discovery time");
        Check(!revisit[3].IsRecentlyAdded, "An old provider date takes precedence over recent discovery");
        var restarted = new ChannelHistoryStore(fixture.Directory, fixture.Clock);
        List<Channel> restored = [new() { Id = 5 }];
        restarted.Apply("account/password?secret", null, restored, c => c.Id.ToString(), default);
        Check(restored[0].DiscoveredUtc == revisit[2].DiscoveredUtc && restored[0].IsRecentlyAdded,
            "History survives restart and favorites can read it without changing the baseline");
        List<Channel> moved = [new() { Id = 1 }, new() { Id = 7 }];
        fixture.Apply("account/password?secret", "category:b", moved);
        Check(moved.All(c => !c.IsRecentlyAdded), "Opening another existing category does not flag its entire catalog");
        List<Channel> other = [new() { Id = 5 }];
        fixture.Apply("other-account", "category:a", other);
        Check(other[0].DiscoveredUtc == null && !other[0].IsRecentlyAdded, "Matching IDs in other accounts cannot inherit new status");
        fixture.Apply("account/password?secret", "category:a", []);
        fixture.Apply("account/password?secret", "category:a", moved);
        Check(moved.All(c => !c.IsRecentlyAdded), "Removed and moved known channels are not rediscovered as new");
        fixture.Clock.Now = revisit[2].DiscoveredUtc!.Value.AddDays(7);
        fixture.Apply("account/password?secret", null, restored);
        Check(!restored[0].IsRecentlyAdded, "Discovery badges expire exactly seven days after discovery");
        var boundary = new Channel { AddedUtc = fixture.Clock.Now.AddDays(-7) };
        Check(!boundary.IsRecentAt(fixture.Clock.Now) && boundary.IsRecentAt(fixture.Clock.Now.AddTicks(-1)), "Provider dates follow the same seven-day boundary");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { fixture.Store.Apply("cancelled", "category:a", [new() { Id = 9 }], c => c.Id.ToString(), cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(true, "Cancelled loads cannot establish discovery baselines"); }
        List<Channel> afterCancel = [new() { Id = 10 }];
        fixture.Apply("cancelled", "category:a", afterCancel);
        Check(!afterCancel[0].IsRecentlyAdded, "The first successful snapshot after cancellation is still a baseline");

        fixture.Apply("complete", "all", [new() { Id = 1 }]);
        List<Channel> afterComplete = [new() { Id = 2 }];
        fixture.Apply("complete", "category:unvisited", afterComplete);
        Check(afterComplete[0].IsRecentlyAdded, "A complete-catalog baseline also detects additions in unvisited categories");
        var persisted = string.Join("", System.IO.Directory.GetFiles(fixture.Directory).Select(File.ReadAllText));
        Check(!persisted.Contains("password") && !persisted.Contains("secret") && !persisted.Contains("category:"),
            "History persists only opaque identities and dates");
        var corruptPath = Path.Combine(fixture.Directory, ChannelHistoryStore.Key("corrupt") + ".json");
        File.WriteAllText(corruptPath, "{broken");
        List<Channel> recover = [new() { Id = 1 }];
        fixture.Apply("corrupt", "category:a", recover);
        Check(!recover[0].IsRecentlyAdded, "Corrupt history recovers with a fresh baseline instead of flagging everything");
        var blockedDirectory = Path.Combine(fixture.Directory, "file"); File.WriteAllText(blockedDirectory, "file");
        var unavailable = new ChannelHistoryStore(blockedDirectory, fixture.Clock);
        List<Channel> dated = [new() { Id = 1, AddedUtc = fixture.Clock.Now }];
        unavailable.Apply("account", "all", dated, c => c.Id.ToString(), default);
        Check(dated[0].IsRecentlyAdded, "Unavailable history storage does not block provider-date badges or browsing");
    }

    private static void VerifyRecentChannelSource()
    {
        using var fixture = new ChannelHistoryFixture();
        var mode = Session.Mode; var playlist = Session.PlaylistChannels; var origin = Session.PlaylistSource;
        var host = Session.Host; var user = Session.Username;
        try
        {
            using var http = new System.Net.Http.HttpClient(new RecentChannelHttpHandler(fixture.Clock.Now));
            var source = new LiveCatalogSource(new CatalogChannelService(), http, fixture.Store);
            Session.Mode = SessionMode.M3u; Session.PlaylistSource = "https://playlist.test/list?password=private";
            Session.PlaylistChannels = [new() { Id = 1, Name = "One", Category = "A", StreamUrl = "https://stream.test/one?secret=private" }];
            var first = source.LoadAllChannelsAsync(default); AwaitSettings(first);
            Check(!first.Result.Single().IsRecentlyAdded, "Playlist's first complete catalog is a baseline");
            Session.PlaylistChannels = [new() { Id = 1, Name = "Two", Category = "A", StreamUrl = "https://stream.test/two" },
                new() { Id = 2, Name = "One", Category = "A", StreamUrl = "https://stream.test/one?secret=private" }];
            var next = source.LoadChannelsAsync(new Category { Id = "A" }, default); AwaitSettings(next);
            Check(next.Result[0].IsRecentlyAdded && !next.Result[1].IsRecentlyAdded, "Playlist insertion uses stream identity, not shifted numeric IDs");
            Session.PlaylistSource = "https://another-playlist.test/list";
            var other = source.LoadAllChannelsAsync(default); AwaitSettings(other);
            Check(other.Result.All(c => !c.IsRecentlyAdded), "Different playlists have separate baselines even with matching streams");
            Session.Mode = SessionMode.Xtream; Session.Host = "https://provider.test"; Session.Username = "user1";
            var xtream = source.LoadAllChannelsAsync(default); AwaitSettings(xtream);
            Check(xtream.Result.Count == 3 && xtream.Result[0].AddedUtc == fixture.Clock.Now && xtream.Result[1].AddedUtc == fixture.Clock.Now
                && xtream.Result[0].IsRecentlyAdded && !xtream.Result[2].IsRecentlyAdded, "Global catalog accepts numeric/string provider dates and tolerates malformed dates");
            Check(System.IO.Directory.GetFiles(fixture.Directory).All(p => !File.ReadAllText(p).Contains("private")), "Playlist credentials and stream URLs never enter saved discovery history");
        }
        finally { Session.Mode = mode; Session.PlaylistChannels = playlist; Session.PlaylistSource = origin; Session.Host = host; Session.Username = user; }
    }

    private static void VerifyRecentChannelFiltering()
    {
        var clock = new DiscoveryClock(); var source = new CatalogLiveSource();
        using var live = new LiveTvPageViewModel(source, TimeSpan.Zero, clock);
        AwaitSettings(live.ActivateAsync()); live.SelectedCategory = live.Categories[0];
        var request = source.Channels.Last();
        live.ShowRecentlyAddedOnly = true;
        Check(!request.Token.IsCancellationRequested && source.Channels.Count == 1, "Toggling the recent filter shares a pending local download");
        request.Complete([new() { Id = 1, Name = "Old" }, new() { Id = 2, Name = "News", AddedUtc = clock.Now.AddDays(-1) },
            new() { Id = 3, Name = "Sports", DiscoveredUtc = clock.Now.AddDays(-2) }]); AwaitSettings(live.LoadTask);
        Check(live.Channels.Count == 3 && live.ChannelsView.Cast<Channel>().Select(c => c.Id).SequenceEqual([2, 3]), "Local recent filter preserves the full cached category");
        Check(live.Channels[1].IsRecentlyAdded && live.Channels[2].IsRecentlyAdded, "Detached channel models preserve both kinds of new metadata");
        live.SearchQuery = "News"; AwaitSettings(live.LoadTask);
        Check(live.ChannelsView.Cast<Channel>().Single().Id == 2 && live.ChannelsCountText == "1 channels", "Recent and text filters compose and update counts");
        live.SearchQuery = ""; AwaitSettings(live.LoadTask); live.ShowRecentlyAddedOnly = false; AwaitSettings(live.LoadTask);
        live.SelectedChannel = live.Channels[0]; live.ShowRecentlyAddedOnly = true; AwaitSettings(live.LoadTask);
        Check(live.SelectedChannel == null && source.Channels.Count == 1, "Filtering clears a hidden selection without refetching");

        var page = new LiveTvPageView { DataContext = new { LiveTv = live, TileWidth = 180d, TileHeight = 150d } };
        using var host = new HwndSource(new HwndSourceParameters("New channels fixture")
        { Width = 1200, Height = 700, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
        host.RootVisual = page; Layout(page, 1200, 700);
        var toggle = (CheckBox)page.FindName("RecentlyAddedOnlyCheckBox");
        Check(toggle.IsChecked == true && ((ItemsControl)page.FindName("ChannelsGridView")).Items.Count == 2, "Compiled recent filter binding updates the channel grid");
        var grid = (ItemsControl)page.FindName("ChannelsGridView");
        Check(Descendants(grid).OfType<TextBlock>().Any(t => t.Text == "NEW" && t.IsVisible), "Recent grid cards display visible NEW badges");
        grid.Visibility = Visibility.Collapsed;
        var list = (ItemsControl)page.FindName("ChannelsListView"); list.Visibility = Visibility.Visible; Layout(page, 1200, 700);
        Check(Descendants(list).OfType<TextBlock>().Any(t => t.Text == "NEW" && t.IsVisible), "Recent list rows display visible NEW badges");
        toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false); Layout(page, 1200, 700); AwaitSettings(live.LoadTask);
        Check(!live.ShowRecentlyAddedOnly && list.Items.Count == 3, "Turning off the compiled filter restores all local channels");
        live.ShowRecentlyAddedOnly = true; AwaitSettings(live.LoadTask); live.SelectedChannel = live.Channels[1];
        clock.Now = clock.Now.AddDays(8); live.RefreshRecentChannels();
        Check(live.Channels.All(c => !c.IsRecentlyAdded) && live.ChannelsView.IsEmpty && live.SelectedChannel == null && live.ChannelsCountText == "0 channels",
            "Time refresh expires badges, filtered rows, counts, and hidden selection together");

        live.SearchAllChannels = true;
        var globalTask = live.LoadTask;
        Check(source.Indexes.Count == 1 && live.IsGlobalSearchActive, "Recent-only global browsing loads the index without search text");
        var index = Enumerable.Range(1, 1100).Select(i => new Channel { Id = i, Number = i, Name = "Old" }).ToList();
        index.Add(new Channel { Id = 2000, Number = 1101, Name = "New global", AddedUtc = clock.Now });
        source.Indexes.Last().Complete(index); AwaitSettings(globalTask);
        Check(live.Channels.Single().Id == 2000 && live.Channels[0].Number == 1101, "Global recent filtering happens before the 1000-result cap and preserves numbering");
        live.SearchQuery = "New"; AwaitSettings(live.LoadTask);
        Check(source.Indexes.Count == 1 && live.Channels.Single().Id == 2000, "Recent global searches share the downloaded index");
        live.SearchQuery = ""; AwaitSettings(live.LoadTask); live.ShowRecentlyAddedOnly = false; AwaitSettings(live.LoadTask);
        Check(!live.IsGlobalSearchActive && live.Channels.Count == 3, "Clearing recent-only global mode restores the selected category");

        var delayed = new CatalogLiveSource(); using var guarded = new LiveTvPageViewModel(delayed, TimeSpan.Zero, clock);
        AwaitSettings(guarded.ActivateAsync()); guarded.SelectedCategory = guarded.Categories[0];
        delayed.Channels.Last().Complete([new() { Id = 1 }]); AwaitSettings(guarded.LoadTask);
        guarded.SearchAllChannels = true; guarded.ShowRecentlyAddedOnly = true;
        var obsolete = guarded.LoadTask;
        guarded.ShowRecentlyAddedOnly = false; AwaitSettings(guarded.LoadTask);
        delayed.Indexes.Last().Complete([new() { Id = 99, AddedUtc = clock.Now }]); AwaitSettings(obsolete);
        Check(guarded.Channels.Single().Id == 1, "A late recent-catalog response cannot overwrite the restored category");
    }

    private sealed class RecentChannelHttpHandler(DateTimeOffset now) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent($$"""[{"stream_id":1,"added":{{now.ToUnixTimeSeconds()}}},{"stream_id":2,"added":"{{now.ToUnixTimeSeconds()}}"},{"stream_id":3,"added":false}]""") });
    }

    private sealed class DiscoveryClock : TimeProvider
    {
        public DateTimeOffset Now = new(2030, 1, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ChannelHistoryFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "iptv-channel-history-" + Guid.NewGuid().ToString("N"));
        public DiscoveryClock Clock { get; } = new();
        public ChannelHistoryStore Store { get; }
        public ChannelHistoryFixture() => Store = new ChannelHistoryStore(Directory, Clock);
        public void Apply(string account, string? scope, List<Channel> channels) => Store.Apply(account, scope, channels, c => c.Id.ToString(), default);
        public void Dispose()
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var file in System.IO.Directory.GetFiles(Directory)) File.Delete(file);
            System.IO.Directory.Delete(Directory);
        }
    }
}
