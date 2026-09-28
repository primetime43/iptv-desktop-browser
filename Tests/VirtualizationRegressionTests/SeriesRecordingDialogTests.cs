using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

internal static partial class Program
{
    private static void VerifySeriesRecordingDialogs()
    {
        var clock = new FormClock();
        var now = clock.UtcNow;
        var service = new FormScheduleService();
        var a = new Channel { Id = 1, Name = "Alpha" };
        var b = new Channel { Id = 2, Name = "Beta" };
        EpgEntry Episode(string title, int hours) => new() { Title = title, StartUtc = now.AddHours(hours), EndUtc = now.AddHours(hours + 1) };
        using var model = new SeriesRecordingDialogViewModel(service, [a, b], clock: clock);
        Check(!model.SaveCommand.CanExecute(null), "Series dialog initially requires a channel and name");
        model.SelectedChannel = a;
        var oldTask = model.GuideLoadTask;
        var old = service.Requests.Last();
        model.SelectedChannel = b;
        var newTask = model.GuideLoadTask;
        service.Requests.Last().Complete([Episode("Example S01E02 [NEW]", 2), Episode("Example S01E01", 1), Episode("Other", -1)]);
        AwaitSettings(newTask);
        old.Complete([Episode("Wrong channel", 1)]);
        AwaitSettings(oldTask);
        Check(old.Token.IsCancellationRequested && model.Titles.SequenceEqual(new[] { "Example", "Other" }),
            "Series channel switching cancels obsolete work and rejects ignored cancellation");
        model.Name = "Example";
        Check(model.NextAiring.Contains("Next airing:") && model.SaveCommand.CanExecute(null), "Typing a show name recalculates next airing and validation");
        SeriesScheduleViewModel? preview = null;
        model.PreviewRequested += value => preview = value;
        model.PreviewCommand.Execute(null);
        Check(preview?.Episodes.Count == 2 && preview.Episodes[0].StartUtc == now.AddHours(1), "Preview normalizes episode metadata and sorts future airings");
        preview?.Dispose();
        foreach (var invalid in new[] { "-1", "1.5", "bad", "2147483648", "1441" })
        {
            model.PreBuffer = invalid;
            Check(!model.SaveCommand.CanExecute(null), "Series buffers reject invalid value " + invalid);
        }
        model.PreBuffer = "0";
        bool? closeResult = null;
        model.CloseRequested += value => closeResult = value;
        model.SaveCommand.Execute(null);
        Check(closeResult == true && model.Result is { Name: "Example", PreBuffer: 0, PostBuffer: 5, Channel.Id: 2 }, "Series save produces a validated detached draft");

        model.SelectedChannel = a;
        var failing = model.GuideLoadTask;
        service.Requests.Last().Fail();
        AwaitSettings(failing);
        model.Name = "Manual show";
        Check(model.GuideStatus.Contains("could not") && model.SaveCommand.CanExecute(null), "Guide failure is visible and allows manual series entry");
        var retry = model.RetryCommand.ExecuteAsync(null);
        service.Requests.Last().Complete([]);
        AwaitSettings(retry);
        Check(model.GuideStatus.Contains("No guide") && !model.PreviewCommand.CanExecute(null), "An empty guide keeps the form usable and disables preview");
        var closing = model.RetryCommand.ExecuteAsync(null);
        var closingRequest = service.Requests.Last();
        model.Dispose();
        closingRequest.Complete([Episode("Late", 1)]);
        AwaitSettings(closing);
        Check(closingRequest.Token.IsCancellationRequested && model.Titles.Count == 0 && !model.SaveCommand.CanExecute(null), "Closing the series form cancels loading and blocks late application and saving");

        var existing = new SeriesRecording { SeriesName = "Original", ChannelId = a.Id, PreBufferMinutes = 4, PostBufferMinutes = 8,
            OnlyNewEpisodes = false, MatchMode = SeriesMatchMode.TitleExact };
        using var edit = new SeriesRecordingDialogViewModel(service, [a], existing, clock);
        var editTask = edit.GuideLoadTask;
        service.Requests.Last().Complete([]);
        AwaitSettings(editTask);
        Check(edit.IsEditing && !edit.CanChangeChannel && edit.Name == "Original" && edit.PreBuffer == "4" && !edit.OnlyNewEpisodes,
            "Editing restores existing rule settings and retains the channel");
        edit.Name = "Changed";
        edit.CancelCommand.Execute(null);
        Check(edit.Result == null && existing.SeriesName == "Original", "Canceling a series edit leaves the original rule untouched");
        edit.SaveCommand.Execute(null);
        Check(edit.Result?.Name == "Changed" && existing.SeriesName == "Original", "Saving returns edits without mutating scheduler state inside the dialog");

        using var schedule = new SeriesScheduleViewModel("Example", a, service, clock);
        var failedLoad = schedule.ReloadCommand.ExecuteAsync(null);
        service.Requests.Last().Fail(); AwaitSettings(failedLoad);
        Check(schedule.Status.Contains("could not") && schedule.ReloadCommand.CanExecute(null), "Schedule errors are visible and retryable");
        var scheduleRetry = schedule.ReloadCommand.ExecuteAsync(null);
        service.Requests.Last().Complete([Episode("Example", -1), Episode("Example [NEW]", 2)]);
        AwaitSettings(scheduleRetry);
        Check(schedule.Episodes.Count == 1, "Schedule retry recovers and excludes past episodes");
        var scheduleClosing = schedule.ReloadCommand.ExecuteAsync(null);
        var pending = service.Requests.Last(); schedule.Dispose(); pending.Complete([Episode("Example", 4)]);
        AwaitSettings(scheduleClosing);
        Check(pending.Token.IsCancellationRequested && schedule.Episodes[0].StartUtc == now.AddHours(2), "Closed schedule dialogs ignore late responses");

        VerifySeriesDialogBindings(service, a, clock);
    }

