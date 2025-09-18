using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopApp.Views;

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

        // Check for scheduled recordings every 30 seconds for better accuracy
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

            // For EPG-based recordings, allow past start times if the show is still airing
            var now = DateTime.UtcNow;
            if (!recording.IsEpgBased && recording.StartTime <= now.AddMinutes(-recording.PreBufferMinutes))
                throw new ArgumentException("Cannot schedule recording in the past");

            // For EPG-based recordings, check if the show has already ended
            if (recording.IsEpgBased && recording.EndTime <= now)
                throw new ArgumentException("Cannot record a show that has already ended");

            // Generate output file path if not set
            if (string.IsNullOrEmpty(recording.OutputFilePath))
            {
                recording.OutputFilePath = GenerateOutputFilePath(recording);
            }

            _scheduledRecordings.Add(recording);
            SaveScheduledRecordings();

            Log($"Scheduled recording: {recording.Title} on {recording.ChannelName} from {recording.StartTimeLocal} to {recording.EndTimeLocal}");

            // If this recording should start immediately (within 1 minute), start it now
            var timeDiff = recording.StartTime.AddMinutes(-recording.PreBufferMinutes) - now;
            if (timeDiff.TotalMinutes <= 1)
            {
                Log($"Recording scheduled to start immediately: {recording.Title}");
                Task.Run(() =>
                {
                    StartRecording(recording);
                    SaveScheduledRecordings(); // Save status after starting
                });
            }
        }
    }

    public void CancelRecording(Guid recordingId)
    {
        lock (_lockObject)
        {
            var recording = _scheduledRecordings.FirstOrDefault(r => r.Id == recordingId);
            if (recording != null && recording.CanCancel)
            {
                // If currently recording, stop the recording process
                if (recording.Status == RecordingScheduleStatus.Recording)
                {
                    StopRecording(recording);
                }

                // Update status on UI thread to ensure proper binding updates
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    recording.Status = RecordingScheduleStatus.Cancelled;
                });

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
                // Update on UI thread to ensure proper binding updates
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var index = _scheduledRecordings.IndexOf(existingRecording);
                    _scheduledRecordings[index] = updatedRecording;
                });

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

            if (toRemove.Any())
            {
                // Remove items on UI thread to ensure proper binding updates
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var recording in toRemove)
                    {
                        _scheduledRecordings.Remove(recording);
                    }
                });

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
                        Log($"Starting recording: {recording.Title} (scheduled: {recording.StartTimeLocal}, now: {DateTime.Now:MM/dd/yyyy hh:mm:ss tt})");
                        StartRecording(recording);
                    }

                    // Check if recording should stop
                    if (recording.ShouldStopNow())
                    {
                        Log($"Stopping recording: {recording.Title} (scheduled end: {recording.EndTimeLocal}, now: {DateTime.Now:MM/dd/yyyy hh:mm:ss tt})");
                        StopRecording(recording);
                    }

                    // Check if recording has exceeded its end time (cleanup for missed stops)
                    if (recording.Status == RecordingScheduleStatus.Recording &&
                        DateTime.UtcNow > recording.EndTime.AddMinutes(recording.PostBufferMinutes + 5))
                    {
                        Log($"Force stopping overdue recording: {recording.Title}");
                        recording.Status = RecordingScheduleStatus.Completed;

                        // Clean up the recording process
                        if (recording.RecordingProcess != null && !recording.RecordingProcess.HasExited)
                        {
                            try
                            {
                                recording.RecordingProcess.Kill();
                                recording.RecordingProcess.Dispose();
                                recording.RecordingProcess = null;
                            }
                            catch (Exception ex)
                            {
                                Log($"Error force stopping process for {recording.Title}: {ex.Message}");
                            }
                        }

                        // Reset channel indicator and UI
                        RecordingManager.Instance.StopRecording();
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            // Find the dashboard window and reset button text
                            foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
                            {
                                if (window is DashboardWindow dashboard)
                                {
                                    if (dashboard.FindName("RecordBtnText") is System.Windows.Controls.TextBlock btnText)
                                        btnText.Text = "Record";
                                    break;
                                }
                            }
                        });
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

            // Start the actual FFmpeg recording process
            Task.Run(() =>
            {
                try
                {
                    StartFfmpegRecording(recording);
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

    private void StartFfmpegRecording(ScheduledRecording recording)
    {
        // Validate FFmpeg path
        if (string.IsNullOrWhiteSpace(Session.FfmpegPath) || !System.IO.File.Exists(Session.FfmpegPath))
        {
            throw new InvalidOperationException("FFmpeg path not set or file not found. Please configure FFmpeg path in Settings.");
        }

        // Ensure output directory exists
        var outputDir = System.IO.Path.GetDirectoryName(recording.OutputFilePath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            System.IO.Directory.CreateDirectory(outputDir);
        }

        // Build FFmpeg process
        var psi = Session.BuildFfmpegRecordProcess(recording.StreamUrl, recording.Title, recording.OutputFilePath);
        if (psi == null)
        {
            throw new InvalidOperationException("Unable to build FFmpeg process configuration.");
        }

        // Start FFmpeg process
        var process = new System.Diagnostics.Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Log($"FFMPEG [{recording.Title}]: {e.Data}");
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Log($"FFMPEG [{recording.Title}]: {e.Data}");
        };

        process.Exited += (s, e) =>
        {
            Log($"FFmpeg process exited for recording: {recording.Title}");
        };

        if (process.Start())
        {
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch { }

            // Update RecordingManager only for channel indicator (separate from status)
            RecordingManager.Instance.StartRecording(recording.StreamUrl, recording.OutputFilePath, recording.Title, recording.ChannelId);

            // Store process reference for stopping later
            recording.RecordingProcess = process;

            // Update UI to show scheduled recording is active
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
                {
                    // Find the dashboard window and update button text
                    foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
                    {
                        if (window is DashboardWindow dashboard)
                        {
                            if (dashboard.FindName("RecordBtnText") is System.Windows.Controls.TextBlock btnText)
                                btnText.Text = "Scheduled";
                            break;
                        }
                    }
                }
            });

            Log($"Started FFmpeg recording process for: {recording.Title} -> {recording.OutputFilePath}");
        }
        else
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start FFmpeg process for recording: {recording.Title}");
        }
    }

    private void StopRecording(ScheduledRecording recording)
    {
        try
        {
            Log($"Stopping scheduled recording: {recording.Title}");

            // Stop the FFmpeg process if it exists
            if (recording.RecordingProcess != null && !recording.RecordingProcess.HasExited)
            {
                try
                {
                    recording.RecordingProcess.CloseMainWindow();
                    if (!recording.RecordingProcess.WaitForExit(5000)) // Wait 5 seconds
                    {
                        recording.RecordingProcess.Kill(); // Force kill if it doesn't stop gracefully
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error stopping FFmpeg process for {recording.Title}: {ex.Message}");
                }
                finally
                {
                    recording.RecordingProcess?.Dispose();
                    recording.RecordingProcess = null;
                }
            }

            // Update RecordingManager only for channel indicator (separate from status)
            RecordingManager.Instance.StopRecording();

            // Update UI to show recording is no longer active
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // Find the dashboard window and reset button text
                foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
                {
                    if (window is DashboardWindow dashboard)
                    {
                        if (dashboard.FindName("RecordBtnText") is System.Windows.Controls.TextBlock btnText)
                            btnText.Text = "Record";
                        break;
                    }
                }
            });

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
        if (string.IsNullOrWhiteSpace(fileName)) return "Unknown";

        // First remove emoji and LIVE NOW indicators
        var cleaned = fileName.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");

        // Remove other emoji characters (basic cleanup)
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[\uD800-\uDBFF\uDC00-\uDFFF]", "");

        // Remove invalid file name characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var result = string.Join("_", cleaned.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

        // Trim and ensure we have something
        result = result.Trim('_', ' ');
        return string.IsNullOrWhiteSpace(result) ? "Recording" : result;
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