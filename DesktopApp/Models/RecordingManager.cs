using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace DesktopApp.Models;

public enum RecordingState
{
    Idle,
    Recording,
    Stopped
}

public sealed class RecordingManager : INotifyPropertyChanged
{
    public static RecordingManager Instance { get; } = new();
    private readonly DispatcherTimer _timer;

    private RecordingManager()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (IsRecording)
                Refresh();
            // Always raise duration even if file size unchanged so UI counts up
            OnPropertyChanged(nameof(DurationDisplay));
        };
        _timer.Start();
    }

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (value != _isRecording)
            {
                _isRecording = value;
                State = value ? RecordingState.Recording : RecordingState.Stopped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartedDisplay));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    private RecordingState _state = RecordingState.Idle;
    public RecordingState State
    {
        get => _state;
        private set
        {
            if (value != _state)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    private string? _channelName;
    public string? ChannelName { get => _channelName; private set { if (value != _channelName) { _channelName = value; OnPropertyChanged(); } } }

    private int? _recordingChannelId;
    public int? RecordingChannelId { get => _recordingChannelId; private set { if (value != _recordingChannelId) { _recordingChannelId = value; OnPropertyChanged(); } } }

    private string? _filePath;
    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (value != _filePath)
            {
                _filePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FileName));
            }
        }
    }

    public string? FileName => string.IsNullOrWhiteSpace(FilePath) ? null : Path.GetFileName(FilePath);

    private DateTime? _startUtc;
    public DateTime? StartUtc { get => _startUtc; private set { if (value != _startUtc) { _startUtc = value; OnPropertyChanged(); OnPropertyChanged(nameof(StartLocal)); OnPropertyChanged(nameof(StartedDisplay)); } } }
    public DateTime? StartLocal => StartUtc?.ToLocalTime();

    private long _sizeBytes;
    public long SizeBytes { get => _sizeBytes; private set { if (value != _sizeBytes) { _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeDisplay)); OnPropertyChanged(nameof(BitrateDisplay)); } } }

    public string SizeDisplay => !IsRecording ? "-" : FormatSize(SizeBytes);
    public string DurationDisplay
    {
        get
        {
            if (!IsRecording || StartUtc == null) return "-";
            var dur = DateTime.UtcNow - StartUtc.Value;
            if (dur.TotalHours >= 1) return dur.ToString(@"hh\:mm\:ss");
            return dur.ToString(@"mm\:ss");
        }
    }
    public string StartedDisplay => StartLocal.HasValue ? StartLocal.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-";
    public string BitrateDisplay
    {
        get
        {
            if (!IsRecording || StartUtc == null) return "-";
            var seconds = (DateTime.UtcNow - StartUtc.Value).TotalSeconds;
            if (seconds <= 0) return "-";
            var bps = SizeBytes * 8.0 / seconds; // bits per second
            if (bps >= 1_000_000) return (bps / 1_000_000d).ToString("0.0") + " Mbps";
            if (bps >= 1_000) return (bps / 1_000d).ToString("0.0") + " Kbps";
            return bps.ToString("0") + " bps";
        }
    }

    public string StatusDisplay => State switch
    {
        RecordingState.Recording => "Recording",
        RecordingState.Stopped => "Stopped",
        _ => "Idle"
    };

    public void Start(string filePath, string? channel, int? channelId = null)
    {
        FilePath = filePath;
        ChannelName = channel;
        RecordingChannelId = channelId;
        StartUtc = DateTime.UtcNow;
        SizeBytes = 0;
        State = RecordingState.Recording;
        IsRecording = true;
        OnPropertyChanged(nameof(DurationDisplay));
    }

    public void Stop()
    {
        IsRecording = false;
        State = RecordingState.Stopped;
        RecordingChannelId = null;
        OnPropertyChanged(nameof(DurationDisplay));
    }

    public void Reset()
    {
        IsRecording = false;
        State = RecordingState.Idle;
        FilePath = null; ChannelName = null; RecordingChannelId = null; StartUtc = null; SizeBytes = 0;
        OnPropertyChanged(nameof(DurationDisplay));
    }

    // Methods used by RecordingScheduler
    public void StartRecording(string streamUrl, string outputPath, string? title, int? channelId = null)
    {
        Start(outputPath, title, channelId);
    }

    public void StopRecording()
    {
        Stop();
    }

    public void Refresh()
    {
        if (!IsRecording || string.IsNullOrWhiteSpace(FilePath)) return;
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists) SizeBytes = fi.Length;
        }
        catch { }
    }

    private static string FormatSize(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return b.ToString(i == 0 ? "0" : "0.0") + " " + units[i];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