    private static void VerifySeriesDialogBindings(FormScheduleService service, Channel channel, FormClock clock)
    {
        using var model = new SeriesRecordingDialogViewModel(service, [channel], clock: clock);
        var dialog = new SeriesRecordingWindow(model);
        var content = (FrameworkElement)dialog.Content;
        content.DataContext = model;
        using var source = new HwndSource(new HwndSourceParameters("Series dialog fixture")
        { Width = 600, Height = 700, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
        source.RootVisual = content;
        Layout(content, 600, 700);
        ((ComboBox)dialog.FindName("ChannelBox")).SetCurrentValue(ComboBox.SelectedItemProperty, channel);
        var load = model.GuideLoadTask;
        service.Requests.Last().Complete([]); AwaitSettings(load);
        ((ComboBox)dialog.FindName("ShowBox")).SetCurrentValue(ComboBox.TextProperty, "Typed show");
        Layout(content, 600, 700);
        Check(model.Name == "Typed show" && ((Button)dialog.FindName("SaveButton")).IsEnabled, "Compiled series dialog binds editable text and save validation without the dashboard");
        dialog.Close();

        using var schedule = new SeriesScheduleViewModel("Example", channel, Enumerable.Range(1, 10000).Select(i => new EpgEntry
        { Title = "Example", StartUtc = clock.UtcNow.AddHours(i), EndUtc = clock.UtcNow.AddHours(i + 1) }), clock);
        var scheduleWindow = new SeriesScheduleWindow(schedule);
        var scheduleContent = (FrameworkElement)scheduleWindow.Content;
        scheduleContent.DataContext = schedule;
        source.RootVisual = scheduleContent;
        Layout(scheduleContent, 720, 520);
        var list = (ListBox)scheduleWindow.FindName("EpisodesList");
        var count = Descendants(list).OfType<ListBoxItem>().Count();
        Check(list.Items.Count == 10000 && count > 0 && count < 100, "Compiled series schedule virtualizes 10,000 airings in a bounded viewport");
        scheduleWindow.Close();
    }
}
