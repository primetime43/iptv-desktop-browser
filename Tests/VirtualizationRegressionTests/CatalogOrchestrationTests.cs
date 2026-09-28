using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

internal static partial class Program
{
    private static void VerifyCatalogOrchestration()
    {
        VerifyLiveCatalogOrchestration();
        VerifyFavoriteCatalogUpdates();
        VerifyVodCatalogOrchestration();
        VerifyCatalogPickerBindings();
        VerifyCatalogSource();
    }

    private static void VerifyLiveCatalogOrchestration()
    {
        var source = new CatalogLiveSource();
        using var live = new LiveTvPageViewModel(source, TimeSpan.Zero);
        var a = new Category { Id = "a", Name = "Alpha" };
        var b = new Category { Id = "b", Name = "Beta" };
        AwaitSettings(live.ActivateAsync());
        Check(source.CategoryCalls == 1 && live.Categories.Count == 2, "Live activation loads categories without a window");
        live.SelectedCategory = a;
        var old = source.Channels.Last(); var oldTask = live.LoadTask;
        live.SelectedCategory = b;
        var current = source.Channels.Last();
        Check(old.Token.IsCancellationRequested && live.IsLoadingChannels && live.Channels.Count == 0, "Live category changes cancel and clear obsolete catalogs");
        live.SearchQuery = "news";
        Check(source.Channels.Count == 2 && !current.Token.IsCancellationRequested, "Local typing shares the pending category download");
        live.SearchQuery = "";
        var raw = new Channel { Id = 2, Name = "Beta news", Number = 95, NowTitle = "Headlines" };
        source.Favorites.Add(2);
        current.Complete([raw, new Channel { Id = 3, Name = "Sports" }]); AwaitSettings(live.LoadTask);
        old.Complete([new Channel { Name = "Wrong" }]); AwaitSettings(oldTask);
        Check(live.Channels.Count == 2 && live.Channels[0].Name == "Beta news" && !live.IsLoadingChannels, "Late live response cannot replace the selected category");
        Check(raw.Number == 95 && live.Channels[0].Number == 1 && live.Channels[0].IsFavorite && !ReferenceEquals(raw, live.Channels[0]), "Live preparation detaches source items and applies favorites and numbering");
        live.SelectedChannel = live.Channels[0];
        live.SearchQuery = "sports"; AwaitSettings(live.LoadTask);
        Check(live.ChannelsCountText == "1 channels" && live.SelectedChannel == null && source.Channels.Count == 2, "Local filtering updates counts and clears hidden selection without refetching");
        live.SearchQuery = "headlines"; AwaitSettings(live.LoadTask);
        Check(live.ChannelsView.Cast<Channel>().Single().Id == 2, "Local search includes the current program title");
        live.SearchQuery = ""; AwaitSettings(live.LoadTask);
        live.SearchAllChannels = true;
        live.SearchQuery = "7";
        var firstSearch = live.LoadTask;
        var index = source.Indexes.Last();
        live.SearchQuery = "9";
        var latestSearch = live.LoadTask;
        Check(source.Indexes.Count == 1 && !index.Token.IsCancellationRequested, "Superseded global searches share the active account's index request");
        index.Complete([new Channel { Id = 7, Number = 7, Name = "Seven" }, new Channel { Id = 9, Number = 9, Name = "Nine" }]);
        AwaitSettings(firstSearch); AwaitSettings(latestSearch);
        Check(live.Channels.Single().Id == 9 && live.Channels[0].Number == 9, "Global numeric search retains stable channel numbers");
        live.SearchQuery = "7"; AwaitSettings(live.LoadTask);
        Check(source.Indexes.Count == 1 && live.Channels.Single().Id == 7 && live.Channels[0].Number == 7, "Repeated numeric search reuses the unmodified index");
        live.SearchQuery = ""; AwaitSettings(live.LoadTask);
        Check(live.Channels.Count == 2 && live.Channels[0].Name == "Beta news", "Clearing global search restores the selected category");
        live.SearchAllChannels = false;
        live.SelectedCategory = a;
        var leaving = source.Channels.Last(); var leavingTask = live.LoadTask;
        live.Deactivate();
        leaving.Complete([new Channel { Name = "After leaving" }]); AwaitSettings(leavingTask);
        Check(leaving.Token.IsCancellationRequested && live.Channels.Count == 0 && !live.IsLoadingChannels, "Page departure rejects live results and clears loading state");
        var returning = live.ActivateAsync();
        Check(source.Channels.Count == 4, "Returning resumes an interrupted selected category");
        source.Channels.Last().Fail(); AwaitSettings(returning);
        Check(live.ErrorMessage.Contains("could not") && live.Channels.Count == 0, "Live failures expose a safe error and no stale rows");
        var retry = live.RetryCommand.ExecuteAsync(null);
        source.Channels.Last().Complete([]); AwaitSettings(retry);
        var calls = source.Channels.Count;
        live.Deactivate(); AwaitSettings(live.ActivateAsync());
        Check(live.ErrorMessage.Length == 0 && source.Channels.Count == calls, "Successful empty categories are retained when returning");
        live.SelectedCategory = null;
        Check(live.Channels.Count == 0 && !live.IsLoadingChannels, "Clearing the live category cancels work");

        var delayedSource = new CatalogLiveSource { DelayCategories = true };
        using var delayed = new LiveTvPageViewModel(delayedSource, TimeSpan.FromMilliseconds(20));
        var activation = delayed.ActivateAsync();
        var categories = delayedSource.CategoryRequests.Last();
        delayed.Deactivate(); categories.Complete([a]); AwaitSettings(activation);
        Check(delayed.Categories.Count == 0 && categories.Token.IsCancellationRequested, "Late category lists cannot apply after page departure");
        var reactivation = delayed.ActivateAsync(); delayedSource.CategoryRequests.Last().Complete([a]); AwaitSettings(reactivation);
        delayed.SearchAllChannels = true; delayed.SearchQuery = "first";
        var debounce = delayed.LoadTask; delayed.SearchQuery = ""; AwaitSettings(debounce); AwaitSettings(delayed.LoadTask);
        Check(delayedSource.Indexes.Count == 0, "Clearing a query cancels pending debounce before network work");
        delayed.SearchQuery = "close";
        var closing = delayed.LoadTask; delayed.Dispose(); AwaitSettings(closing);
        Check(!delayed.IsSearchLoading && delayedSource.Indexes.Count == 0, "Disposal cancels pending search and blocks background index creation");
    }

