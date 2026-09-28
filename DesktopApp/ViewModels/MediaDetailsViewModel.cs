using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public enum MediaDetailsState { Empty, Loading, Ready, Failed }
public sealed record MediaSummaryRow(string Title, string Year, string Duration, string Rating,
    string Genre, string Cast, string Director, string Plot, bool IsMovie);
public sealed record SeasonHeaderRow(string Title);

// One selection owns both movie and series details. Called on the UI thread;
// service results are applied by SelectedDetailsLoader only while that selection is current.
public partial class MediaDetailsViewModel(IVodService vodService) : ObservableObject
{
    private readonly SelectedDetailsLoader _requests = new();
    private readonly HashSet<EpisodeContent> _episodes = new();
    private IWatchableContent? _content;
    private MediaDetailsState _state;
    private long _version;
    private Task _pendingSelection = Task.CompletedTask;
    private CancellationToken _lifetimeToken;

    public IWatchableContent? Content => _content;
    public MediaDetailsState State => _state;
    public bool HasDetails => State == MediaDetailsState.Ready;
    public bool HasPlaceholder => !HasDetails;
    public bool CanRetry => State == MediaDetailsState.Failed && !_lifetimeToken.IsCancellationRequested;
    public string Placeholder => State switch
    {
        MediaDetailsState.Loading => "Loading details...",
        MediaDetailsState.Failed => "Failed to load details. Try again.",
        _ => "Select a movie or series to view details"
    };
    // A single flat list lets WPF virtualize season headers and episodes together.
    public BulkObservableCollection<object> Rows { get; } = new();

    public event Action<VodContent>? MoviePlaybackRequested;
    public event Action<EpisodeContent>? EpisodePlaybackRequested;
    public event Action<Exception>? LoadFailed;

    public Task SelectAsync(IWatchableContent content, CancellationToken lifetimeToken = default)
    {
        if (content is not VodContent and not SeriesContent)
            throw new ArgumentException("Select a movie or series.", nameof(content));
        if (lifetimeToken.IsCancellationRequested) return Task.CompletedTask;
        if (ReferenceEquals(Content, content) && State is MediaDetailsState.Loading or MediaDetailsState.Ready)
            return _pendingSelection;

        Clear();
        _content = content;
        _lifetimeToken = lifetimeToken;
        content.IsSelected = true;
        OnPropertyChanged(nameof(Content));
        SetState(MediaDetailsState.Loading);
        return _pendingSelection = LoadAsync(content, _version, lifetimeToken);
    }

    public void Clear()
    {
        _version++;
        _requests.Cancel();
        if (_content != null) _content.IsSelected = false;
        _content = null;
        _episodes.Clear();
        Rows.ReplaceAll([]);
        OnPropertyChanged(nameof(Content));
        SetState(MediaDetailsState.Empty);
    }

    private async Task LoadAsync(IWatchableContent content, long version, CancellationToken token)
    {
        bool IsCurrent() => _version == version && ReferenceEquals(Content, content);
        void Failed(Exception error)
        {
            SetState(MediaDetailsState.Failed);
            LoadFailed?.Invoke(error);
        }

        if (content is VodContent movie)
            await _requests.LoadAsync(movie, vodService.LoadVodDetailsAsync,
                (target, details) => target.ApplyDetails(details), Failed, IsCurrent, token);
        else if (content is SeriesContent series)
            await _requests.LoadAsync(series, vodService.LoadSeriesDetailsAsync,
                (target, details) => target.ApplyDetails(details), Failed, IsCurrent, token);

        if (!IsCurrent()) return;
        if (token.IsCancellationRequested) { Clear(); return; }
        if (State == MediaDetailsState.Failed) return;
        ShowDetails(content);
    }

    private void ShowDetails(IWatchableContent content)
    {
        var rows = new List<object>
        {
            new MediaSummaryRow(content.Name,
                string.IsNullOrWhiteSpace(content.ReleaseDate) ? "" : content.DisplayYear,
                content is SeriesContent series ? (series.SeasonCount > 0 ? series.DisplayDuration : "") :
                    string.IsNullOrWhiteSpace(content.Duration) ? "" : content.DisplayDuration,
                content.Rating ?? "", content.Genre ?? "", content.Cast ?? "", content.Director ?? "",
                string.IsNullOrWhiteSpace(content.Plot) ? "No plot available" : content.Plot, content is VodContent)
        };
        if (content is SeriesContent show)
        {
            foreach (var season in show.Seasons)
            {
                rows.Add(new SeasonHeaderRow($"{season.DisplayName} ({season.Episodes.Count} episodes)"));
                rows.AddRange(season.Episodes);
                _episodes.UnionWith(season.Episodes);
            }
        }
        Rows.ReplaceAll(rows);
        SetState(MediaDetailsState.Ready);
    }

    private void SetState(MediaDetailsState state)
    {
        SetProperty(ref _state, state, nameof(State));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(HasPlaceholder));
        OnPropertyChanged(nameof(Placeholder));
        OnPropertyChanged(nameof(CanRetry));
        RetryCommand.NotifyCanExecuteChanged();
        PlayMovieCommand.NotifyCanExecuteChanged();
        PlayEpisodeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRetry), AllowConcurrentExecutions = true)]
    private Task RetryAsync() => Content is { } content ? SelectAsync(content, _lifetimeToken) : Task.CompletedTask;

    private bool CanPlayMovie() => HasDetails && Content is VodContent;
    [RelayCommand(CanExecute = nameof(CanPlayMovie))]
    private void PlayMovie()
    {
        if (CanPlayMovie() && Content is VodContent movie) MoviePlaybackRequested?.Invoke(movie);
    }

    private bool CanPlayEpisode(EpisodeContent? episode) => HasDetails && episode != null && _episodes.Contains(episode);
    [RelayCommand(CanExecute = nameof(CanPlayEpisode))]
    private void PlayEpisode(EpisodeContent? episode)
    {
        if (CanPlayEpisode(episode)) EpisodePlaybackRequested?.Invoke(episode!);
    }
}
