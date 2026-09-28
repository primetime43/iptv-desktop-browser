using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Security;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public enum RecordingInputMode { Guide, Custom }
public sealed record RecordingProgram(EpgEntry Entry, bool IsLive)
{
    public string Title => IsLive ? $"🔴 {Entry.Title} (LIVE NOW)" : Entry.Title;
    public DateTime StartTimeLocal => Entry.StartUtc.ToLocalTime();
    public string TimeRangeLocal => Entry.TimeRangeLocal;
}

public partial class RecordingFormViewModel : ObservableObject
{
    private readonly IRecordingScheduleService _service;
    private readonly IRecordingFormInteraction _interaction;
    private readonly TimeProvider _clock;
    private readonly LatestRequestLoader _programRequests = new();
    private bool _active;
    private bool _resetting;
    private bool _manualOutput;

    public RecordingFormViewModel(IRecordingScheduleService service, IRecordingFormInteraction interaction, TimeProvider? clock = null)
    {
        _service = service;
        _interaction = interaction;
        _clock = clock ?? TimeProvider.System;
        StartDate = _clock.GetLocalNow().Date;
    }

    public BulkObservableCollection<Channel> Channels { get; } = new();
    public BulkObservableCollection<RecordingProgram> Programs { get; } = new();
    public Task ProgramLoadTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private RecordingInputMode _mode;
    [ObservableProperty] private Channel? _selectedChannel;
    [ObservableProperty] private RecordingProgram? _selectedProgram;
    [ObservableProperty] private RecordingProgram? _hoveredProgram;
    [ObservableProperty] private Channel? _customChannel;
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private string _startTime = "20:00";
    [ObservableProperty] private string _endTime = "21:00";
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _preBuffer = "2";
    [ObservableProperty] private string _postBuffer = "5";
    [ObservableProperty] private string _outputFilePath = string.Empty;
    [ObservableProperty] private bool _isLoadingPrograms;
    [ObservableProperty] private string _guideStatus = "Select a channel to load its program guide.";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _statusIsError;

    public bool IsGuideMode { get => Mode == RecordingInputMode.Guide; set { if (value) Mode = RecordingInputMode.Guide; } }
    public bool IsCustomMode { get => Mode == RecordingInputMode.Custom; set { if (value) Mode = RecordingInputMode.Custom; } }
    public string ProgramTimeText
    {
        get
        {
            var program = (HoveredProgram ?? SelectedProgram)?.Entry;
            if (program == null) return "Hover over a show to see air time";
            var duration = program.EndUtc - program.StartUtc;
            return $"📅 {program.StartUtc.ToLocalTime():ddd, MMM dd yyyy}  •  🕐 {program.TimeRangeLocal}  •  ⏱ {(int)duration.TotalHours}h {duration.Minutes}m";
        }
    }

    public void Activate(IEnumerable<Channel> channels)
    {
        _active = true;
        _resetting = true;
        try
        {
            Channels.ReplaceAll(channels.OrderBy(channel => channel.Name));
            if (SelectedChannel != null && !Channels.Contains(SelectedChannel)) SelectedChannel = null;
            if (CustomChannel != null && !Channels.Contains(CustomChannel)) CustomChannel = null;
        }
        finally { _resetting = false; }
        ProgramLoadTask = ReloadProgramsAsync();
        UpdateOutputPath();
    }

    public void Deactivate()
    {
        _active = false;
        CancelPrograms();
        GuideStatus = "Select a channel to load its program guide.";
    }

