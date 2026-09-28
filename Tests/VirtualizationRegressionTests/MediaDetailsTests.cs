using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

internal static partial class Program
{
    private static void VerifyMediaDetails()
    {
        var service = new DetailsVodService();
        var catalog = new MoviesSeriesPageViewModel(service);
        var model = catalog.Details;
        Check(model.State == MediaDetailsState.Empty && model.Rows.Count == 0 && !model.PlayMovieCommand.CanExecute(null),
            "Details starts empty with playback disabled and no dashboard dependency");
        var a = new VodContent { Id = 1, Name = "Movie A" };
        var b = new VodContent { Id = 2, Name = "Movie B" };
        var first = model.SelectAsync(a);
        var requestA = service.Requests.Last();
        Check(model.State == MediaDetailsState.Loading && a.DetailsLoading && a.IsSelected && catalog.SelectedMovie == a,
            "Selecting a movie immediately owns the selection and loading state");
        Check(!ReferenceEquals(a, requestA.Content), "Details service receives a detached model");
        Check(ReferenceEquals(first, model.SelectAsync(a)) && service.Requests.Count == 1,
            "Repeated selection shares the in-flight request");
        var second = model.SelectAsync(b);
        var requestB = service.Requests.Last();
        Check(requestA.Token.IsCancellationRequested && !a.DetailsLoading && !a.IsSelected && b.DetailsLoading,
            "Selecting another movie cancels the old request and clears its indicators");
        requestB.Complete(content => { content.Plot = "Current plot"; content.Duration = "3600"; });
        AwaitSettings(second);
        requestA.Complete(content => content.Plot = "Stale plot");
        AwaitSettings(first);
        Check(ReferenceEquals(model.Content, b) && model.State == MediaDetailsState.Ready && b.Plot == "Current plot" && a.Plot == null,
            "An older provider response cannot overwrite the current details or mutate its old bound model");
        Check(model.Rows[0] is MediaSummaryRow { Title: "Movie B", Duration: "1h 0m", Plot: "Current plot", IsMovie: true },
            "Movie metadata is prepared as one bound summary row");
        var requestsBefore = service.Requests.Count;
        model.Clear();
        AwaitSettings(model.SelectAsync(b));
        Check(service.Requests.Count == requestsBefore && model.HasDetails, "Already-loaded details render without another service request");
        VodContent? playedMovie = null;
        model.MoviePlaybackRequested += movie => playedMovie = movie;
        model.PlayMovieCommand.Execute(null);
        Check(ReferenceEquals(playedMovie, b), "Movie playback command requests the selected movie from its host");

        var returning = new VodContent { Id = 3, Name = "Returning" };
        var oldReturn = model.SelectAsync(returning);
        var oldRequest = service.Requests.Last();
        var other = model.SelectAsync(new VodContent { Id = 4 });
        var otherRequest = service.Requests.Last();
        var newReturn = model.SelectAsync(returning);
        var newRequest = service.Requests.Last();
        oldRequest.Complete(content => content.Plot = "Old same-object response");
        AwaitSettings(oldReturn);
        Check(returning.DetailsLoading && !returning.DetailsLoaded && model.State == MediaDetailsState.Loading,
            "A-B-A selection cannot let the first A completion clear the new A spinner");
        newRequest.Complete(content => content.Plot = "Fresh A response");
        AwaitSettings(newReturn);
        otherRequest.Fail();
        AwaitSettings(other);
        Check(returning.Plot == "Fresh A response" && model.HasDetails, "Stale failures cannot replace a newer success");

        var movie = model.SelectAsync(new VodContent { Id = 5 });
        var movieRequest = service.Requests.Last();
        catalog.ShowSeriesCommand.Execute(null);
        Check(movieRequest.Token.IsCancellationRequested && model.Content == null && catalog.SelectedMovie == null,
            "Changing content type clears details without depending on a window handler");
        var show = new SeriesContent { Id = 6, Name = "Show" };
        var series = model.SelectAsync(show);
        var seriesRequest = service.Requests.Last();
        seriesRequest.Complete(content => ((SeriesContent)content).Seasons =
            [new SeasonInfo { SeasonNumber = 1, Episodes = [new EpisodeContent { Id = 7, SeasonNumber = 1, EpisodeNumber = 1, Name = "Pilot" }] }]);
        AwaitSettings(series);
        movieRequest.Complete(content => content.Plot = "Wrong type");
        AwaitSettings(movie);
        Check(catalog.SelectedSeries == show && catalog.SelectedMovie == null && model.Rows.Count == 3 && !model.PlayMovieCommand.CanExecute(null),
            "Series owns the shared panel and exposes summary, season, and episode rows");
        catalog.ShowSeriesCommand.Execute(null);
        Check(ReferenceEquals(model.Content, show) && model.HasDetails, "Selecting the active content type leaves loaded details intact");
        var episode = show.Seasons[0].Episodes[0];
        EpisodeContent? playedEpisode = null;
        model.EpisodePlaybackRequested += value => playedEpisode = value;
        model.PlayEpisodeCommand.Execute(episode);
        Check(ReferenceEquals(episode, playedEpisode), "Episode command retains the correct episode model");
        var unrelated = new EpisodeContent { Id = episode.Id };
        model.PlayEpisodeCommand.Execute(unrelated);
        Check(!model.PlayEpisodeCommand.CanExecute(unrelated) && ReferenceEquals(playedEpisode, episode),
            "An episode with a matching ID outside the current series cannot be played");

        var errors = 0;
        model.LoadFailed += _ => errors++;
        var broken = model.SelectAsync(new SeriesContent { Id = 8, Name = "Retry show" });
        service.Requests.Last().Fail();
        AwaitSettings(broken);
        Check(model.State == MediaDetailsState.Failed && model.CanRetry && model.Rows.Count == 0 && errors == 1,
            "Current request errors leave loading state and expose retry");
        var retry = model.RetryCommand.ExecuteAsync(null);
        Check(model.State == MediaDetailsState.Loading && !model.RetryCommand.CanExecute(null), "Retry disables another request for the same selection");
        service.Requests.Last().Complete();
        AwaitSettings(retry);
        Check(model.HasDetails && !model.CanRetry && model.Content!.Name == "Retry show", "Retry recovers the selected details");

        using var lifetime = new CancellationTokenSource();
        var canceled = model.SelectAsync(new VodContent { Id = 9 }, lifetime.Token);
        var canceledRequest = service.Requests.Last();
        lifetime.Cancel();
        canceledRequest.Complete();
        AwaitSettings(canceled);
        Check(model.State == MediaDetailsState.Empty && model.Rows.Count == 0 && model.Content == null,
            "Lifetime cancellation prevents committing details even when the service returns success");
        var pending = model.SelectAsync(new VodContent { Id = 10 });
        var pendingRequest = service.Requests.Last();
        model.Clear();
        pendingRequest.Fail();
        AwaitSettings(pending);
        Check(pendingRequest.Token.IsCancellationRequested && model.State == MediaDetailsState.Empty && errors == 1,
            "Leaving the page clears selection and suppresses late errors");
        model.PlayMovieCommand.Execute(null);
        model.PlayEpisodeCommand.Execute(episode);
        Check(ReferenceEquals(playedMovie, b) && ReferenceEquals(playedEpisode, episode), "Cleared details cannot issue stale playback requests");

        VerifyMediaDetailsView();
    }

