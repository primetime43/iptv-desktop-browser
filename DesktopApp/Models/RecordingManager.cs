using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace DesktopApp.Models;

public sealed class RecordingManager : INotifyPropertyChanged
{
    public static RecordingManager Instance { get; } = new();
    private RecordingManager() { }

    private bool _isRecording; public bool IsRecording { get => _isRecording; private set { if (value != _isRecording) { _isRecording = value; OnPropertyChanged(); OnPropertyChanged(nameof(StartedDisplay)); } } }
    private string? _channelName; public string? ChannelName { get => _channelName; private set { if (value != _channelName) { _channelName = value; OnPropertyChanged(); } } }
    private string? _filePath; public string? FilePath { get => _filePath; private set { if (value != _filePath) { _filePath = value; OnPropertyChanged(); } } }
    private DateTime? _startUtc; public DateTime? StartUtc { get => _startUtc; private set { if (value != _startUtc) { _startUtc = value; OnPropertyChanged(); OnPropertyChanged(nameof(StartLocal)); OnPropertyChanged(nameof(StartedDisplay)); } } }
    public DateTime? StartLocal => StartUtc?.ToLocalTime();

    private long _sizeBytes; public long SizeBytes { get => _sizeBytes; private set { if (value != _sizeBytes) { _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeDisplay)); OnPropertyChanged(nameof(BitrateDisplay)); } } }

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

    public void Start(string filePath, string? channel)
    {
        FilePath = filePath;
        ChannelName = channel;
        StartUtc = DateTime.UtcNow;
        SizeBytes = 0;
        IsRecording = true;
        OnPropertyChanged(nameof(DurationDisplay));
    }

    public void Stop()
    {
        IsRecording = false;
        FilePath = null; ChannelName = null; StartUtc = null; SizeBytes = 0;
        OnPropertyChanged(nameof(DurationDisplay));
    }

    public void Refresh()
    {
        if (!IsRecording || string.IsNullOrWhiteSpace(FilePath)) return;
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists) SizeBytes = fi.Length;
            OnPropertyChanged(nameof(DurationDisplay));
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
