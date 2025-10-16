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
    public event Action<SeriesRecording>? EpgRefreshNeeded;

    private static RecordingScheduler? _instance;
    public static RecordingScheduler Instance => _instance ??= new RecordingScheduler();

    private readonly ObservableCollection<ScheduledRecording> _scheduledRecordings = new();
    public ObservableCollection<ScheduledRecording> ScheduledRecordings => _scheduledRecordings;

    private readonly ObservableCollection<SeriesRecording> _seriesRecordings = new();
    public ObservableCollection<SeriesRecording> SeriesRecordings => _seriesRecordings;

    private readonly Timer _schedulerTimer;
    private readonly Timer _epgRefreshTimer;
    private readonly string _scheduleFilePath;
    private readonly string _seriesFilePath;
    private readonly object _lockObject = new();

    private RecordingScheduler()
    {
        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IPTV-Desktop-Browser");
        Directory.CreateDirectory(appDataPath);
        _scheduleFilePath = Path.Combine(appDataPath, "scheduled_recordings.json");
        _seriesFilePath = Path.Combine(appDataPath, "series_recordings.json");

        LoadScheduledRecordings();
        LoadSeriesRecordings();

        // Check for scheduled recordings every 30 seconds for better accuracy
        _schedulerTimer = new Timer(CheckScheduledRecordings, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        // Refresh EPG for series recordings every 6 hours
        // Start after 5 minutes to avoid startup load, then repeat every 6 hours
        _epgRefreshTimer = new Timer(RefreshSeriesEpg, null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(6));
        Log("EPG auto-refresh initialized: will check for new episodes every 6 hours");
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
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
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
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
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
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
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
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
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
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
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
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
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
            // Load all scheduled recordings from file (for all accounts)
            var allScheduled = LoadAllScheduledRecordings();

            // Update the current session's scheduled recordings
            var currentSessionKey = GetCurrentSessionKey();
            allScheduled[currentSessionKey] = _scheduledRecordings.ToList();

            // Save all accounts back to file
            var json = JsonSerializer.Serialize(allScheduled, new JsonSerializerOptions
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

    private Dictionary<string, List<ScheduledRecording>> LoadAllScheduledRecordings()
    {
        try
        {
            if (File.Exists(_scheduleFilePath))
            {
                var json = File.ReadAllText(_scheduleFilePath);
                if (string.IsNullOrWhiteSpace(json))
                    return new Dictionary<string, List<ScheduledRecording>>();

                // Check if JSON starts with '[' (array/old format) or '{' (object/new format)
                var trimmed = json.TrimStart();
                if (trimmed.StartsWith("{"))
                {
                    // New per-account format
                    var perAccount = JsonSerializer.Deserialize<Dictionary<string, List<ScheduledRecording>>>(json);
                    if (perAccount != null)
                        return perAccount;
                }
                else if (trimmed.StartsWith("["))
                {
                    // Old format - migrate
                    var oldFormat = JsonSerializer.Deserialize<List<ScheduledRecording>>(json);
                    if (oldFormat != null && oldFormat.Any())
                    {
                        var currentSessionKey = GetCurrentSessionKey();
                        Log($"Migrating {oldFormat.Count} scheduled recordings from old format to new per-account format");

                        // Save in new format immediately
                        var migrated = new Dictionary<string, List<ScheduledRecording>>
                        {
                            [currentSessionKey] = oldFormat
                        };

                        // Save migrated data back to file
                        var newJson = JsonSerializer.Serialize(migrated, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(_scheduleFilePath, newJson);

                        return migrated;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading all scheduled recordings: {ex.Message}");
        }

        return new Dictionary<string, List<ScheduledRecording>>();
    }

    private void LoadScheduledRecordings()
    {
        try
        {
            var allScheduled = LoadAllScheduledRecordings();
            var currentSessionKey = GetCurrentSessionKey();

            _scheduledRecordings.Clear();

            if (allScheduled.TryGetValue(currentSessionKey, out var sessionRecordings))
            {
                foreach (var recording in sessionRecordings)
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

                Log($"Loaded {_scheduledRecordings.Count} scheduled recordings for current session");
            }
            else
            {
                Log($"No scheduled recordings found for current session");
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

    // ===================== Series Recording Management =====================

    public void AddSeriesRecording(SeriesRecording seriesRecording)
    {
        lock (_lockObject)
        {
            _seriesRecordings.Add(seriesRecording);
            SaveSeriesRecordings();
            Log($"Added series recording: {seriesRecording.SeriesName} on {seriesRecording.ChannelName}");
        }
    }

    public void RemoveSeriesRecording(Guid seriesId)
    {
        lock (_lockObject)
        {
            var series = _seriesRecordings.FirstOrDefault(s => s.Id == seriesId);
            if (series != null)
            {
                // Cancel any future scheduled recordings associated with this series
                var relatedRecordings = _scheduledRecordings
                    .Where(r => r.SeriesRecordingId == seriesId &&
                               r.Status == RecordingScheduleStatus.Scheduled)
                    .ToList();

                foreach (var recording in relatedRecordings)
                {
                    CancelRecording(recording.Id);
                }

                _seriesRecordings.Remove(series);
                SaveSeriesRecordings();
                Log($"Removed series recording: {series.SeriesName}");
            }
        }
    }

    public void UpdateSeriesRecording(SeriesRecording updatedSeries)
    {
        lock (_lockObject)
        {
            var existing = _seriesRecordings.FirstOrDefault(s => s.Id == updatedSeries.Id);
            if (existing != null)
            {
                var index = _seriesRecordings.IndexOf(existing);
                _seriesRecordings[index] = updatedSeries;
                SaveSeriesRecordings();
                Log($"Updated series recording: {updatedSeries.SeriesName}");
            }
        }
    }

    /// <summary>
    /// Updates next recording information for all series recordings.
    /// Should be called after scheduled recordings are modified or loaded.
    /// </summary>
    public void UpdateAllSeriesRecordingInfo()
    {
        lock (_lockObject)
        {
            foreach (var series in _seriesRecordings)
            {
                UpdateSeriesRecordingInfo(series);
            }
        }
    }

    /// <summary>
    /// Checks EPG data for new episodes matching series recording rules and schedules them.
    /// Uses time-based matching first (same time slot), then falls back to title matching.
    /// </summary>
    public void CheckForNewEpisodes(int channelId, List<EpgEntry> epgEntries)
    {
        lock (_lockObject)
        {
            var seriesForChannel = _seriesRecordings
                .Where(s => s.IsActive && s.ChannelId == channelId)
                .ToList();

            if (!seriesForChannel.Any())
                return;

            var now = DateTime.UtcNow;
            foreach (var series in seriesForChannel)
            {
                Log($"Checking EPG for '{series.SeriesName}' ({epgEntries.Count} programs)");

                // Get existing recordings for time-based pattern detection
                var existingRecordings = _scheduledRecordings
                    .Where(r => r.SeriesRecordingId == series.Id && r.StartTime <= now)
                    .OrderByDescending(r => r.StartTime)
                    .Take(3)
                    .ToList();

                // Detect time pattern from existing recordings
                TimeSpan? timeOfDay = null;
                if (existingRecordings.Any())
                {
                    timeOfDay = existingRecordings.First().StartTime.TimeOfDay;
                    Log($"  Using time pattern: {timeOfDay.Value:hh\\:mm} from {existingRecordings.Count} past recording(s)");
                }
                else
                {
                    Log($"  No past recordings, using title matching only");
                }

                // Normalize series name for better matching
                var normalizedSeriesName = NormalizeTitle(series.SeriesName);

                int futureCount = 0;
                int matchCount = 0;

                foreach (var epg in epgEntries)
                {
                    // Check if episode is in the future
                    if (epg.StartUtc <= now)
                        continue;

                    futureCount++;

                    // Check if we've already recorded this episode
                    if (series.IsAlreadyRecorded(epg.Title))
                        continue;

                    // Check if we already have a recording scheduled for this exact program
                    if (_scheduledRecordings.Any(r =>
                        r.SeriesRecordingId == series.Id &&
                        r.StartTime == epg.StartUtc &&
                        r.Title == epg.Title))
                        continue;

                    bool isMatch = false;
                    string matchMethod = "";

                    // Method 1: Time-based matching (preferred for recurring shows)
                    if (timeOfDay.HasValue)
                    {
                        var epgTimeOfDay = epg.StartUtc.TimeOfDay;
                        var timeDiff = Math.Abs((epgTimeOfDay - timeOfDay.Value).TotalMinutes);

                        // If within 15 minutes of the usual time slot, consider it a match
                        if (timeDiff <= 15)
                        {
                            isMatch = true;
                            matchMethod = $"time-based ({timeDiff:F0}min diff)";
                        }
                    }

                    // Method 2: Title-based matching (fallback or no time pattern yet)
                    if (!isMatch)
                    {
                        var normalizedEpgTitle = NormalizeTitle(epg.Title);

                        // Try normalized title matching
                        bool titleMatch = series.MatchMode switch
                        {
                            SeriesMatchMode.TitleContains => normalizedEpgTitle.Contains(normalizedSeriesName, StringComparison.OrdinalIgnoreCase),
                            SeriesMatchMode.TitleStartsWith => normalizedEpgTitle.StartsWith(normalizedSeriesName, StringComparison.OrdinalIgnoreCase),
                            SeriesMatchMode.TitleExact => normalizedEpgTitle.Equals(normalizedSeriesName, StringComparison.OrdinalIgnoreCase),
                            _ => false
                        };

                        if (titleMatch)
                        {
                            isMatch = true;
                            matchMethod = $"title ({series.MatchMode})";
                        }
                    }

                    if (!isMatch)
                        continue;

                    // Check for "only new episodes" logic
                    if (series.OnlyNewEpisodes && IsLikelyRerun(epg.Title, epg.Description))
                        continue;

                    // Create a new scheduled recording for this episode
                    try
                    {
                        var recording = new ScheduledRecording
                        {
                            Title = epg.Title,
                            Description = epg.Description ?? string.Empty,
                            ChannelId = series.ChannelId,
                            ChannelName = series.ChannelName,
                            StreamUrl = series.StreamUrl,
                            StartTime = epg.StartUtc,
                            EndTime = epg.EndUtc,
                            IsEpgBased = true,
                            IsSeriesRecording = true,
                            SeriesRecordingId = series.Id,
                            PreBufferMinutes = series.PreBufferMinutes,
                            PostBufferMinutes = series.PostBufferMinutes
                        };

                        ScheduleRecording(recording);
                        series.MarkAsRecorded(epg.Title);
                        SaveSeriesRecordings(); // Save updated recorded episodes list

                        matchCount++;
                        Log($"  ✓ Scheduled ({matchMethod}): {epg.Title} at {epg.StartUtc.ToLocalTime():MMM d h:mm tt}");
                    }
                    catch (Exception ex)
                    {
                        Log($"  Error scheduling: {ex.Message}");
                    }
                }

                Log($"  Result: {matchCount} episodes scheduled from {futureCount} future programs");

                // If no matches found, show sample programs
                if (matchCount == 0 && futureCount > 0)
                {
                    Log($"  No matches found. Sample EPG programs:");
                    var samplePrograms = epgEntries.Where(e => e.StartUtc > now).Take(5).ToList();
                    foreach (var prog in samplePrograms)
                    {
                        Log($"    - '{prog.Title}' at {prog.StartUtc.ToLocalTime():ddd h:mm tt}");
                    }
                }

                series.LastCheckedUtc = DateTime.UtcNow;

                // Update next recording info and frequency pattern
                UpdateSeriesRecordingInfo(series);
            }

            SaveSeriesRecordings();
        }
    }

    /// <summary>
    /// Normalizes a title by removing special characters, superscripts, and extra metadata.
    /// </summary>
    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        // Remove common metadata markers
        var normalized = title;

        // Remove superscript/subscript characters (like ᴺᵉʷ, ᴴᴰ, etc.)
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[\u1D00-\u1DBF\u2070-\u209F]", "");

        // Remove ***, [NEW], (NEW), emojis, etc.
        normalized = normalized.Replace("***", "");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[\[\(](NEW|HD|4K|LIVE)[\]\)]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+(NEW|HD|4K|LIVE)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove emoji and special unicode characters
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[\uD800-\uDBFF\uDC00-\uDFFF]", "");

        // Normalize whitespace
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ").Trim();

        return normalized;
    }

    /// <summary>
    /// Updates the next recording time and frequency pattern for a series recording.
    /// </summary>
    private void UpdateSeriesRecordingInfo(SeriesRecording series)
    {
        try
        {
            // Find the next scheduled recording for this series
            var nextRecording = _scheduledRecordings
                .Where(r => r.SeriesRecordingId == series.Id && r.StartTime > DateTime.UtcNow)
                .OrderBy(r => r.StartTime)
                .FirstOrDefault();

            if (nextRecording != null)
            {
                series.NextRecordingTime = nextRecording.StartTime;
                series.NextRecordingTitle = nextRecording.Title;
            }
            else
            {
                series.NextRecordingTime = null;
                series.NextRecordingTitle = null;
            }

            // Calculate frequency pattern based on scheduled recordings
            var upcomingRecordings = _scheduledRecordings
                .Where(r => r.SeriesRecordingId == series.Id && r.StartTime > DateTime.UtcNow)
                .OrderBy(r => r.StartTime)
                .Take(5)
                .ToList();

            if (upcomingRecordings.Count >= 2)
            {
                // Calculate average interval between episodes
                var intervals = new List<TimeSpan>();
                for (int i = 1; i < upcomingRecordings.Count; i++)
                {
                    intervals.Add(upcomingRecordings[i].StartTime - upcomingRecordings[i - 1].StartTime);
                }

                var avgInterval = TimeSpan.FromTicks((long)intervals.Average(ts => ts.Ticks));

                // Determine frequency pattern
                if (avgInterval.TotalHours < 25) // Daily (accounting for slight variations)
                    series.RecurrencePattern = "Daily";
                else if (avgInterval.TotalDays < 8) // Weekly
                    series.RecurrencePattern = "Weekly";
                else if (avgInterval.TotalDays < 32) // Monthly
                    series.RecurrencePattern = "Monthly";
                else
                    series.RecurrencePattern = $"Every {(int)avgInterval.TotalDays} days";
            }
            else if (upcomingRecordings.Count == 1)
            {
                series.RecurrencePattern = "One episode";
            }
            else
            {
                series.RecurrencePattern = "No episodes";
            }
        }
        catch (Exception ex)
        {
            Log($"Error updating series recording info for {series.SeriesName}: {ex.Message}");
            series.RecurrencePattern = "Unknown";
        }
    }

    /// <summary>
    /// Simple heuristic to detect reruns. Can be enhanced based on EPG data.
    /// </summary>
    private static bool IsLikelyRerun(string title, string? description)
    {
        // Check title for rerun indicators
        if (title.Contains("repeat", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("rerun", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("encore", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check description for rerun indicators
        if (!string.IsNullOrWhiteSpace(description))
        {
            if (description.Contains("repeat", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("rerun", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("previously aired", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a unique session key for the current account/profile.
    /// This ensures series recordings are isolated per account.
    /// </summary>
    private static string GetCurrentSessionKey()
    {
        if (Session.Mode == SessionMode.M3u)
        {
            return $"m3u_{Environment.UserName}";
        }
        else
        {
            return $"{Environment.UserName}_{Session.Host}_{Session.Port}_{Session.Username}";
        }
    }

    /// <summary>
    /// Reloads both series recordings and scheduled recordings for the current session.
    /// Call this when switching accounts/profiles to ensure correct data is displayed.
    /// </summary>
    public void ReloadForCurrentSession()
    {
        lock (_lockObject)
        {
            LoadScheduledRecordings();
            LoadSeriesRecordings();
            Log($"Reloaded recordings for current session");
        }
    }

    private void SaveSeriesRecordings()
    {
        try
        {
            // Load all series recordings from file (for all accounts)
            var allSeries = LoadAllSeriesRecordings();

            // Update the current session's series recordings
            var currentSessionKey = GetCurrentSessionKey();
            allSeries[currentSessionKey] = _seriesRecordings.ToList();

            // Save all accounts back to file
            var json = JsonSerializer.Serialize(allSeries, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_seriesFilePath, json);
        }
        catch (Exception ex)
        {
            Log($"Error saving series recordings: {ex.Message}");
        }
    }

    private Dictionary<string, List<SeriesRecording>> LoadAllSeriesRecordings()
    {
        try
        {
            if (File.Exists(_seriesFilePath))
            {
                var json = File.ReadAllText(_seriesFilePath);
                if (string.IsNullOrWhiteSpace(json))
                    return new Dictionary<string, List<SeriesRecording>>();

                // Check if JSON starts with '[' (array/old format) or '{' (object/new format)
                var trimmed = json.TrimStart();
                if (trimmed.StartsWith("{"))
                {
                    // New per-account format
                    var perAccount = JsonSerializer.Deserialize<Dictionary<string, List<SeriesRecording>>>(json);
                    if (perAccount != null)
                        return perAccount;
                }
                else if (trimmed.StartsWith("["))
                {
                    // Old format - migrate
                    var oldFormat = JsonSerializer.Deserialize<List<SeriesRecording>>(json);
                    if (oldFormat != null && oldFormat.Any())
                    {
                        var currentSessionKey = GetCurrentSessionKey();
                        Log($"Migrating {oldFormat.Count} series recordings from old format to new per-account format");

                        // Save in new format immediately
                        var migrated = new Dictionary<string, List<SeriesRecording>>
                        {
                            [currentSessionKey] = oldFormat
                        };

                        // Save migrated data back to file
                        var newJson = JsonSerializer.Serialize(migrated, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(_seriesFilePath, newJson);

                        return migrated;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading all series recordings: {ex.Message}");
        }

        return new Dictionary<string, List<SeriesRecording>>();
    }

    private void LoadSeriesRecordings()
    {
        try
        {
            var allSeries = LoadAllSeriesRecordings();
            var currentSessionKey = GetCurrentSessionKey();

            _seriesRecordings.Clear();

            if (allSeries.TryGetValue(currentSessionKey, out var sessionSeries))
            {
                foreach (var series in sessionSeries)
                {
                    _seriesRecordings.Add(series);
                }

                Log($"Loaded {_seriesRecordings.Count} series recordings for current session");

                // Update next recording info for all loaded series
                UpdateAllSeriesRecordingInfo();
            }
            else
            {
                Log($"No series recordings found for current session");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading series recordings: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggers EPG refresh for all active series recordings.
    /// This method is called periodically by the timer to find new episodes.
    /// </summary>
    private void RefreshSeriesEpg(object? state)
    {
        try
        {
            lock (_lockObject)
            {
                var activeSeries = _seriesRecordings.Where(s => s.IsActive).ToList();

                if (!activeSeries.Any())
                {
                    Log("EPG refresh: No active series recordings");
                    return;
                }

                Log($"EPG refresh: Checking {activeSeries.Count} active series recording(s) for new episodes");

                foreach (var series in activeSeries)
                {
                    // Fire event to request EPG data from DashboardWindow
                    EpgRefreshNeeded?.Invoke(series);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error during EPG refresh: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        // Log to both debug output and UI
        System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] {DateTime.Now:HH:mm:ss} {message}");

        // Also log to the UI Logs tab
        try
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
                {
                    if (window is DashboardWindow dashboard)
                    {
                        // Use reflection to call the Log method since it's private
                        var logMethod = dashboard.GetType().GetMethod("Log",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        logMethod?.Invoke(dashboard, new object[] { message });
                        break;
                    }
                }
            });
        }
        catch
        {
            // Silently fail if UI logging doesn't work
        }
    }

    public void Dispose()
    {
        _schedulerTimer?.Dispose();
        _epgRefreshTimer?.Dispose();
    }
}