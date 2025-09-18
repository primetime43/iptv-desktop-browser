using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using DesktopApp.Models;
using Microsoft.Win32;

namespace DesktopApp.Views;

public partial class RecordingSchedulerWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly RecordingScheduler _scheduler = RecordingScheduler.Instance;
    private Channel? _selectedChannel;
    private EpgEntry? _selectedProgram;

    public RecordingSchedulerWindow()
    {
        InitializeComponent();
        DataContext = this;

        System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Initializing window, Owner: {Owner?.GetType().Name}");
        InitializeWindow();
        LoadChannels();
        LoadScheduledRecordings();
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] OnSourceInitialized, Owner: {Owner?.GetType().Name}");
        // Reload channels now that the window is fully initialized
        LoadChannels();
    }

    private void InitializeWindow()
    {
        if (StartDatePicker != null)
            StartDatePicker.SelectedDate = DateTime.Today;
        if (StartTimeBox != null)
            StartTimeBox.Text = "20:00";
        if (EndTimeBox != null)
            EndTimeBox.Text = "21:00";
        UpdateOutputFilePath();
    }

    private void LoadChannels()
    {
        // Get channels from the dashboard owner if available
        if (Owner is DashboardWindow dashboard)
        {
            var channels = dashboard.Channels.OrderBy(c => c.Name).ToList();
            System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Loading {channels.Count} channels from dashboard");
            if (ChannelCombo != null)
                ChannelCombo.ItemsSource = channels;
            if (CustomChannelCombo != null)
                CustomChannelCombo.ItemsSource = channels;
        }
        else
        {
            // Fallback to playlist channels
            var playlistChannels = Session.PlaylistChannels.OrderBy(c => c.Name).ToList();
            System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Loading {playlistChannels.Count} playlist channels as fallback");
            // Convert PlaylistEntry to Channel for compatibility
            var channels = playlistChannels.Select(p => new Channel
            {
                Id = p.Id,
                Name = p.Name,
                Logo = p.Logo,
                EpgChannelId = p.TvgId
            }).ToList();

            if (ChannelCombo != null)
                ChannelCombo.ItemsSource = channels;
            if (CustomChannelCombo != null)
                CustomChannelCombo.ItemsSource = channels;
        }
    }

    private void LoadScheduledRecordings()
    {
        ScheduledGrid.ItemsSource = _scheduler.ScheduledRecordings;
    }

    private void RecordingType_Changed(object sender, RoutedEventArgs e)
    {
        if (EpgPanel == null || CustomPanel == null) return;

        if (EpgRadio?.IsChecked == true)
        {
            EpgPanel.IsEnabled = true;
            CustomPanel.IsEnabled = false;
        }
        else
        {
            EpgPanel.IsEnabled = false;
            CustomPanel.IsEnabled = true;
        }

        UpdateOutputFilePath();
    }

    private void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is Channel channel)
        {
            _selectedChannel = channel;
            LoadProgramsForChannel(channel);
            UpdateOutputFilePath();
        }
    }

    private async void LoadProgramsForChannel(Channel channel)
    {
        var now = DateTime.UtcNow;
        var programs = new List<EpgEntry>();

        if (Session.Mode == SessionMode.M3u)
        {
            // For M3U mode, use cached EPG data
            if (!string.IsNullOrWhiteSpace(channel.EpgChannelId) &&
                Session.M3uEpgByChannel.TryGetValue(channel.EpgChannelId, out var entries))
            {
                programs = entries
                    .Where(epg => epg.EndUtc > now) // Include currently airing shows (end time must be in future)
                    .Select(epg =>
                    {
                        var isCurrentlyAiring = epg.StartUtc <= now && epg.EndUtc > now;
                        var displayTitle = isCurrentlyAiring
                            ? $"🔴 {epg.Title} (LIVE NOW)"
                            : epg.Title;

                        return new EpgEntry
                        {
                            StartUtc = epg.StartUtc,
                            EndUtc = epg.EndUtc,
                            Title = displayTitle,
                            Description = epg.Description
                        };
                    })
                    .OrderBy(epg => epg.StartUtc)
                    .Take(50) // Limit to next 50 programs
                    .ToList();
            }
        }
        else if (Session.Mode == SessionMode.Xtream)
        {
            // For Xtream mode, fetch EPG data via API
            try
            {
                using var http = new System.Net.Http.HttpClient();
                var url = Session.BuildApi("get_simple_data_table") + "&stream_id=" + channel.Id;

                System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Fetching EPG for channel {channel.Name}: {url}");

                using var resp = await http.GetAsync(url);
                var json = await resp.Content.ReadAsStringAsync();

                if (!string.IsNullOrWhiteSpace(json))
                {
                    var trimmed = json.AsSpan().TrimStart();
                    if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("epg_listings", out var listings) &&
                            listings.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var el in listings.EnumerateArray())
                            {
                                var start = GetUnixTimestamp(el, "start_timestamp");
                                var end = GetUnixTimestamp(el, "stop_timestamp");

                                if (start == DateTime.MinValue || end == DateTime.MinValue) continue;
                                if (end <= now) continue; // Skip programs that have already ended (but include currently airing)

                                var title = GetStringValue(el, "title", "name", "programme", "program");
                                var desc = GetStringValue(el, "description", "desc", "info", "plot", "short_description");

                                if (!string.IsNullOrWhiteSpace(title))
                                {
                                    var isCurrentlyAiring = start <= now && end > now;
                                    var displayTitle = isCurrentlyAiring
                                        ? $"🔴 {DecodeMaybeBase64(title)} (LIVE NOW)"
                                        : DecodeMaybeBase64(title);

                                    programs.Add(new EpgEntry
                                    {
                                        StartUtc = start,
                                        EndUtc = end,
                                        Title = displayTitle,
                                        Description = DecodeMaybeBase64(desc ?? "")
                                    });
                                }
                            }

                            programs = programs
                                .OrderBy(epg => epg.StartUtc)
                                .Take(50)
                                .ToList();
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Loaded {programs.Count} programs for channel {channel.Name}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RecordingScheduler] Error loading EPG for channel {channel.Name}: {ex.Message}");
            }
        }

        ProgramCombo.ItemsSource = programs;

        if (programs.Any())
        {
            ProgramCombo.SelectedIndex = 0;
        }
    }

    private void ProgramCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProgramCombo.SelectedItem is EpgEntry program)
        {
            _selectedProgram = program;
            if (TitleBox != null)
            {
                // Remove the LIVE NOW indicator for the title box
                var cleanTitle = program.Title.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");
                TitleBox.Text = cleanTitle;
            }
            if (ProgramTimeText != null)
                ProgramTimeText.Text = program.TimeRangeLocal;
            UpdateOutputFilePath();
        }
    }

    private void CustomChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOutputFilePath();
    }

    private void CustomTime_Changed(object sender, EventArgs e)
    {
        UpdateOutputFilePath();
    }

    private void UpdateOutputFilePath()
    {
        if (OutputFileBox == null) return;

        if (EpgRadio?.IsChecked == true && _selectedChannel != null && _selectedProgram != null)
        {
            var sanitizedTitle = SanitizeFileName(_selectedProgram.Title);
            var sanitizedChannel = SanitizeFileName(_selectedChannel.Name);
            var timestamp = _selectedProgram.StartUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm");
            var fileName = $"{sanitizedChannel}_{sanitizedTitle}_{timestamp}.ts";
            var recordingDir = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            OutputFileBox.Text = Path.Combine(recordingDir, fileName);
        }
        else if (CustomRadio?.IsChecked == true && CustomChannelCombo?.SelectedItem is Channel customChannel)
        {
            var title = string.IsNullOrWhiteSpace(TitleBox?.Text) ? "Custom_Recording" : TitleBox.Text;
            var sanitizedTitle = SanitizeFileName(title);
            var sanitizedChannel = SanitizeFileName(customChannel.Name);
            var startDate = StartDatePicker?.SelectedDate ?? DateTime.Today;
            var timestamp = startDate.ToString("yyyy-MM-dd_HH-mm");
            var fileName = $"{sanitizedChannel}_{sanitizedTitle}_{timestamp}.ts";
            var recordingDir = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            OutputFileBox.Text = Path.Combine(recordingDir, fileName);
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
    }

    private string GetStreamUrlForChannel(Channel channel)
    {
        if (Session.Mode == SessionMode.M3u)
        {
            // For M3U mode, find the corresponding playlist entry
            var playlistEntry = Session.PlaylistChannels.FirstOrDefault(p => p.Id == channel.Id);
            return playlistEntry?.StreamUrl ?? "";
        }
        else
        {
            // For Xtream mode, build the stream URL
            return Session.BuildStreamUrl(channel.Id, "ts");
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Transport Stream|*.ts|MP4 Video|*.mp4|All Files|*.*",
            DefaultExt = ".ts",
            FileName = Path.GetFileName(OutputFileBox.Text)
        };

        if (!string.IsNullOrEmpty(OutputFileBox.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(OutputFileBox.Text);
        }

        if (dialog.ShowDialog() == true)
        {
            OutputFileBox.Text = dialog.FileName;
        }
    }

    private void ScheduleRecording_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScheduledRecording recording;

            if (EpgRadio.IsChecked == true)
            {
                // EPG-based recording
                if (_selectedChannel == null || _selectedProgram == null)
                {
                    MessageBox.Show("Please select a channel and program.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // For currently airing shows, start recording now instead of at the original start time
                var now = DateTime.UtcNow;
                var isCurrentlyAiring = _selectedProgram.StartUtc <= now && _selectedProgram.EndUtc > now;
                var effectiveStartTime = isCurrentlyAiring ? now : _selectedProgram.StartUtc;

                recording = new ScheduledRecording
                {
                    Title = _selectedProgram.Title.Replace("🔴 ", "").Replace(" (LIVE NOW)", ""),
                    Description = _selectedProgram.Description ?? "",
                    ChannelId = _selectedChannel.Id,
                    ChannelName = _selectedChannel.Name,
                    StreamUrl = GetStreamUrlForChannel(_selectedChannel),
                    StartTime = effectiveStartTime,
                    EndTime = _selectedProgram.EndUtc,
                    IsEpgBased = true,
                    EpgProgramId = _selectedProgram.GetHashCode().ToString()
                };
            }
            else
            {
                // Custom time recording
                if (CustomChannelCombo.SelectedItem is not Channel customChannel)
                {
                    MessageBox.Show("Please select a channel.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!DateTime.TryParse(StartTimeBox.Text, out var startTime) ||
                    !DateTime.TryParse(EndTimeBox.Text, out var endTime))
                {
                    MessageBox.Show("Please enter valid start and end times (HH:mm format).", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var startDate = StartDatePicker.SelectedDate ?? DateTime.Today;
                var startDateTime = startDate.Date.Add(startTime.TimeOfDay);
                var endDateTime = startDate.Date.Add(endTime.TimeOfDay);

                // If end time is before start time, assume it's the next day
                if (endDateTime <= startDateTime)
                {
                    endDateTime = endDateTime.AddDays(1);
                }

                recording = new ScheduledRecording
                {
                    Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Custom Recording" : TitleBox.Text,
                    ChannelId = customChannel.Id,
                    ChannelName = customChannel.Name,
                    StreamUrl = GetStreamUrlForChannel(customChannel),
                    StartTime = startDateTime.ToUniversalTime(),
                    EndTime = endDateTime.ToUniversalTime(),
                    IsEpgBased = false
                };
            }

            // Set buffer times
            if (int.TryParse(PreBufferBox.Text, out var preBuffer))
                recording.PreBufferMinutes = preBuffer;
            if (int.TryParse(PostBufferBox.Text, out var postBuffer))
                recording.PostBufferMinutes = postBuffer;

            // Set output file path
            if (!string.IsNullOrWhiteSpace(OutputFileBox.Text))
                recording.OutputFilePath = OutputFileBox.Text;

            // Check for conflicts
            if (_scheduler.HasConflictingRecording(recording.StartTime, recording.EndTime))
            {
                var result = MessageBox.Show(
                    "This recording conflicts with an existing scheduled recording. Do you want to schedule it anyway?",
                    "Recording Conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // Schedule the recording
            _scheduler.ScheduleRecording(recording);

            MessageBox.Show($"Recording scheduled successfully!\n\nTitle: {recording.Title}\nTime: {recording.TimeRangeText}",
                "Recording Scheduled", MessageBoxButton.OK, MessageBoxImage.Information);

            // Reset form
            ResetForm();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error scheduling recording: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetForm()
    {
        if (TitleBox != null)
            TitleBox.Text = "";
        if (ChannelCombo != null)
            ChannelCombo.SelectedIndex = -1;
        if (ProgramCombo != null)
            ProgramCombo.ItemsSource = null;
        if (ProgramTimeText != null)
            ProgramTimeText.Text = "";
        if (CustomChannelCombo != null)
            CustomChannelCombo.SelectedIndex = -1;
        if (StartDatePicker != null)
            StartDatePicker.SelectedDate = DateTime.Today;
        if (StartTimeBox != null)
            StartTimeBox.Text = "20:00";
        if (EndTimeBox != null)
            EndTimeBox.Text = "21:00";
        if (PreBufferBox != null)
            PreBufferBox.Text = "2";
        if (PostBufferBox != null)
            PostBufferBox.Text = "5";
        if (OutputFileBox != null)
            OutputFileBox.Text = "";

        _selectedChannel = null;
        _selectedProgram = null;
    }

    private void RefreshScheduled_Click(object sender, RoutedEventArgs e)
    {
        LoadScheduledRecordings();
    }

    private void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will delete all completed, failed, and cancelled recordings from the list. Continue?",
            "Delete Completed Recordings", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _scheduler.DeleteCompletedRecordings();
            LoadScheduledRecordings();
        }
    }

    private void PropertiesRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ScheduledRecording recording)
            return;

        var properties = $"Recording Properties\n\n" +
                        $"Title: {recording.Title}\n" +
                        $"Channel: {recording.ChannelName}\n" +
                        $"Status: {recording.StatusText}\n" +
                        $"Start Time: {recording.StartTimeLocal}\n" +
                        $"End Time: {recording.EndTimeLocal}\n" +
                        $"Duration: {recording.DurationText}\n" +
                        $"Pre-buffer: {recording.PreBufferMinutes} minutes\n" +
                        $"Post-buffer: {recording.PostBufferMinutes} minutes\n" +
                        $"EPG-based: {(recording.IsEpgBased ? "Yes" : "No")}\n" +
                        $"Output File: {recording.OutputFilePath}\n" +
                        $"Stream URL: {recording.StreamUrl}\n";

        if (!string.IsNullOrEmpty(recording.Description))
        {
            properties += $"Description: {recording.Description}\n";
        }

        MessageBox.Show(properties, "Recording Properties", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EditRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ScheduledRecording recording)
            return;

        // TODO: Implement edit recording dialog
        MessageBox.Show("Edit recording functionality will be implemented in a future update.",
            "Coming Soon", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CancelRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ScheduledRecording recording)
            return;

        var result = MessageBox.Show($"Cancel recording '{recording.Title}'?", "Cancel Recording",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _scheduler.CancelRecording(recording.Id);
            LoadScheduledRecordings();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Helper methods for parsing EPG JSON data (copied from DashboardWindow)
    private static DateTime GetUnixTimestamp(System.Text.Json.JsonElement el, params string[] props)
    {
        foreach (var prop in props)
        {
            if (el.TryGetProperty(prop, out var val))
            {
                if (val.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(val.GetString(), out var ts))
                    return DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
                if (val.ValueKind == System.Text.Json.JsonValueKind.Number && val.TryGetInt64(out var tsNum))
                    return DateTimeOffset.FromUnixTimeSeconds(tsNum).UtcDateTime;
            }
        }
        return DateTime.MinValue;
    }

    private static string GetStringValue(System.Text.Json.JsonElement el, params string[] props)
    {
        foreach (var prop in props)
        {
            if (el.TryGetProperty(prop, out var val) && val.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var str = val.GetString();
                if (!string.IsNullOrWhiteSpace(str)) return str;
            }
        }
        return "";
    }

    private static string DecodeMaybeBase64(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        try
        {
            var bytes = Convert.FromBase64String(input);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(decoded) ? input : decoded;
        }
        catch
        {
            return input;
        }
    }
}