    private static void VerifyFavoriteCatalogUpdates()
    {
        var source = new CatalogLiveSource();
        using var live = new LiveTvPageViewModel(source, TimeSpan.Zero);
        var favorites = new Category { Id = LiveCatalogSource.FavoritesId, Name = "Favorites" };
        var one = new Channel { Id = 1, Number = 1, Name = "One" };
        var two = new Channel { Id = 2, Number = 2, Name = "Two" };
        source.Favorites.UnionWith([1, 2]);
        AwaitSettings(live.ActivateAsync());
        live.SelectedCategory = favorites;
        source.Channels.Last().Complete([one, two]); AwaitSettings(live.LoadTask);
        live.SelectedChannel = live.Channels[0];
        live.UpcomingPrograms.Add(new EpgEntry { Title = "Selected guide" });

        source.Favorites.Remove(1);
        live.RefreshFavorites();
        Check(source.Channels.Count == 2, "Removing a favorite reloads Favorites membership, not just its star");
        Check(live.SelectedChannel == null && live.UpcomingPrograms.Count == 0,
            "Removing the selected favorite clears its selection and guide");
        source.Channels.Last().Complete([two]); AwaitSettings(live.LoadTask);
        Check(live.Channels.Single().Id == 2 && live.ChannelsCountText == "1 favorite channels",
            "Favorites rows and count reflect removals");

        source.Favorites.Add(1);
        live.RefreshFavorites();
        var stale = source.Channels.Last(); var staleTask = live.LoadTask;
        source.Favorites.Clear();
        live.RefreshFavorites();
        source.Channels.Last().Complete([]); AwaitSettings(live.LoadTask);
        stale.Complete([one, two]); AwaitSettings(staleTask);
        Check(stale.Token.IsCancellationRequested && live.Channels.Count == 0 && !live.IsLoadingChannels,
            "Rapid favorite edits reject an older snapshot even when cancellation is ignored");

        live.Deactivate();
        var calls = source.Channels.Count;
        source.Favorites.Add(1);
        live.RefreshFavorites();
        Check(source.Channels.Count == calls, "Favorites changes on another page do not start hidden catalog loads");
        var returning = live.ActivateAsync();
        Check(source.Channels.Count == calls + 1, "Returning reloads the invalidated Favorites category, including cached empty lists");
        source.Channels.Last().Complete([one]); AwaitSettings(returning);
        Check(live.Channels.Single().Id == 1, "Favorites added while away appear on return");

        live.SearchAllChannels = true;
        live.SearchQuery = "Two";
        source.Indexes.Last().Complete([one, two]); AwaitSettings(live.LoadTask);
        var searchRow = live.Channels.Single();
        live.SelectedChannel = searchRow;
        source.Favorites.Add(2);
        calls = source.Channels.Count;
        live.RefreshFavorites();
        Check(source.Channels.Count == calls && source.Indexes.Count == 1 && searchRow.IsFavorite && live.SelectedChannel == searchRow,
            "Editing favorites during global search updates stars without restarting search or replacing selection");
        live.SearchQuery = "";
        Check(source.Channels.Count == calls + 1, "Clearing global search reloads changed Favorites membership");
        source.Channels.Last().Complete([one, two]); AwaitSettings(live.LoadTask);
        Check(live.Channels.Select(c => c.Id).SequenceEqual([1, 2]), "Restored Favorites includes items added during global search");

        live.SearchAllChannels = false;
        live.SelectedCategory = new Category { Id = "ordinary" };
        source.Channels.Last().Complete([one, two]); AwaitSettings(live.LoadTask);
        calls = source.Channels.Count;
        source.Favorites.Remove(2);
        live.RefreshFavorites();
        Check(source.Channels.Count == calls && live.Channels.Count == 2 && !live.Channels.Single(c => c.Id == 2).IsFavorite,
            "Ordinary categories update stars without downloading or removing channels");
        live.Dispose();
        live.RefreshFavorites();
        Check(source.Channels.Count == calls, "Late favorites notifications cannot restart a disposed page");
    }