    private static void VerifyMediaDetailsView()
    {
        var service = new DetailsVodService();
        var model = new MediaDetailsViewModel(service);
        var view = new MediaDetailsView { DataContext = model };
        using var source = new HwndSource(new HwndSourceParameters("Media details fixture")
        { Width = 360, Height = 500, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
        source.RootVisual = view;
        Layout(view, 360, 500);
        var placeholder = (TextBlock)view.FindName("PlaceholderText");
        var list = (ItemsControl)view.FindName("DetailsList");
        Check(placeholder.Text.Contains("Select a movie") && list.Visibility == Visibility.Collapsed,
            "Standalone details view binds its initial placeholder");
        var load = model.SelectAsync(new VodContent { Id = 1, Name = "Bound movie" });
        Layout(view, 360, 500);
        Check(placeholder.Text == "Loading details...", "Loading state renders through binding");
        service.Requests.Last().Fail();
        AwaitSettings(load);
        Layout(view, 360, 500);
        var retryButton = (Button)view.FindName("RetryButton");
        Check(placeholder.Text.StartsWith("Failed") && retryButton.IsVisible && ReferenceEquals(retryButton.Command, model.RetryCommand),
            "Failure and retry render without any window control lookups");
        var retry = model.RetryCommand.ExecuteAsync(null);
        service.Requests.Last().Complete(content => content.Plot = "Bound plot");
        AwaitSettings(retry);
        Layout(view, 360, 500);
        Check(list.Visibility == Visibility.Visible && Descendants(list).OfType<TextBlock>().Any(text => text.Text == "Bound plot"),
            "Successful retry renders metadata through the summary template");
        var play = Descendants(list).OfType<Button>().Single(button => Equals(button.Content, "▶ Play"));
        Check(play.IsVisible && play.IsEnabled && ReferenceEquals(play.Command, model.PlayMovieCommand),
            "Movie template resolves its play command through the details view");

        var show = new SeriesContent { Id = 2, Name = "Long series", DetailsLoaded = true, Seasons =
            [new SeasonInfo { SeasonNumber = 1, Episodes = Enumerable.Range(0, 10000).Select(i => new EpisodeContent
                { Id = i, Name = $"Episode {i}", SeasonNumber = 1, EpisodeNumber = i + 1 }).ToList() }] };
        AwaitSettings(model.SelectAsync(show));
        Layout(view, 360, 500);
        var panel = Descendants(list).OfType<VirtualizingPanel>().Single();
        Check(list.Items.Count == 10002 && Children(panel).Count() is > 0 and < 100,
            "Ten thousand episodes render only viewport-sized containers, including summary and season rows");
        Check(!Descendants(list).OfType<Button>().Any(button => Equals(button.Content, "▶ Play") && button.IsVisible),
            "Series details do not expose the movie play action");
        var scroll = (IScrollInfo)panel;
        scroll.SetVerticalOffset(scroll.ExtentHeight);
        Layout(view, 360, 500);
        Check(Children(panel).Count() < 100 && list.ItemContainerGenerator.ContainerFromIndex(10001) != null,
            "Scrolling a large series virtualizes through its final episode");
        var last = Descendants(list).OfType<Button>().Single(button => ReferenceEquals(button.CommandParameter, show.Seasons[0].Episodes[^1]));
        Check(last.IsEnabled && ReferenceEquals(last.Command, model.PlayEpisodeCommand), "Recycled episode rows keep the correct command parameter");
        EpisodeContent? played = null;
        model.EpisodePlaybackRequested += episode => played = episode;
        last.Command.Execute(last.CommandParameter);
        Check(ReferenceEquals(played, show.Seasons[0].Episodes[^1]), "The final virtualized episode requests the correct playback");
        var staleCommand = last.Command;
        var staleParameter = last.CommandParameter;
        AwaitSettings(model.SelectAsync(new VodContent { Id = 3, Name = "Replacement movie", DetailsLoaded = true }));
        Layout(view, 360, 500);
        Check(list.Items.Count == 1 && Descendants(list).OfType<TextBlock>().Any(text => text.Text == "Replacement movie") &&
            !staleCommand.CanExecute(staleParameter), "Switching away from a scrolled series restores summary and invalidates old episode commands");
    }

    private sealed class DetailsRequest(IWatchableContent content, CancellationToken token)
    {
        public IWatchableContent Content { get; } = content;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Completion { get; } = new();
        public void Complete(Action<IWatchableContent>? fill = null)
        {
            fill?.Invoke(Content);
            Content.DetailsLoaded = true;
            Completion.SetResult();
        }
        public void Fail() => Completion.SetException(new IOException("Provider failure"));
    }

    // Deliberately ignores cancellation so tests exercise guards against stale providers/caches.
    private sealed class DetailsVodService : IVodService
    {
        public List<DetailsRequest> Requests { get; } = new();
        private Task Load(IWatchableContent content, CancellationToken token)
        {
            var request = new DetailsRequest(content, token);
            Requests.Add(request);
            return request.Completion.Task;
        }
        public Task LoadVodDetailsAsync(VodContent content, CancellationToken token = default) => Load(content, token);
        public Task LoadSeriesDetailsAsync(SeriesContent content, CancellationToken token = default) => Load(content, token);
        public Task<List<VodCategory>> LoadVodCategoriesAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<List<VodContent>> LoadVodContentAsync(string category, CancellationToken token = default) => throw new NotSupportedException();
        public Task<List<SeriesCategory>> LoadSeriesCategoriesAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<List<SeriesContent>> LoadSeriesContentAsync(string category, CancellationToken token = default) => throw new NotSupportedException();
        public Task<List<EpisodeContent>> LoadEpisodesAsync(string series, CancellationToken token = default) => throw new NotSupportedException();
        public Task LoadVodPosterAsync(VodContent content, CancellationToken token = default) => throw new NotSupportedException();
        public Task LoadSeriesPosterAsync(SeriesContent content, CancellationToken token = default) => throw new NotSupportedException();
    }
}
