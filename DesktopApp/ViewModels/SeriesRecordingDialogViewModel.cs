using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public sealed record SeriesRecordingDraft(Channel Channel, string Name, SeriesMatchMode MatchMode,
    bool OnlyNewEpisodes, int PreBuffer, int PostBuffer);

public partial class SeriesRecordingDialogViewModel : ObservableObject, IDisposable
{
    private readonly IRecordingScheduleService _service;
    private readonly TimeProvider _clock;
    private readonly LatestRequestLoader _requests = new();
    private List<EpgEntry> _programs = new();
    private bool _closed;

    public SeriesRecordingDialogViewModel(IRecordingScheduleService service, IEnumerable<Channel> channels,
        SeriesRecording? existing = null, TimeProvider? clock = null)
    {
        _service = service;
        _clock = clock ?? TimeProvider.System;
        Channels = channels.OrderBy(c => c.Name).ToArray();
        IsEditing = existing != null;
        if (existing != null)
        {
            Name = existing.SeriesName;
            MatchMode = existing.MatchMode;
            OnlyNewEpisodes = existing.OnlyNewEpisodes;
            PreBuffer = existing.PreBufferMinutes.ToString();
            PostBuffer = existing.PostBufferMinutes.ToString();
            SelectedChannel = Channels.FirstOrDefault(c => c.Id == existing.ChannelId);
        }
    }

    public IReadOnlyList<Channel> Channels { get; }
    public bool IsEditing { get; }
    public bool CanChangeChannel => !IsEditing;
    public string WindowTitle => IsEditing ? "Edit Series Recording" : "Add Series Recording";
    public Array MatchModes => Enum.GetValues<SeriesMatchMode>();
    public BulkObservableCollection<string> Titles { get; } = new();
    public Task GuideLoadTask { get; private set; } = Task.CompletedTask;
    public SeriesRecordingDraft? Result { get; private set; }
    public event Action<bool>? CloseRequested;
    public event Action<SeriesScheduleViewModel>? PreviewRequested;

    [ObservableProperty] private Channel? _selectedChannel;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SeriesMatchMode _matchMode;
    [ObservableProperty] private bool _onlyNewEpisodes = true;
    [ObservableProperty] private string _preBuffer = "2";
    [ObservableProperty] private string _postBuffer = "5";
    [ObservableProperty] private string _guideStatus = "Select a channel to load its guide. You can also enter a show name manually.";
    [ObservableProperty] private bool _isLoading;

    public string ValidationMessage => SelectedChannel == null ? "Select a channel." :
        string.IsNullOrWhiteSpace(Name) ? "Enter a show name." :
        !Enum.IsDefined(MatchMode) ? "Select a match mode." :
        !int.TryParse(PreBuffer, out var pre) || pre < 0 || pre > 1440 ||
        !int.TryParse(PostBuffer, out var post) || post < 0 || post > 1440
            ? "Buffer times must be whole minutes between 0 and 1440." : string.Empty;

    public string NextAiring => SeriesGuide.FindEpisodes(_programs, Name, _clock.GetUtcNow().UtcDateTime).FirstOrDefault() is { } next
        ? $"Next airing: {next.StartTimeLocal:ddd, MMM d, h:mm tt}" : "No upcoming airing found for this show.";

    partial void OnSelectedChannelChanged(Channel? value)
    {
        if (!IsEditing) Name = string.Empty;
        GuideLoadTask = LoadGuideAsync();
        UpdateCommands();
    }
    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(NextAiring)); UpdateCommands(); }
    partial void OnPreBufferChanged(string value) => UpdateCommands();
    partial void OnPostBufferChanged(string value) => UpdateCommands();
    partial void OnMatchModeChanged(SeriesMatchMode value) => UpdateCommands();

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(ValidationMessage));
        SaveCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadGuideAsync()
    {
        _requests.Cancel();
        _programs.Clear();
        Titles.ReplaceAll(Array.Empty<string>());
        OnPropertyChanged(nameof(NextAiring));
        IsLoading = false;
        if (_closed || SelectedChannel is not { } channel) return;
        IsLoading = true;
        GuideStatus = "Loading program guide…";
        UpdateCommands();
        await _requests.LoadAsync(async token =>
        {
            var entries = await _service.LoadProgramsAsync(channel, token).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return (Entries: entries, Titles: entries.Select(e => SeriesGuide.StripEpisodeMetadata(e.Title))
                    .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToArray());
            }, token).ConfigureAwait(false);
        }, data =>
        {
            _programs = data.Entries;
            Titles.ReplaceAll(data.Titles);
            GuideStatus = Titles.Count > 0 ? $"{Titles.Count} shows found. Choose one or enter a name." : "No guide available. Enter a show name manually.";
            OnPropertyChanged(nameof(NextAiring));
        }, _ => GuideStatus = "The guide could not be loaded. Retry or enter a show name manually.",
        () => { IsLoading = false; UpdateCommands(); },
        () => !_closed && ReferenceEquals(SelectedChannel, channel), CancellationToken.None);
    }

    private bool CanSave() => !_closed && ValidationMessage.Length == 0;
    private bool CanPreview() => !_closed && !IsLoading && SelectedChannel != null && !string.IsNullOrWhiteSpace(Name) && _programs.Count > 0;
    private bool CanRetry() => !_closed && !IsLoading && SelectedChannel != null;
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => GuideLoadTask = LoadGuideAsync();
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private void Preview()
    {
        if (CanPreview()) PreviewRequested?.Invoke(new SeriesScheduleViewModel(Name.Trim(), SelectedChannel!, _programs, _clock));
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!CanSave()) return;
        Result = new(SelectedChannel!, Name.Trim(), MatchMode, OnlyNewEpisodes, int.Parse(PreBuffer), int.Parse(PostBuffer));
        CloseRequested?.Invoke(true);
    }
    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);
    public void Dispose() { _closed = true; _requests.Cancel(); IsLoading = false; UpdateCommands(); }
}