    private static void VerifyVodCatalogOrchestration()
    {
        var source = new CatalogVodSource();
        using var catalog = new MoviesSeriesPageViewModel(source);
        AwaitSettings(catalog.ActivateAsync());
        Check(source.MovieCategoryCalls == 1 && source.SeriesCategoryCalls == 1, "Catalog activation independently loads both category lists");
        catalog.SelectedMovieCategoryId = "a";
        var old = source.Movies.Last(); var oldTask = catalog.LoadTask;
        catalog.SelectedMovieCategoryId = "b";
        var newer = source.Movies.Last();
        Check(old.Token.IsCancellationRequested && catalog.IsLoadingMovies, "Movie category selection cancels the previous request");
        var movie = new VodContent { Id = 2, CategoryId = "b", Name = "Movie", Genre = "Comedy", Plot = "A space voyage", DetailsLoaded = true };
        newer.Complete([movie, new VodContent { Id = 3, CategoryId = "b", Name = "Other", Genre = "Drama" }]); AwaitSettings(catalog.LoadTask);
        old.Complete([new VodContent { Name = "Wrong", CategoryId = "a" }]); AwaitSettings(oldTask);
        Check(catalog.Movies.Count == 2 && catalog.Movies[0] == movie && !catalog.IsLoadingMovies, "Late movie responses cannot overwrite a newer category");
        AwaitSettings(catalog.SelectMovieAsync(movie));
        catalog.SearchQuery = "drama";
        Check(catalog.MovieCountText == "1 movies" && catalog.SelectedMovie == null, "VOD search uses its own query and clears filtered-out details");
        catalog.SearchQuery = "space";
        Check(catalog.MoviesView.Cast<VodContent>().Single() == movie, "Movie search includes plot and genre");
        catalog.SearchQuery = "";
        catalog.SelectedMovieCategoryId = "c";
        var interrupted = source.Movies.Last(); var interruptedTask = catalog.LoadTask;
        catalog.ContentType = CatalogContentType.Series;
        interrupted.Complete([new VodContent { CategoryId = "c", Name = "Late movie" }]); AwaitSettings(interruptedTask);
        Check(interrupted.Token.IsCancellationRequested && catalog.Movies.Count == 0 && catalog.Details.Content == null, "Content-type switching rejects old catalogs and clears details");
        catalog.SelectedSeriesCategoryId = "s1";
        var oldSeries = source.Series.Last(); var oldSeriesTask = catalog.LoadTask;
        catalog.SelectedSeriesCategoryId = "s2";
        source.Series.Last().Complete([new SeriesContent { Id = 4, CategoryId = "s2", Name = "Series", Genre = "Comedy" }]); AwaitSettings(catalog.LoadTask);
        oldSeries.Fail(); AwaitSettings(oldSeriesTask);
        Check(catalog.Series.Single().Id == 4 && catalog.ErrorMessage.Length == 0, "Superseded series failures cannot replace current content or error state");
        catalog.SearchQuery = "comedy";
        Check(catalog.SeriesCountText == "1 series", "Series filtering uses the VOD search field");
        catalog.ContentType = CatalogContentType.Movies;
        Check(source.Movies.Last().Key == "c", "Returning to movies resumes its interrupted category");
        source.Movies.Last().Complete([]); AwaitSettings(catalog.LoadTask);
        var movieCalls = source.Movies.Count;
        catalog.ContentType = CatalogContentType.Series;
        AwaitSettings(catalog.LoadTask);
        Check(source.Series.Count == 2 && catalog.SelectedSeriesCategoryId == "s2", "Switching back retains the series category and successful results");
        catalog.ContentType = CatalogContentType.Movies; AwaitSettings(catalog.LoadTask);
        Check(source.Movies.Count == movieCalls, "Empty successful movie catalogs do not cause repeated downloads");
        catalog.SelectedMovieCategoryId = "d";
        var departing = source.Movies.Last(); var departingTask = catalog.LoadTask;
        catalog.Deactivate(); departing.Complete([new VodContent { CategoryId = "d" }]); AwaitSettings(departingTask);
        Check(departing.Token.IsCancellationRequested && catalog.Movies.Count == 0 && !catalog.IsLoadingMovies, "Leaving VOD rejects late responses immediately");
        var resume = catalog.ActivateAsync(); source.Movies.Last().Fail(); AwaitSettings(resume);
        Check(catalog.ErrorMessage.Contains("could not"), "Movie load failures expose a retryable error");
        var retry = catalog.RetryCommand.ExecuteAsync(null); source.Movies.Last().Complete([]); AwaitSettings(retry);
        Check(catalog.ErrorMessage.Length == 0, "Catalog retry recovers from a failed request");
        AwaitSettings(catalog.SelectMovieAsync(movie));
        Check(catalog.Details.Content == null, "Old catalog rows cannot reselect details after a category change");
        catalog.SelectedMovieCategoryId = "e";
        var clearing = source.Movies.Last(); var clearingTask = catalog.LoadTask;
        catalog.SelectedMovieCategoryId = ""; clearing.Complete([movie]); AwaitSettings(clearingTask);
        Check(clearing.Token.IsCancellationRequested && !catalog.IsLoadingMovies && catalog.Movies.Count == 0, "Clearing a category cancels and removes its loading state");

        var failingCategories = new CatalogVodSource { FailSeriesCategories = true };
        using var partial = new MoviesSeriesPageViewModel(failingCategories);
        AwaitSettings(partial.ActivateAsync());
        Check(partial.MovieCategories.Count == 2 && partial.SeriesCategoriesError.Length > 0, "One category endpoint can fail without blocking the other");
        failingCategories.FailSeriesCategories = false;
        AwaitSettings(partial.RetryCommand.ExecuteAsync(null));
        Check(failingCategories.MovieCategoryCalls == 1 && failingCategories.SeriesCategoryCalls == 2 && partial.SeriesCategories.Count == 2,
            "Retry requests only the failed category endpoint");
    }

