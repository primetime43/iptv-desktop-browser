using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopApp.Models;

/// <summary>
/// Represents a series recording rule that automatically schedules recordings
/// for new episodes of a show based on EPG data.
/// </summary>
public class SeriesRecording : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public Guid Id { get; set; } = Guid.NewGuid();

    private string _seriesName = string.Empty;
    public string SeriesName
    {
        get => _seriesName;
        set
        {
            if (_seriesName != value)
            {
                _seriesName = value;
                OnPropertyChanged();
            }
        }
    }

    private int _channelId;
    public int ChannelId
    {
        get => _channelId;
        set
        {
            if (_channelId != value)
            {
                _channelId = value;
                OnPropertyChanged();
            }
        }
    }

    public string ChannelName { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;

    // Title matching pattern for identifying episodes
    private SeriesMatchMode _matchMode = SeriesMatchMode.TitleContains;
    public SeriesMatchMode MatchMode
    {
        get => _matchMode;
        set
        {
            if (_matchMode != value)
            {
                _matchMode = value;
                OnPropertyChanged();
            }
        }
    }

    // Only record new episodes (not repeats/reruns)
    private bool _onlyNewEpisodes = true;
    public bool OnlyNewEpisodes
    {
        get => _onlyNewEpisodes;
        set
        {
            if (_onlyNewEpisodes != value)
            {
                _onlyNewEpisodes = value;
                OnPropertyChanged();
            }
        }
    }

    // Pre-buffer and post-buffer (in minutes)
    public int PreBufferMinutes { get; set; } = 2;
    public int PostBufferMinutes { get; set; } = 5;

    // Recording quality/settings
    public string? CustomFfmpegArgs { get; set; }
    public string? OutputDirectory { get; set; }

    // Status
    private bool _isActive = true;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastRecordedUtc { get; set; }

    // Track recorded episodes to avoid duplicates
    public HashSet<string> RecordedEpisodeTitles { get; set; } = new();

    // Next recording information
    private DateTime? _nextRecordingTime;
    public DateTime? NextRecordingTime
    {
        get => _nextRecordingTime;
        set
        {
            if (_nextRecordingTime != value)
            {
                _nextRecordingTime = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NextRecordingDisplay));
            }
        }
    }

    private string? _nextRecordingTitle;
    public string? NextRecordingTitle
    {
        get => _nextRecordingTitle;
        set
        {
            if (_nextRecordingTitle != value)
            {
                _nextRecordingTitle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NextRecordingDisplay));
            }
        }
    }

    private string _recurrencePattern = "Unknown";
    public string RecurrencePattern
    {
        get => _recurrencePattern;
        set
        {
            if (_recurrencePattern != value)
            {
                _recurrencePattern = value;
                OnPropertyChanged();
            }
        }
    }

    // Display properties
    public string StatusText => IsActive ? "Active" : "Paused";

    public string NextRecordingDisplay
    {
        get
        {
            if (NextRecordingTime == null)
                return "No upcoming episodes";

            var localTime = NextRecordingTime.Value.ToLocalTime();
            var now = DateTime.Now;

            // Show relative time for near-future recordings
            if (localTime.Date == now.Date)
                return $"Today at {localTime:h:mm tt}";
            else if (localTime.Date == now.Date.AddDays(1))
                return $"Tomorrow at {localTime:h:mm tt}";
            else if ((localTime - now).TotalDays < 7)
                return $"{localTime:ddd h:mm tt}";
            else
                return $"{localTime:MMM d, h:mm tt}";
        }
    }

    /// <summary>
    /// Determines if a program title matches this series recording rule.
    /// </summary>
    public bool IsMatch(string programTitle)
    {
        if (string.IsNullOrWhiteSpace(programTitle))
            return false;

        return MatchMode switch
        {
            SeriesMatchMode.TitleContains => programTitle.Contains(SeriesName, StringComparison.OrdinalIgnoreCase),
            SeriesMatchMode.TitleStartsWith => programTitle.StartsWith(SeriesName, StringComparison.OrdinalIgnoreCase),
            SeriesMatchMode.TitleExact => programTitle.Equals(SeriesName, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>
    /// Checks if this episode has already been recorded.
    /// </summary>
    public bool IsAlreadyRecorded(string episodeTitle)
    {
        return RecordedEpisodeTitles.Contains(episodeTitle);
    }

    /// <summary>
    /// Marks an episode as recorded to avoid duplicate recordings.
    /// </summary>
    public void MarkAsRecorded(string episodeTitle)
    {
        RecordedEpisodeTitles.Add(episodeTitle);
        LastRecordedUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(LastRecordedUtc));
    }
}

public enum SeriesMatchMode
{
    TitleContains,      // Matches if program title contains series name
    TitleStartsWith,    // Matches if program title starts with series name
    TitleExact          // Matches only if program title exactly equals series name
}
