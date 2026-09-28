using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public partial class PlaybackQualityViewModel : ObservableObject, IDisposable
{
    private readonly IStreamQualityService _service;
    private readonly Uri _source;
    private readonly StreamQualityOption _original;
    private readonly LatestRequestLoader _requests = new();
    private bool _closed;
    public PlaybackQualityViewModel(string title, Uri original, Uri source, IStreamQualityService service)
    {
        Title = title; _source = source; _service = service;
        _original = new StreamQualityOption("Automatic / original stream", original);
        _selectedQuality = _original;
        Qualities.ReplaceAll([_selectedQuality]);
    }

    public string Title { get; }
    public BulkObservableCollection<StreamQualityOption> Qualities { get; } = new();
    public StreamQualityOption? Result { get; private set; }
    public event Action<bool>? CloseRequested;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PlayCommand))] private StreamQualityOption? _selectedQuality;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ReloadCommand))] private bool _isLoading;
    [ObservableProperty] private string _status = "Choose Automatic to use the original stream, or wait for available qualities.";

    private bool CanReload() => !_closed && !IsLoading;
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        if (!CanReload()) return;
        var automatic = _original;
        SelectedQuality = automatic;
        Qualities.ReplaceAll([automatic]);
        IsLoading = true; Status = "Checking available stream qualities…";
        await _requests.LoadAsync(token => _service.DiscoverAsync(_source, token), discovery =>
        {
            // Automatic uses the advertised master after successful discovery; fallback
            // retains the original TS/MP4/playlist URL if the provider cannot be inspected.
            automatic = new StreamQualityOption("Automatic (player chooses)", discovery.Source);
            Qualities.ReplaceAll(new[] { automatic }.Concat(discovery.Options));
            SelectedQuality = automatic;
            Status = discovery.Options.Count > 0
                ? "Choose a quality. Lower bitrates use less bandwidth."
                : "The provider advertises no alternate qualities. You can play the original stream.";
        }, _ => Status = "Quality options could not be read. You can retry or play the original stream.",
        () => IsLoading = false, () => !_closed, CancellationToken.None);
    }
    private bool CanPlay() => !_closed && SelectedQuality != null && Qualities.Contains(SelectedQuality);
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (!CanPlay()) return;
        Result = SelectedQuality;
        Dispose();
        CloseRequested?.Invoke(true);
    }
    [RelayCommand]
    private void Cancel() { Dispose(); CloseRequested?.Invoke(false); }
    public void Dispose()
    {
        _closed = true; _requests.Cancel(); IsLoading = false;
        PlayCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
    }
}