    private static void VerifyCatalogPickerBindings()
    {
        var source = new CatalogVodSource();
        using var catalog = new MoviesSeriesPageViewModel(source);
        AwaitSettings(catalog.ActivateAsync());
        var page = new MoviesSeriesPageView { DataContext = new { Catalog = catalog, TileWidth = 180d, VodTileHeight = 240d } };
        using var host = new HwndSource(new HwndSourceParameters("Catalog picker fixture")
        { Width = 1200, Height = 700, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
        host.RootVisual = page; Layout(page, 1200, 700);
        var movies = (ComboBox)page.FindName("VodCategoryCombo");
        var series = (ComboBox)page.FindName("SeriesCategoryCombo");
        movies.SelectedIndex = 0;
        Layout(page, 1200, 700);
        Check(catalog.SelectedMovieCategoryId == "a" && source.Movies.Count == 1, "Compiled movie picker loads through direct view-model binding");
        Check(((Grid)page.FindName("MoviesLoadingOverlay")).Visibility == Visibility.Visible, "Movie loading overlay binds to model state");
        source.Movies.Last().Complete([new VodContent { CategoryId = "a", Name = "Match", Genre = "Comedy" }]); AwaitSettings(catalog.LoadTask);
        ((TextBox)page.FindName("VodSearchBox")).SetCurrentValue(TextBox.TextProperty, "missing");
        Layout(page, 1200, 700);
        Check(catalog.SearchQuery == "missing" && ((ItemsControl)page.FindName("MoviesGridView")).Items.Count == 0, "Compiled VOD search filters actual catalog items");
        catalog.ContentType = CatalogContentType.Series; Layout(page, 1200, 700);
        series.SelectedIndex = 1; Layout(page, 1200, 700);
        Check(catalog.SelectedSeriesCategoryId == "s2" && movies.Visibility == Visibility.Collapsed && series.Visibility == Visibility.Visible,
            "Series picker has independent selection and visibility");
        source.Series.Last().Complete([]); AwaitSettings(catalog.LoadTask);
        catalog.ContentType = CatalogContentType.Movies; Layout(page, 1200, 700);
        Check(movies.SelectedValue?.ToString() == "a" && catalog.SelectedSeriesCategoryId == "s2", "Changing type preserves both actual picker selections");

        var liveSource = new CatalogLiveSource();
        using var live = new LiveTvPageViewModel(liveSource, TimeSpan.Zero);
        AwaitSettings(live.ActivateAsync());
        var livePage = new LiveTvPageView { DataContext = new { LiveTv = live, TileWidth = 180d, TileHeight = 150d } };
        host.RootVisual = livePage; Layout(livePage, 1200, 700);
        ((ComboBox)livePage.FindName("CategoryCombo")).SelectedIndex = 0; Layout(livePage, 1200, 700);
        Check(live.SelectedCategory?.Id == "a" && liveSource.Channels.Count == 1, "Compiled live picker loads without a DashboardWindow handler");
        liveSource.Channels.Last().Complete([new Channel { Name = "Channel" }]); AwaitSettings(live.LoadTask);
        Layout(livePage, 1200, 700);
        Check(((Grid)livePage.FindName("ChannelsLoadingOverlay")).Visibility == Visibility.Collapsed, "Live loading overlay resets after completion");
    }

    private static void VerifyCatalogSource()
    {
        var savedMode = Session.Mode;
        var savedPlaylist = Session.PlaylistChannels;
        var savedHost = Session.Host;
        try
        {
            using var http = new System.Net.Http.HttpClient(new CatalogHttpHandler());
            var channelService = new CatalogChannelService();
            var source = new LiveCatalogSource(channelService, http);
            Session.Mode = SessionMode.M3u;
            Session.PlaylistChannels = [new PlaylistEntry { Id = 91, Name = "Playlist one", Category = " ", TvgId = "epg-1" },
                new PlaylistEntry { Id = 27, Name = "Playlist two", Category = "Sports", TvgId = "epg-2" }];
            var categories = source.LoadCategoriesAsync(default); AwaitSettings(categories);
            Check(categories.Result.Select(c => c.Id).SequenceEqual([LiveCatalogSource.FavoritesId, "Other", "Sports"]), "Playlist catalog preserves Other grouping and favorites placement");
            var group = source.LoadChannelsAsync(new Category { Id = "Sports" }, default); AwaitSettings(group);
            Check(group.Result.Single() is { Id: 27, EpgChannelId: "epg-2" }, "Playlist category loading preserves stream IDs and guide mappings");
            var index = source.LoadAllChannelsAsync(default); AwaitSettings(index);
            Check(index.Result.Select(c => c.Id).SequenceEqual([91, 27]) && index.Result.Select(c => c.Number).SequenceEqual([1, 2]), "Playlist global index numbers remain independent of stream IDs");
            Session.Mode = SessionMode.Xtream;
            Session.Host = "https://example.invalid";
            categories = source.LoadCategoriesAsync(default); AwaitSettings(categories);
            Check(channelService.Categories.Count == 1 && categories.Result.Count == 2, "Favorites insertion does not mutate cached service categories");
            index = source.LoadAllChannelsAsync(default); AwaitSettings(index);
            Check(index.Result.Count == 2 && index.Result[0].Id == 31 && index.Result[1].Id == 32 && index.Result[1].Number == 2,
                "Shared index parser accepts string IDs and skips malformed rows");
        }
        finally { Session.Mode = savedMode; Session.PlaylistChannels = savedPlaylist; Session.Host = savedHost; }
    }
    private sealed class CatalogHttpHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("""[{"stream_id":"31","name":"One"},false,{"stream_id":32,"name":"Two","epg_channel_id":null},{"name":"Invalid"}]""") });
    }
    private sealed class CatalogChannelService : IChannelService
    {
        public List<Category> Categories = [new() { Id = "a", Name = "Alpha" }];
        public Task<List<Category>> LoadCategoriesAsync(CancellationToken token = default) => Task.FromResult(Categories);
        public Task<List<Channel>> LoadChannelsForCategoryAsync(Category category, CancellationToken token = default) => throw new NotSupportedException();
        public Task LoadEpgForChannelAsync(Channel channel, CancellationToken token = default) => throw new NotSupportedException();
        public Task LoadEpgForChannelsAsync(IEnumerable<Channel> channels, CancellationToken token = default) => throw new NotSupportedException();
        public void SetRawOutputLogger(Action<string>? logger) { }
    }
    private sealed class CatalogRequest<T>(string key, CancellationToken token)
    {
        public string Key { get; } = key;
        public CancellationToken Token { get; } = token;
        private readonly TaskCompletionSource<List<T>> _completion = new();
        public Task<List<T>> Task => _completion.Task;
        public void Complete(List<T> values) => _completion.SetResult(values);
        public void Fail() => _completion.SetException(new InvalidOperationException("Provider failure"));
    }
    private sealed class CatalogLiveSource : ILiveCatalogSource
    {
        public int CategoryCalls;
        public bool DelayCategories;
        public List<CatalogRequest<Category>> CategoryRequests = new();
        public List<CatalogRequest<Channel>> Channels = new(), Indexes = new();
        public HashSet<int> Favorites = new();
        public HashSet<int> GetFavoriteIds() => Favorites;
        public Task<List<Category>> LoadCategoriesAsync(CancellationToken token)
        {
            CategoryCalls++;
            if (!DelayCategories) return Task.FromResult(new List<Category> { new() { Id = "a", Name = "Alpha" }, new() { Id = "b", Name = "Beta" } });
            var request = new CatalogRequest<Category>("categories", token); CategoryRequests.Add(request); return request.Task;
        }
        public Task<List<Channel>> LoadChannelsAsync(Category category, CancellationToken token)
        {
            var request = new CatalogRequest<Channel>(category.Id, token); Channels.Add(request); return request.Task;
        }
        public Task<List<Channel>> LoadAllChannelsAsync(CancellationToken token)
        {
            var request = new CatalogRequest<Channel>("index", token); Indexes.Add(request); return request.Task;
        }
    }
    private sealed class CatalogVodSource : IVodService
    {
        public int MovieCategoryCalls, SeriesCategoryCalls;
        public bool FailSeriesCategories;
        public List<CatalogRequest<VodContent>> Movies = new();
        public List<CatalogRequest<SeriesContent>> Series = new();
        public Task<List<VodCategory>> LoadVodCategoriesAsync(CancellationToken token = default)
        {
            MovieCategoryCalls++;
            return Task.FromResult(new List<VodCategory> { new() { CategoryId = "a", CategoryName = "Alpha" }, new() { CategoryId = "b", CategoryName = "Beta" } });
        }
        public Task<List<SeriesCategory>> LoadSeriesCategoriesAsync(CancellationToken token = default)
        {
            SeriesCategoryCalls++;
            return FailSeriesCategories ? Task.FromException<List<SeriesCategory>>(new Exception("Failed")) :
                Task.FromResult(new List<SeriesCategory> { new() { CategoryId = "s1", CategoryName = "One" }, new() { CategoryId = "s2", CategoryName = "Two" } });
        }
        public Task<List<VodContent>> LoadVodContentAsync(string category, CancellationToken token = default)
        {
            var request = new CatalogRequest<VodContent>(category, token); Movies.Add(request); return request.Task;
        }
        public Task<List<SeriesContent>> LoadSeriesContentAsync(string category, CancellationToken token = default)
        {
            var request = new CatalogRequest<SeriesContent>(category, token); Series.Add(request); return request.Task;
        }
        public Task LoadVodDetailsAsync(VodContent content, CancellationToken token = default) { content.DetailsLoaded = true; return Task.CompletedTask; }
        public Task LoadSeriesDetailsAsync(SeriesContent content, CancellationToken token = default) { content.DetailsLoaded = true; return Task.CompletedTask; }
        public Task<List<EpisodeContent>> LoadEpisodesAsync(string seriesId, CancellationToken token = default) => throw new NotSupportedException();
        public Task LoadVodPosterAsync(VodContent content, CancellationToken token = default) => throw new NotSupportedException();
        public Task LoadSeriesPosterAsync(SeriesContent content, CancellationToken token = default) => throw new NotSupportedException();
    }
}
