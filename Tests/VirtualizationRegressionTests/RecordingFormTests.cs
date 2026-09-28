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
    private static (SchedulerPageViewModel Page, FormScheduleService Service, FormInteraction Interaction, FormClock Clock) CreateRecordingFixture()
    {
        var service = new FormScheduleService();
        var interaction = new FormInteraction();
        var clock = new FormClock();
        return (new SchedulerPageViewModel(new RecordingFormViewModel(service, interaction, clock),
            new RecordingManagementViewModel(new ManagementService(), new ManagementInteraction())), service, interaction, clock);
    }

    private static void VerifyRecordingForm()
    {
        var (page, service, interaction, clock) = CreateRecordingFixture();
        var model = page.NewRecording;
        var a = new Channel { Id = 1, Name = "A: channel" };
        var b = new Channel { Id = 2, Name = "B channel" };
        var now = clock.GetUtcNow().UtcDateTime;
        model.Activate([b, a]);
        Check(model.IsGuideMode && !model.IsCustomMode && model.Channels[0] == a && model.StartTime == "20:00",
            "Recording form activates with explicit guide mode and sorted channels");
        model.SelectedChannel = a;
        var first = model.ProgramLoadTask;
        var requestA = service.Requests.Last();
        Check(model.IsLoadingPrograms && model.SelectedProgram == null && !model.RefreshProgramsCommand.CanExecute(null),
            "Selecting a channel starts one guide request and clears any previous program");
        model.SelectedChannel = b;
        var second = model.ProgramLoadTask;
        var requestB = service.Requests.Last();
        Check(requestA.Token.IsCancellationRequested, "Superseded scheduler guide requests are canceled");
        var current = new EpgEntry { Title = "Current program", Description = "Live description", StartUtc = now.AddMinutes(-10), EndUtc = now.AddMinutes(20) };
        requestB.Complete([
            new EpgEntry { Title = "Later", StartUtc = now.AddHours(1), EndUtc = now.AddHours(2) }, current,
            new EpgEntry { Title = "Ended", StartUtc = now.AddHours(-2), EndUtc = now.AddHours(-1) }]);
        AwaitSettings(second);
        requestA.Complete([new EpgEntry { Title = "Wrong channel", StartUtc = now, EndUtc = now.AddHours(1) }]);
        AwaitSettings(first);
        Check(model.Programs.Count == 2 && model.SelectedProgram?.Entry == current && model.SelectedProgram.IsLive && !model.IsLoadingPrograms,
            "Guide sorts upcoming programs and ignores a late response from the old channel");
        Check(current.Title == "Current program" && model.SelectedProgram!.Title.Contains("LIVE NOW") && model.Title == "Current program",
            "Live decoration belongs to the display row and never changes the guide or recording title");
        Check(model.OutputFilePath.Contains("Current program") && !model.OutputFilePath.Contains("LIVE NOW"),
            "Output naming uses the undecorated program title");
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 1 && service.Scheduled[0].ChannelId == b.Id && service.Scheduled[0].StartTime == now &&
            service.Scheduled[0].EndTime == current.EndUtc && service.Scheduled[0].Description == "Live description",
            "Scheduling a currently airing show starts now and retains the program's end time");
        Check(model.SelectedChannel == null && model.SelectedProgram == null && model.Programs.Count == 0 && model.OutputFilePath == "" && !model.StatusIsError,
            "Successful scheduling resets the entire draft without retaining stale program selection");
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 1 && model.StatusIsError, "A reset form cannot schedule its old program again");

        model.SelectedChannel = a;
        var broken = model.ProgramLoadTask;
        service.Requests.Last().Fail();
        AwaitSettings(broken);
        Check(!model.IsLoadingPrograms && model.GuideStatus.Contains("Unable") && model.RefreshProgramsCommand.CanExecute(null),
            "Guide errors leave loading state and enable retry");
        var retry = model.RefreshProgramsCommand.ExecuteAsync(null);
        service.Requests.Last().Complete(Enumerable.Range(0, 100).Reverse().Select(i => new EpgEntry
            { Title = $"Show {i}", StartUtc = now.AddMinutes(i), EndUtc = now.AddMinutes(i + 1) }).ToList());
        AwaitSettings(retry);
        Check(model.Programs.Count == 50 && model.Programs[0].Entry.Title == "Show 0" && model.Programs[^1].Entry.Title == "Show 49",
            "Guide retry applies a sorted, bounded program batch");
        model.SelectedProgram = new RecordingProgram(current, true);
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 1 && model.StatusIsError, "Scheduling rejects a program outside the current guide list");
        model.SelectedProgram = model.Programs[0];
        clock.UtcNow = now.AddHours(4);
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 1 && model.StatusMessage.Contains("ended"), "A program that ended while the form was open cannot be scheduled");
        clock.UtcNow = now;
        var inFlight = model.RefreshProgramsCommand.ExecuteAsync(null);
        var obsolete = service.Requests.Last();
        model.Mode = RecordingInputMode.Custom;
        obsolete.Complete([current]);
        AwaitSettings(inFlight);
        Check(obsolete.Token.IsCancellationRequested && model.Programs.Count == 0 && !model.IsLoadingPrograms && model.IsCustomMode,
            "Switching to custom times cancels guide work and cannot restore obsolete programs");

        model.CustomChannel = a;
        model.StartDate = now.Date.AddDays(1);
        model.StartTime = "23:30";
        model.EndTime = "01:15";
        model.Title = "A/B: Special";
        Check(model.OutputFilePath.EndsWith("2030-01-16_23-30.ts") && !Path.GetFileName(model.OutputFilePath).Contains(':') &&
            !Path.GetFileName(model.OutputFilePath).Contains('/'), "Custom output names use the entered time and sanitize title/channel characters");
        foreach (var invalid in new[] { "24:00", "20:99", "tomorrow", "" })
        {
            model.StartTime = invalid;
            model.ScheduleCommand.Execute(null);
            Check(service.Scheduled.Count == 1 && model.StatusIsError, $"Custom start time '{invalid}' is rejected without scheduling");
        }
        model.StartTime = "23:30";
        model.PreBuffer = "-1";
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 1 && model.StatusMessage.Contains("Buffer"), "Negative buffers fail form validation");
        model.PreBuffer = "2";
        model.PostBuffer = "abc";
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 1 && model.StatusMessage.Contains("Buffer"), "Nonnumeric buffers cannot silently use defaults");
        model.PostBuffer = "5";
        interaction.Output = @"C:\recordings\chosen.ts";
        model.BrowseOutputCommand.Execute(null);
        model.Title = "Changed title";
        model.StartTime = "23:45";
        Check(model.OutputFilePath == interaction.Output, "An explicitly browsed output path survives subsequent draft edits");
        interaction.Output = null;
        model.BrowseOutputCommand.Execute(null);
        Check(model.OutputFilePath == @"C:\recordings\chosen.ts", "Canceling output browse leaves the chosen path intact");
        service.Conflict = true;
        interaction.AllowConflict = false;
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 1 && interaction.ConflictPrompts == 1 && model.Title == "Changed title",
            "Declining a recording conflict preserves the draft and does not schedule");
        interaction.AllowConflict = true;
        model.ScheduleCommand.Execute(null);
        var scheduled = service.Scheduled.Last();
        Check(service.Scheduled.Count == 2 && scheduled.StartTime == now.Date.AddDays(1).AddHours(23).AddMinutes(45) &&
            scheduled.EndTime == now.Date.AddDays(2).AddHours(1).AddMinutes(15) && !scheduled.IsEpgBased,
            "Custom recordings preserve overnight end-time behavior and UTC conversion");
        Check(scheduled.PreBufferMinutes == 2 && scheduled.PostBufferMinutes == 5 && scheduled.OutputFilePath == @"C:\recordings\chosen.ts",
            "Scheduling passes the chosen output and buffer values to the existing scheduler");
        model.CustomChannel = a;
        model.StartDate = now.Date.AddDays(1);
        model.Title = "Keep this draft";
        service.FailSchedule = true;
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 2 && model.Title == "Keep this draft" && model.StatusIsError && model.StatusMessage.Contains("save failed"),
            "Scheduler failures surface without resetting the user's draft");
        service.FailSchedule = false;
        service.StreamUrl = "";
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 2 && model.StatusMessage.Contains("stream URL"), "Missing playlist stream URLs are rejected before scheduling");
        service.StreamUrl = "https://example.invalid/live";
        model.StartDate = null;
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 2 && model.StatusMessage.Contains("date"), "A missing recording date is validated");
        model.StartDate = now.Date.AddDays(-1);
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 2 && model.StatusMessage.Contains("past"), "Past custom recordings are rejected before submission");

        model.Mode = RecordingInputMode.Guide;
        model.SelectedChannel = b;
        var leaving = model.ProgramLoadTask;
        var leavingRequest = service.Requests.Last();
        model.Deactivate();
        leavingRequest.Complete([current]);
        AwaitSettings(leaving);
        Check(leavingRequest.Token.IsCancellationRequested && model.Programs.Count == 0 && !model.IsLoadingPrograms,
            "Leaving the scheduler cancels guide loading and rejects late results");
        model.ScheduleCommand.Execute(null);
        Check(service.Scheduled.Count == 2, "An inactive recording form cannot submit recordings");
        model.Activate([a, b]);
        var returning = model.ProgramLoadTask;
        service.Requests.Last().Complete([]);
        AwaitSettings(returning);
        Check(model.SelectedChannel == b && model.GuideStatus.Contains("No upcoming"), "Returning resumes the selected channel and explains an empty guide");

        VerifyRecordingFormBindings();
    }

    private static void VerifyRecordingFormBindings()
    {
        var (page, service, interaction, clock) = CreateRecordingFixture();
        var model = page.NewRecording;
        var channel = new Channel { Id = 1, Name = "Bound channel" };
        model.Activate([channel]);
        var view = new RecordingFormView { DataContext = model };
        using var source = new HwndSource(new HwndSourceParameters("Recording form fixture")
        { Width = 1000, Height = 1000, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
        source.RootVisual = view;
        Layout(view, 1000, 1000);
        var guidePanel = (Border)view.FindName("EpgPanel");
        var customPanel = (Border)view.FindName("CustomPanel");
        Check(guidePanel.IsEnabled && !customPanel.IsEnabled, "Recording mode bindings enable the guide and disable custom fields");
        var combo = (ComboBox)view.FindName("ChannelCombo");
        combo.SetCurrentValue(ComboBox.SelectedItemProperty, channel);
        Layout(view, 1000, 1000);
        var pending = model.ProgramLoadTask;
        Check(model.SelectedChannel == channel && service.Requests.Count == 1, "Channel selection is bound directly to the form model");
        var now = clock.GetUtcNow().UtcDateTime;
        service.Requests.Last().Complete([new EpgEntry { Title = "Bound show", StartUtc = now, EndUtc = now.AddHours(1) }]);
        AwaitSettings(pending);
        Layout(view, 1000, 1000);
        Check(((ComboBox)view.FindName("ProgramCombo")).Items.Count == 1 && ((TextBox)view.FindName("TitleBox")).Text == "Bound show",
            "Guide results and selected program title render through bindings");
        ((RadioButton)view.FindName("CustomRadio")).SetCurrentValue(RadioButton.IsCheckedProperty, true);
        Layout(view, 1000, 1000);
        Check(model.Mode == RecordingInputMode.Custom && customPanel.IsEnabled && !guidePanel.IsEnabled,
            "Radio selection updates explicit mode and both actual Border panels");
        ((ComboBox)view.FindName("CustomChannelCombo")).SetCurrentValue(ComboBox.SelectedItemProperty, channel);
        ((TextBox)view.FindName("StartTimeBox")).SetCurrentValue(TextBox.TextProperty, "22:15");
        ((TextBox)view.FindName("TitleBox")).SetCurrentValue(TextBox.TextProperty, "Typed title");
        Layout(view, 1000, 1000);
        Check(model.StartTime == "22:15" && model.Title == "Typed title" && model.OutputFilePath.Contains("22-15"),
            "Typing custom times and title updates model state and the proposed output path");
        Check(ReferenceEquals(((Button)view.FindName("ScheduleButton")).Command, model.ScheduleCommand),
            "The compiled form resolves its scheduling command without a DashboardWindow");
        model.Deactivate();
    }

    private sealed class FormClock : TimeProvider
    {
        public DateTime UtcNow = new(2030, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private sealed class GuideRequest(Channel channel, CancellationToken token)
    {
        public Channel Channel { get; } = channel;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<List<EpgEntry>> Completion { get; } = new();
        public void Complete(List<EpgEntry> programs) => Completion.SetResult(programs);
        public void Fail() => Completion.SetException(new IOException("Guide unavailable"));
    }
    private sealed class FormScheduleService : IRecordingScheduleService
    {
        public List<GuideRequest> Requests { get; } = new();
        public List<ScheduledRecording> Scheduled { get; } = new();
        public bool Conflict, FailSchedule;
        public string StreamUrl = "https://example.invalid/live";
        public string RecordingDirectory => @"C:\recordings";
        public Task<List<EpgEntry>> LoadProgramsAsync(Channel channel, CancellationToken token)
        {
            var request = new GuideRequest(channel, token);
            Requests.Add(request);
            return request.Completion.Task;
        }
        public string GetStreamUrl(Channel channel) => StreamUrl;
        public bool HasConflict(DateTime start, DateTime end) => Conflict;
        public void Schedule(ScheduledRecording recording)
        {
            if (FailSchedule) throw new IOException("save failed");
            Scheduled.Add(recording);
        }
    }
    private sealed class FormInteraction : IRecordingFormInteraction
    {
        public string? Output;
        public bool AllowConflict = true;
        public int ConflictPrompts;
        public string? BrowseOutput(string currentPath) => Output;
        public bool ConfirmConflict(ScheduledRecording recording) { ConflictPrompts++; return AllowConflict; }
        public void Notify(string message, bool isError = false) { }
    }
}
