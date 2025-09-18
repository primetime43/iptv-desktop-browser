using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DesktopApp.Models;

public class ScheduledRecording : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public Guid Id { get; set; } = Guid.NewGuid();

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set
        {
            if (_description != value)
            {
                _description = value;
                OnPropertyChanged();
            }
        }
    }

    public int ChannelId { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;

    private DateTime _startTime;
    public DateTime StartTime
    {
        get => _startTime;
        set
        {
            if (_startTime != value)
            {
                _startTime = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartTimeLocal));
                OnPropertyChanged(nameof(Duration));
            }
        }
    }

    private DateTime _endTime;
    public DateTime EndTime
    {
        get => _endTime;
        set
        {
            if (_endTime != value)
            {
                _endTime = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EndTimeLocal));
                OnPropertyChanged(nameof(Duration));
            }
        }
    }

    private RecordingScheduleStatus _status = RecordingScheduleStatus.Scheduled;
    public RecordingScheduleStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public string OutputFilePath { get; set; } = string.Empty;

    // EPG-based scheduling properties
    public bool IsEpgBased { get; set; }
    public string? EpgProgramId { get; set; }

    // Pre-buffer and post-buffer (in minutes)
    public int PreBufferMinutes { get; set; } = 2;
    public int PostBufferMinutes { get; set; } = 5;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Internal process reference for stopping recording (not serialized)
    [System.Text.Json.Serialization.JsonIgnore]
    public Process? RecordingProcess { get; set; }

    // Display properties
    public string StartTimeLocal => StartTime.ToLocalTime().ToString("MM/dd/yyyy hh:mm tt");
    public string EndTimeLocal => EndTime.ToLocalTime().ToString("MM/dd/yyyy hh:mm tt");
    public TimeSpan Duration => EndTime - StartTime;
    public string DurationText => Duration.TotalMinutes < 60
        ? $"{Duration.TotalMinutes:F0}m"
        : $"{Duration.Hours}h {Duration.Minutes}m";

    public string StatusText => Status switch
    {
        RecordingScheduleStatus.Scheduled => "Scheduled",
        RecordingScheduleStatus.Recording => "Recording",
        RecordingScheduleStatus.Completed => "Completed",
        RecordingScheduleStatus.Failed => "Failed",
        RecordingScheduleStatus.Cancelled => "Cancelled",
        RecordingScheduleStatus.Missed => "Missed",
        _ => "Unknown"
    };

    public bool CanCancel => Status == RecordingScheduleStatus.Scheduled || Status == RecordingScheduleStatus.Recording;
    public bool CanEdit => Status == RecordingScheduleStatus.Scheduled;

    public string TimeRangeText => $"{StartTimeLocal} - {EndTimeLocal} ({DurationText})";

    // Check if this recording should start now (within a tolerance)
    public bool ShouldStartNow(TimeSpan tolerance = default)
    {
        if (tolerance == default) tolerance = TimeSpan.FromMinutes(2); // Increased tolerance to 2 minutes
        var now = DateTime.UtcNow;
        var adjustedStartTime = StartTime.AddMinutes(-PreBufferMinutes);

        return Status == RecordingScheduleStatus.Scheduled &&
               now >= adjustedStartTime &&
               now <= adjustedStartTime.Add(tolerance);
    }

    // Check if this recording should stop now
    public bool ShouldStopNow(TimeSpan tolerance = default)
    {
        if (tolerance == default) tolerance = TimeSpan.FromMinutes(2); // Increased tolerance to 2 minutes
        var now = DateTime.UtcNow;
        var adjustedEndTime = EndTime.AddMinutes(PostBufferMinutes);
        return Status == RecordingScheduleStatus.Recording &&
               now >= adjustedEndTime;
    }

    // Check if this recording has been missed
    public bool IsMissed()
    {
        var now = DateTime.UtcNow;
        var adjustedStartTime = StartTime.AddMinutes(-PreBufferMinutes);
        return Status == RecordingScheduleStatus.Scheduled &&
               now > adjustedStartTime.AddMinutes(5); // 5 minute grace period
    }
}

public enum RecordingScheduleStatus
{
    Scheduled,
    Recording,
    Completed,
    Failed,
    Cancelled,
    Missed
}

public enum RecordingType
{
    Manual,      // User-defined time range
    EpgProgram,  // Based on EPG program data
    Series       // Recurring series recording (future enhancement)
}