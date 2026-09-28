using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public partial class SeriesScheduleViewModel : ObservableObject, IDisposable
{
    private readonly LatestRequestLoader _requests = new();
    private readonly IRecordingScheduleService? _service;
    private readonly Channel _channel;
    private readonly string _name;
    private readonly TimeProvider _clock;
    private bool _closed;
    public SeriesScheduleViewModel(string name, Channel channel, IEnumerable<EpgEntry> programs, TimeProvider? clock = null)
    {
        _name = name; _channel = channel; _clock = clock ?? TimeProvider.System;
        Apply(SeriesGuide.FindEpisodes(programs, _name, _clock.GetUtcNow().UtcDateTime));
    }
    public SeriesScheduleViewModel(string name, Channel channel, IRecordingScheduleService service, TimeProvider? clock = null)
        : this(name, channel, Array.Empty<EpgEntry>(), clock) => _service = service;

    public string WindowTitle => $"Schedule: {_name} on {_channel.Name}";
    public string Explanation => "Upcoming guide airings with this show name. Recording rules and duplicate checks may exclude some airings.";
    public BulkObservableCollection<EpgEntry> Episodes { get; } = new();
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isLoading;
    private void Apply(IEnumerable<EpgEntry> programs)
    {
        Episodes.ReplaceAll(programs);
        Status = Episodes.Count == 0 ? "No upcoming episodes found in the guide." : $"{Episodes.Count} upcoming episode(s).";
    }
    private bool CanReload() => !_closed && !IsLoading && _service != null;
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        if (!CanReload()) return;
        IsLoading = true; Status = "Loading program guide…"; ReloadCommand.NotifyCanExecuteChanged();
        await _requests.LoadAsync(async token =>
        {
            var entries = await _service!.LoadProgramsAsync(_channel, token).ConfigureAwait(false);
            return await Task.Run(() => SeriesGuide.FindEpisodes(entries, _name, _clock.GetUtcNow().UtcDateTime).ToArray(), token).ConfigureAwait(false);
        }, Apply, _ => Status = "The guide could not be loaded. Try again.",
        () => { IsLoading = false; ReloadCommand.NotifyCanExecuteChanged(); }, () => !_closed, CancellationToken.None);
    }
    public void Dispose() { _closed = true; _requests.Cancel(); IsLoading = false; ReloadCommand.NotifyCanExecuteChanged(); }
}