    partial void OnModeChanged(RecordingInputMode value)
    {
        OnPropertyChanged(nameof(IsGuideMode));
        OnPropertyChanged(nameof(IsCustomMode));
        ProgramLoadTask = ReloadProgramsAsync();
        UpdateOutputPath();
    }
    partial void OnSelectedChannelChanged(Channel? value)
    {
        if (!_resetting) ProgramLoadTask = ReloadProgramsAsync();
        UpdateOutputPath();
    }
    partial void OnSelectedProgramChanged(RecordingProgram? value)
    {
        HoveredProgram = null;
        if (value != null) Title = value.Entry.Title;
        OnPropertyChanged(nameof(ProgramTimeText));
        UpdateOutputPath();
    }
    partial void OnHoveredProgramChanged(RecordingProgram? value) => OnPropertyChanged(nameof(ProgramTimeText));
    partial void OnCustomChannelChanged(Channel? value) => UpdateOutputPath();
    partial void OnStartDateChanged(DateTime? value) => UpdateOutputPath();
    partial void OnStartTimeChanged(string value) => UpdateOutputPath();
    partial void OnTitleChanged(string value) => UpdateOutputPath();
    partial void OnIsLoadingProgramsChanged(bool value) => RefreshProgramsCommand.NotifyCanExecuteChanged();

    private void CancelPrograms()
    {
        _programRequests.Cancel();
        IsLoadingPrograms = false;
        SelectedProgram = null;
        HoveredProgram = null;
        Programs.ReplaceAll([]);
        RefreshProgramsCommand.NotifyCanExecuteChanged();
    }

    private bool CanRefreshPrograms() => _active && IsGuideMode && SelectedChannel != null && !IsLoadingPrograms;
    [RelayCommand(CanExecute = nameof(CanRefreshPrograms), AllowConcurrentExecutions = true)]
    private Task RefreshProgramsAsync() => ProgramLoadTask = ReloadProgramsAsync();

    private async Task ReloadProgramsAsync()
    {
        CancelPrograms();
        GuideStatus = "Select a channel to load its program guide.";
        if (!_active || !IsGuideMode || SelectedChannel is not { } channel) return;
        IsLoadingPrograms = true;
        GuideStatus = "Loading programs...";
        await _programRequests.LoadAsync(async token =>
        {
            var entries = await _service.LoadProgramsAsync(channel, token);
            var now = _clock.GetUtcNow().UtcDateTime;
            return await Task.Run(() => entries.Where(entry => entry.EndUtc > now && entry.EndUtc > entry.StartUtc)
                .OrderBy(entry => entry.StartUtc).Take(50)
                .Select(entry => new RecordingProgram(entry, entry.StartUtc <= now)).ToList(), token);
        }, programs =>
        {
            Programs.ReplaceAll(programs);
            SelectedProgram = Programs.FirstOrDefault();
            GuideStatus = programs.Count == 0 ? "No upcoming programs. Try custom times or refresh the guide." : $"{programs.Count} programs available";
        }, _ => GuideStatus = "Unable to load the program guide. Use Refresh Guide to try again.",
            () => IsLoadingPrograms = false,
            () => _active && IsGuideMode && ReferenceEquals(SelectedChannel, channel), CancellationToken.None);
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        try
        {
            if (_interaction.BrowseOutput(OutputFilePath) is not { } path) return;
            _manualOutput = true;
            OutputFilePath = path;
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    [RelayCommand]
    private void Schedule()
    {
        try
        {
            if (!_active) return;
            if (!int.TryParse(PreBuffer, out var pre) || pre < 0 || !int.TryParse(PostBuffer, out var post) || post < 0)
                throw new ArgumentException("Buffer times must be non-negative whole minutes.");
            var now = _clock.GetUtcNow().UtcDateTime;
            Channel channel;
            ScheduledRecording recording;
            if (IsGuideMode)
            {
                if (SelectedChannel == null || SelectedProgram == null || !Programs.Contains(SelectedProgram))
                    throw new ArgumentException("Please select a channel and program.");
                channel = SelectedChannel;
                var program = SelectedProgram.Entry;
                if (program.EndUtc <= now) throw new ArgumentException("Cannot record a show that has already ended.");
                recording = new ScheduledRecording
                {
                    Title = program.Title, Description = program.Description ?? "",
                    StartTime = program.StartUtc < now ? now : program.StartUtc, EndTime = program.EndUtc,
                    IsEpgBased = true, EpgProgramId = program.GetHashCode().ToString()
                };
            }
            else
            {
                channel = CustomChannel ?? throw new ArgumentException("Please select a channel.");
                if (!TryTime(StartTime, out var start) || !TryTime(EndTime, out var end))
                    throw new ArgumentException("Please enter valid start and end times (HH:mm format).");
                if (!StartDate.HasValue) throw new ArgumentException("Please choose a recording date.");
                var date = StartDate.Value.Date;
                var startLocal = date.Add(start.ToTimeSpan());
                var endLocal = date.Add(end.ToTimeSpan());
                if (endLocal <= startLocal) endLocal = endLocal.AddDays(1);
                var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified), _clock.LocalTimeZone);
                var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), _clock.LocalTimeZone);
                if (startUtc <= now.AddMinutes(-pre)) throw new ArgumentException("Cannot schedule recording in the past.");
                recording = new ScheduledRecording
                {
                    Title = string.IsNullOrWhiteSpace(Title) ? "Custom Recording" : Title.Trim(),
                    StartTime = startUtc, EndTime = endUtc, IsEpgBased = false
                };
            }
            if (!Channels.Contains(channel)) throw new ArgumentException("Please select a channel from the current list.");
            recording.ChannelId = channel.Id;
            recording.ChannelName = channel.Name;
            recording.StreamUrl = _service.GetStreamUrl(channel);
            if (string.IsNullOrWhiteSpace(recording.StreamUrl)) throw new ArgumentException("The selected channel has no stream URL.");
            recording.PreBufferMinutes = pre;
            recording.PostBufferMinutes = post;
            recording.OutputFilePath = OutputFilePath;
            if (_service.HasConflict(recording.StartTime, recording.EndTime) && !_interaction.ConfirmConflict(recording)) return;
            _service.Schedule(recording);
            Reset();
            StatusIsError = false;
            StatusMessage = $"Recording scheduled successfully!\n\nTitle: {recording.Title}\nTime: {recording.TimeRangeText}";
            _interaction.Notify(StatusMessage);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void Fail(string message)
    {
        StatusIsError = true;
        StatusMessage = DiagnosticRedactor.Redact(message);
        _interaction.Notify(StatusMessage, true);
    }

