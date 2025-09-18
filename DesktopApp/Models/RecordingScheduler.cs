using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopApp.Models;

public class RecordingScheduler : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ScheduledRecording>? RecordingStarted;
    public event Action<ScheduledRecording>? RecordingStopped;
    public event Action<ScheduledRecording>? RecordingFailed;

    private static RecordingScheduler? _instance;
    public static RecordingScheduler Instance => _instance ??= new RecordingScheduler();

    private readonly ObservableCollection<ScheduledRecording> _scheduledRecordings = new();
    public ObservableCollection<ScheduledRecording> ScheduledRecordings => _scheduledRecordings;

    private readonly Timer _schedulerTimer;
    private readonly string _scheduleFilePath;
    private readonly object _lockObject = new();

    private RecordingScheduler()
    {
        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IPTV-Desktop-Browser");
        Directory.CreateDirectory(appDataPath);
        _scheduleFilePath = Path.Combine(appDataPath, "scheduled_recordings.json");

        LoadScheduledRecordings();

        // Check for scheduled recordings every 30 seconds
        _schedulerTimer = new Timer(CheckScheduledRecordings, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void ScheduleRecording(ScheduledRecording recording)
    {
        lock (_lockObject)
        {
            // Validate the recording
            if (recording.StartTime >= recording.EndTime)
                throw new ArgumentException("Start time must be before end time");

            if (recording.StartTime <= DateTime.UtcNow.AddMinutes(-recording.PreBufferMinutes))
                throw new ArgumentException("Cannot schedule recording in the past");

            // Generate output file path if not set
            if (string.IsNullOrEmpty(recording.OutputFilePath))
            {
                recording.OutputFilePath = GenerateOutputFilePath(recording);
            }

            _scheduledRecordings.Add(recording);
            SaveScheduledRecordings();

            Log($"Scheduled recording: {recording.Title} on {recording.ChannelName} from {recording.StartTimeLocal} to {recording.EndTimeLocal}");
        }
    }

    public void CancelRecording(Guid recordingId)
    {
        lock (_lockObject)
        {
            var recording = _scheduledRecordings.FirstOrDefault(r => r.Id == recordingId);
            if (recording != null && recording.CanCancel)
            {
                recording.Status = RecordingScheduleStatus.Cancelled;
                SaveScheduledRecordings();
                Log($"Cancelled recording: {recording.Title}");
            }
        }
    }

    public void UpdateRecording(ScheduledRecording updatedRecording)
    {
        lock (_lockObject)
        {
            var existingRecording = _scheduledRecordings.FirstOrDefault(r => r.Id == updatedRecording.Id);
            if (existingRecording != null && existingRecording.CanEdit)
            {
                var index = _scheduledRecordings.IndexOf(existingRecording);
                _scheduledRecordings[index] = updatedRecording;
                SaveScheduledRecordings();
                Log($"Updated recording: {updatedRecording.Title}");
            }
        }
    }

    public void DeleteCompletedRecordings()
    {
        lock (_lockObject)
        {
            var toRemove = _scheduledRecordings
                .Where(r => r.Status == RecordingScheduleStatus.Completed ||
                           r.Status == RecordingScheduleStatus.Failed ||
                           r.Status == RecordingScheduleStatus.Cancelled)
                .ToList();

            foreach (var recording in toRemove)
            {
                _scheduledRecordings.Remove(recording);
            }

            if (toRemove.Any())
            {
                SaveScheduledRecordings();
                Log($"Deleted {toRemove.Count} completed recordings");
            }
        }
    }

    private void CheckScheduledRecordings(object? state)
    {
        try
        {
            lock (_lockObject)
            {
                var now = DateTime.UtcNow;

                foreach (var recording in _scheduledRecordings.ToList())
                {
                    // Check for missed recordings
                    if (recording.IsMissed())
                    {
                        recording.Status = RecordingScheduleStatus.Missed;
                        Log($"Missed recording: {recording.Title}");
                        continue;
                    }

                    // Check if recording should start
                    if (recording.ShouldStartNow())
                    {
                        StartRecording(recording);
                    }

                    // Check if recording should stop
                    if (recording.ShouldStopNow())
                    {
                        StopRecording(recording);
                    }
                }

                SaveScheduledRecordings();
            }
        }
        catch (Exception ex)
        {
            Log($"Error in recording scheduler: {ex.Message}");
        }
    }

    private void StartRecording(ScheduledRecording recording)
    {
        try
        {
            recording.Status = RecordingScheduleStatus.Recording;
            Log($"Starting scheduled recording: {recording.Title}");

            // Start the actual recording using the existing recording system
            Task.Run(() =>
            {
                try
                {
                    RecordingManager.Instance.StartRecording(
                        recording.StreamUrl,
                        recording.OutputFilePath,
                        recording.Title
                    );

                    RecordingStarted?.Invoke(recording);
                }
                catch (Exception ex)
                {
                    recording.Status = RecordingScheduleStatus.Failed;
                    Log($"Failed to start recording {recording.Title}: {ex.Message}");
                    RecordingFailed?.Invoke(recording);
                }
            });
        }
        catch (Exception ex)
        {
            recording.Status = RecordingScheduleStatus.Failed;
            Log($"Error starting recording {recording.Title}: {ex.Message}");
            RecordingFailed?.Invoke(recording);
        }
    }

    private void StopRecording(ScheduledRecording recording)
    {
        try
        {
            Log($"Stopping scheduled recording: {recording.Title}");

            // Stop the recording using the existing recording system
            RecordingManager.Instance.StopRecording();

            recording.Status = RecordingScheduleStatus.Completed;
            RecordingStopped?.Invoke(recording);

            Log($"Completed recording: {recording.Title} -> {recording.OutputFilePath}");
        }
        catch (Exception ex)
        {
            recording.Status = RecordingScheduleStatus.Failed;
            Log($"Error stopping recording {recording.Title}: {ex.Message}");
            RecordingFailed?.Invoke(recording);
        }
    }

    private string GenerateOutputFilePath(ScheduledRecording recording)
    {
        var recordingDir = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var sanitizedTitle = SanitizeFileName(recording.Title);
        var sanitizedChannel = SanitizeFileName(recording.ChannelName);
        var timestamp = recording.StartTime.ToLocalTime().ToString("yyyy-MM-dd_HH-mm");

        var fileName = $"{sanitizedChannel}_{sanitizedTitle}_{timestamp}.ts";
        return Path.Combine(recordingDir, fileName);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
    }

    private void SaveScheduledRecordings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_scheduledRecordings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_scheduleFilePath, json);
        }
        catch (Exception ex)
        {
            Log($"Error saving scheduled recordings: {ex.Message}");
        }
    }

    private void LoadScheduledRecordings()
    {
        try
        {
            if (File.Exists(_scheduleFilePath))
            {
                var json = File.ReadAllText(_scheduleFilePath);
                var recordings = JsonSerializer.Deserialize<List<ScheduledRecording>>(json);

                if (recordings != null)
                {
                    _scheduledRecordings.Clear();
                    foreach (var recording in recordings)
                    {
                        // Skip recordings that are already completed or too old
                        if (recording.Status == RecordingScheduleStatus.Completed ||
                            recording.Status == RecordingScheduleStatus.Failed ||
                            recording.Status == RecordingScheduleStatus.Cancelled ||
                            recording.EndTime < DateTime.UtcNow.AddDays(-7))
                        {
                            continue;
                        }

                        _scheduledRecordings.Add(recording);
                    }

                    Log($"Loaded {_scheduledRecordings.Count} scheduled recordings");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading scheduled recordings: {ex.Message}");
        }
    }

    public List<ScheduledRecording> GetUpcomingRecordings(int hours = 24)
    {
        var cutoff = DateTime.UtcNow.AddHours(hours);
        return _scheduledRecordings
            .Where(r => r.Status == RecordingScheduleStatus.Scheduled && r.StartTime <= cutoff)
            .OrderBy(r => r.StartTime)
            .ToList();
    }

    public bool HasConflictingRecording(DateTime startTime, DateTime endTime, Guid? excludeId = null)
    {
        return _scheduledRecordings.Any(r =>
            r.Id != excludeId &&
            r.Status == RecordingScheduleStatus.Scheduled &&
            ((startTime >= r.StartTime && startTime < r.EndTime) ||
             (endTime > r.StartTime && endTime <= r.EndTime) ||
             (startTime <= r.StartTime && endTime >= r.EndTime))
        );
    }

    private static void Log(string message)
    {
        // Use the existing logging system from the main window
        // This will be integrated with the main application's logging
        System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] {DateTime.Now:HH:mm:ss} {message}");
    }

    public void Dispose()
    {
        _schedulerTimer?.Dispose();
    }
}