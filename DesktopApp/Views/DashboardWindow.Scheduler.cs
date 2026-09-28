using System.Windows;
using System.Windows.Controls;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

namespace DesktopApp.Views;

public partial class DashboardWindow
{
    private IRecordingScheduleService CreateSeriesGuideService() => new RecordingScheduleService(_channelService, _scheduler);

    private void InitializeScheduler()
    {
        SchedulerPageModel.NewRecording.Activate(Channels);
        if (FindDashboardElement("SeriesGrid") is DataGrid grid) grid.ItemsSource = _scheduler.SeriesRecordings;
    }

    internal void AddSeriesRecording_Click(object sender, RoutedEventArgs e)
    {
        var service = CreateSeriesGuideService();
        using var model = new SeriesRecordingDialogViewModel(service, Channels);
        var window = new SeriesRecordingWindow(model) { Owner = this };
        if (window.ShowDialog() != true || model.Result is not { } draft || _isClosing) return;
        var streamUrl = service.GetStreamUrl(draft.Channel);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            MessageBox.Show(this, "No stream is available for this channel.", "Series Recording");
            return;
        }
        var series = new SeriesRecording
        {
            SeriesName = draft.Name, ChannelId = draft.Channel.Id, ChannelName = draft.Channel.Name,
            StreamUrl = streamUrl, MatchMode = draft.MatchMode, OnlyNewEpisodes = draft.OnlyNewEpisodes,
            PreBufferMinutes = draft.PreBuffer, PostBufferMinutes = draft.PostBuffer
        };
        _scheduler.AddSeriesRecording(series);
        OnEpgRefreshNeeded(series);
    }

    internal void ViewSeriesSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SeriesRecording series } || !_scheduler.SeriesRecordings.Contains(series)) return;
        using var model = new SeriesScheduleViewModel(series.SeriesName, SeriesChannel(series), CreateSeriesGuideService());
        new SeriesScheduleWindow(model) { Owner = this }.ShowDialog();
    }

    private Channel SeriesChannel(SeriesRecording series) => Channels.FirstOrDefault(c => c.Id == series.ChannelId)
        ?? new Channel { Id = series.ChannelId, Name = series.ChannelName };

    // Timer callbacks enter through the dispatcher. Reject results for removed or reloaded rules.
    private async void OnEpgRefreshNeeded(SeriesRecording series)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(() => OnEpgRefreshNeeded(series));
            return;
        }
        if (_isClosing || !_scheduler.SeriesRecordings.Contains(series)) return;
        try
        {
            var programs = await CreateSeriesGuideService().LoadProgramsAsync(SeriesChannel(series), _cts.Token);
            if (!_isClosing && _scheduler.SeriesRecordings.Contains(series))
                _scheduler.CheckForNewEpisodes(series.ChannelId, programs);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Log("[Series] The program guide could not be refreshed. The scheduler will retry.\n"); }
    }

    internal void EditSeriesRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SeriesRecording series } || !_scheduler.SeriesRecordings.Contains(series)) return;
        using var model = new SeriesRecordingDialogViewModel(CreateSeriesGuideService(), new[] { SeriesChannel(series) }, series);
        if (new SeriesRecordingWindow(model) { Owner = this }.ShowDialog() != true || model.Result is not { } draft ||
            _isClosing || !_scheduler.SeriesRecordings.Contains(series)) return;
        series.SeriesName = draft.Name;
        series.MatchMode = draft.MatchMode;
        series.OnlyNewEpisodes = draft.OnlyNewEpisodes;
        series.PreBufferMinutes = draft.PreBuffer;
        series.PostBufferMinutes = draft.PostBuffer;
        _scheduler.UpdateSeriesRecording(series);
        OnEpgRefreshNeeded(series);
    }

    internal void DeleteSeriesRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SeriesRecording series }) return;
        if (MessageBox.Show(this, $"Delete the series recording for '{series.SeriesName}'?\n\nUpcoming scheduled recordings for this series will also be canceled.",
            "Delete Series Recording", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes &&
            !_isClosing && _scheduler.SeriesRecordings.Contains(series)) _scheduler.RemoveSeriesRecording(series.Id);
    }

    private readonly RecordingScheduler _scheduler = RecordingScheduler.Instance;
    private void OnScheduledRecordingFailed(ScheduledRecording recording)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || !_scheduler.ScheduledRecordings.Contains(recording)) return;
            var summary = recording.FailureReason?.Split('\n')[0] ?? "Open recording properties for details.";
            ShowToast("Recording failed", $"{recording.Title}: {summary}", "#DC3545");
        });
    }
}