    public void Reset()
    {
        _resetting = true;
        try
        {
            CancelPrograms();
            SelectedChannel = CustomChannel = null;
            Title = "";
            StartDate = _clock.GetLocalNow().Date;
            StartTime = "20:00";
            EndTime = "21:00";
            PreBuffer = "2";
            PostBuffer = "5";
            _manualOutput = false;
            OutputFilePath = "";
            GuideStatus = "Select a channel to load its program guide.";
        }
        finally { _resetting = false; }
        RefreshProgramsCommand.NotifyCanExecuteChanged();
    }

    private void UpdateOutputPath()
    {
        if (_resetting || _manualOutput) return;
        var channel = IsGuideMode ? SelectedChannel : CustomChannel;
        DateTime? start = IsGuideMode ? SelectedProgram?.Entry.StartUtc.ToLocalTime() :
            StartDate.HasValue && TryTime(StartTime, out var time) ? StartDate.Value.Date.Add(time.ToTimeSpan()) : null;
        if (channel == null || start == null) { OutputFilePath = ""; return; }
        var title = IsGuideMode ? SelectedProgram!.Entry.Title : string.IsNullOrWhiteSpace(Title) ? "Custom_Recording" : Title;
        OutputFilePath = Path.Combine(_service.RecordingDirectory,
            $"{SanitizeFileName(channel.Name)}_{SanitizeFileName(title)}_{start:yyyy-MM-dd_HH-mm}.ts");
    }

    private static bool TryTime(string text, out TimeOnly time) => TimeOnly.TryParseExact(text.Trim(),
        ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string SanitizeFileName(string value)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(value, @"[\uD800-\uDBFF\uDC00-\uDFFF]", "");
        var result = string.Join("_", cleaned.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim('_', ' ');
        return result.Length == 0 ? "Recording" : result;
    }
}
