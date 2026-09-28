using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DesktopApp.Models;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Input;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using Microsoft.Win32;

namespace DesktopApp.Views;

public partial class DashboardWindow
{
        private void InitializeScheduler()
        {
            InitializeSchedulerWindow();
            LoadSchedulerChannels();
            LoadScheduledRecordings();
            LoadSeriesRecordings();
        }

        private void InitializeSchedulerWindow()
        {
            if (FindDashboardElement("StartDatePicker") is DatePicker startDatePicker)
                startDatePicker.SelectedDate = DateTime.Today;
            if (FindDashboardElement("StartTimeBox") is TextBox startTimeBox)
                startTimeBox.Text = "20:00";
            if (FindDashboardElement("EndTimeBox") is TextBox endTimeBox)
                endTimeBox.Text = "21:00";
            UpdateOutputFilePath();
        }

        private void LoadSchedulerChannels()
        {
            var channels = Channels.OrderBy(c => c.Name).ToList();
            SchedulerPageModel.Channels.ReplaceAll(channels);
            if (FindDashboardElement("ChannelCombo") is ComboBox channelCombo)
                channelCombo.ItemsSource = SchedulerPageModel.Channels;
            if (FindDashboardElement("CustomChannelCombo") is ComboBox customChannelCombo)
                customChannelCombo.ItemsSource = SchedulerPageModel.Channels;
        }

        private void LoadScheduledRecordings()
        {
            if (FindDashboardElement("ScheduledGrid") is DataGrid scheduledGrid)
                scheduledGrid.ItemsSource = _scheduler.ScheduledRecordings;
        }

        // ===================== Series Recording UI Methods =====================

        private void LoadSeriesRecordings()
        {
            if (FindDashboardElement("SeriesGrid") is DataGrid seriesGrid)
                seriesGrid.ItemsSource = _scheduler.SeriesRecordings;
        }

        /// <summary>
        /// Immediately loads EPG for a channel and checks for new episodes.
        /// Called right after adding a series recording to schedule the first episodes.
        /// </summary>
        private async Task LoadEpgAndCheckForEpisodesAsync(SeriesRecording series, Channel channel)
        {
            try
            {
                Log($"Loading EPG for series: {series.SeriesName} on {channel.Name}\n");

                var epgEntries = new List<EpgEntry>();

                if (Session.Mode == SessionMode.Xtream)
                {
                    // Make fresh API call to get ALL EPG data
                    var url = Session.BuildApi("get_simple_data_table") + "&stream_id=" + channel.Id;
                    Log($"Calling EPG API: {url}\n");

                    using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    var json = await resp.Content.ReadAsStringAsync(_cts.Token);

                    var trimmed = json.AsSpan().TrimStart();
                    if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("epg_listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in listings.EnumerateArray())
                            {
                                string titleRaw = TryGetString(el, "title", "name", "programme", "program");
                                string descRaw = TryGetString(el, "description", "desc");

                                if (!string.IsNullOrWhiteSpace(titleRaw))
                                {
                                    var title = DecodeMaybeBase64(titleRaw);
                                    var description = !string.IsNullOrWhiteSpace(descRaw) ? DecodeMaybeBase64(descRaw) : "";

                                    var startStr = TryGetString(el, "start", "start_timestamp");
                                    var endStr = TryGetString(el, "stop", "end", "end_timestamp");

                                    DateTime startUtc, endUtc;

                                    // Try parsing as Unix timestamp first (long)
                                    if (long.TryParse(startStr, out var startUnix) && long.TryParse(endStr, out var endUnix))
                                    {
                                        startUtc = DateTimeOffset.FromUnixTimeSeconds(startUnix).UtcDateTime;
                                        endUtc = DateTimeOffset.FromUnixTimeSeconds(endUnix).UtcDateTime;
                                    }
                                    // Try parsing as datetime string (e.g., "2025-10-07 08:00:00")
                                    else if (DateTime.TryParse(startStr, out var startDt) && DateTime.TryParse(endStr, out var endDt))
                                    {
                                        startUtc = DateTime.SpecifyKind(startDt, DateTimeKind.Utc);
                                        endUtc = DateTime.SpecifyKind(endDt, DateTimeKind.Utc);
                                    }
                                    else
                                    {
                                        continue; // Skip entries with invalid dates
                                    }

                                    epgEntries.Add(new EpgEntry
                                    {
                                        Title = title,
                                        Description = description,
                                        StartUtc = startUtc,
                                        EndUtc = endUtc
                                    });
                                }
                            }
                        }
                    }
                }
                else if (Session.Mode == SessionMode.M3u)
                {
                    var playlist = Session.PlaylistChannels.FirstOrDefault(p => p.Id == channel.Id);
                    if (playlist != null && !string.IsNullOrWhiteSpace(playlist.TvgId))
                    {
                        if (Session.M3uEpgByChannel.TryGetValue(playlist.TvgId, out var epgList))
                        {
                            epgEntries.AddRange(epgList);
                        }
                    }
                }

                Log($"Loaded {epgEntries.Count} EPG entries for {channel.Name}\n");

                // Check for episodes and schedule them
                if (epgEntries.Any())
                {
                    _scheduler.CheckForNewEpisodes(channel.Id, epgEntries);
                }
                else
                {
                    Log($"No EPG data available for {channel.Name}\n");
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading EPG for series: {ex.Message}\n");
            }
        }

        internal async void AddSeriesRecording_Click(object sender, RoutedEventArgs e)
        {
            // Create a simple dialog for adding series recording
            var dialog = new Window
            {
                Title = "Add Series Recording",
                Width = 550,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0D1117"))
            };

            var panel = new StackPanel { Margin = new Thickness(20) };

            // Channel Selection (moved to top)
            panel.Children.Add(new TextBlock { Text = "1. Select Channel:", Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 5), FontWeight = FontWeights.SemiBold });
            var channelCombo = new ComboBox
            {
                ItemsSource = _channels,
                DisplayMemberPath = "Name",
                SelectedValuePath = "Id",
                Margin = new Thickness(0, 0, 0, 15)
            };
            panel.Children.Add(channelCombo);

            // Series/Program Selection
            panel.Children.Add(new TextBlock { Text = "2. Select Show from EPG:", Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 5), FontWeight = FontWeights.SemiBold });
            var loadingText = new TextBlock
            {
                Text = "Select a channel to load available shows...",
                Foreground = System.Windows.Media.Brushes.Gray,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 5)
            };
            panel.Children.Add(loadingText);

            var seriesCombo = new ComboBox
            {
                IsEnabled = false,
                IsEditable = true,
                Margin = new Thickness(0, 0, 0, 5)
            };
            panel.Children.Add(seriesCombo);

            var nextAiringText = new TextBlock
            {
                Text = "",
                Foreground = System.Windows.Media.Brushes.LightBlue,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5),
                Visibility = Visibility.Collapsed
            };
            panel.Children.Add(nextAiringText);

            var helpText = new TextBlock
            {
                Text = "Type to filter or select from the dropdown. This list shows all unique shows from the EPG guide.",
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 15)
            };
            panel.Children.Add(helpText);

            // Store EPG data for finding next airing
            List<EpgEntry> channelEpgData = new List<EpgEntry>();

            // Load programs when channel is selected
            channelCombo.SelectionChanged += async (s, args) =>
            {
                if (channelCombo.SelectedItem is Channel selectedChannel)
                {
                    loadingText.Text = "Loading programs from EPG...";
                    seriesCombo.IsEnabled = false;
                    seriesCombo.ItemsSource = null;
                    nextAiringText.Visibility = Visibility.Collapsed;

                    try
                    {
                        // Load EPG entries (not just titles)
                        channelEpgData = await LoadEpgEntriesForChannel(selectedChannel);

                        // Extract unique show names
                        var programs = channelEpgData
                            .Select(e => StripEpisodeMetadata(e.Title))
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(t => t)
                            .ToList();

                        if (programs.Any())
                        {
                            seriesCombo.ItemsSource = programs;
                            seriesCombo.IsEnabled = true;
                            loadingText.Text = $"Found {programs.Count} unique show(s) in EPG. Select a show to see next airing.";
                            loadingText.Foreground = System.Windows.Media.Brushes.LightGreen;
                        }
                        else
                        {
                            loadingText.Text = "No programs found in EPG for this channel. You can still type a show name manually.";
                            loadingText.Foreground = System.Windows.Media.Brushes.Orange;
                            seriesCombo.IsEnabled = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        loadingText.Text = $"Error loading programs: {ex.Message}";
                        loadingText.Foreground = System.Windows.Media.Brushes.Red;
                        seriesCombo.IsEnabled = true;
                    }
                }
            };

            // Show next airing when show is selected
            seriesCombo.SelectionChanged += (s, args) =>
            {
                if (!string.IsNullOrWhiteSpace(seriesCombo.Text) && channelEpgData.Any())
                {
                    var selectedShow = seriesCombo.Text.Trim();
                    var now = DateTime.UtcNow;

                    // Find next airing of this show
                    var nextEpisode = channelEpgData
                        .Where(e => e.StartUtc > now)
                        .Where(e => StripEpisodeMetadata(e.Title).Equals(selectedShow, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.StartUtc)
                        .FirstOrDefault();

                    if (nextEpisode != null)
                    {
                        var localTime = nextEpisode.StartUtc.ToLocalTime();
                        var timeUntil = nextEpisode.StartUtc - now;

                        string timeDisplay;
                        if (timeUntil.TotalHours < 1)
                            timeDisplay = $"in {(int)timeUntil.TotalMinutes} minutes";
                        else if (timeUntil.TotalHours < 24)
                            timeDisplay = $"today at {localTime:h:mm tt}";
                        else if (timeUntil.TotalDays < 2)
                            timeDisplay = $"tomorrow at {localTime:h:mm tt}";
                        else if (timeUntil.TotalDays < 7)
                            timeDisplay = $"{localTime:dddd} at {localTime:h:mm tt}";
                        else
                            timeDisplay = $"{localTime:MMM d} at {localTime:h:mm tt}";

                        nextAiringText.Text = $"📅 Next airing: {timeDisplay}";
                        nextAiringText.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        nextAiringText.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    nextAiringText.Visibility = Visibility.Collapsed;
                }
            };

            // Match Mode
            panel.Children.Add(new TextBlock { Text = "Match Mode:", Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 5) });
            var matchModeCombo = new ComboBox
            {
                ItemsSource = Enum.GetValues(typeof(SeriesMatchMode)),
                SelectedIndex = 0,
                Margin = new Thickness(0, 0, 0, 15)
            };
            panel.Children.Add(matchModeCombo);

            // Only New Episodes
            var onlyNewCheckBox = new CheckBox
            {
                Content = "Only record new episodes (skip reruns)",
                IsChecked = true,
                Foreground = System.Windows.Media.Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 15)
            };
            panel.Children.Add(onlyNewCheckBox);

            // Buffer times
            panel.Children.Add(new TextBlock { Text = "Pre-buffer (minutes):", Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 5) });
            var preBufferBox = new TextBox { Text = "2", Margin = new Thickness(0, 0, 0, 15) };
            panel.Children.Add(preBufferBox);

            panel.Children.Add(new TextBlock { Text = "Post-buffer (minutes):", Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 5) });
            var postBufferBox = new TextBox { Text = "5", Margin = new Thickness(0, 0, 0, 15) };
            panel.Children.Add(postBufferBox);

            // Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var viewScheduleButton = new Button { Content = "View Schedule", Width = 120, Margin = new Thickness(0, 0, 10, 0) };
            var saveButton = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 10, 0) };
            var cancelButton = new Button { Content = "Cancel", Width = 80 };

            saveButton.Click += (s, args) =>
            {
                if (string.IsNullOrWhiteSpace(seriesCombo.Text))
                {
                    MessageBox.Show("Please select or enter a series name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (channelCombo.SelectedItem == null)
                {
                    MessageBox.Show("Please select a channel.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var channel = (Channel)channelCombo.SelectedItem;
                var streamUrl = Session.Mode == SessionMode.Xtream
                    ? Session.BuildStreamUrl(channel.Id)
                    : Session.PlaylistChannels.FirstOrDefault(p => p.Id == channel.Id)?.StreamUrl ?? "";

                var seriesRecording = new SeriesRecording
                {
                    SeriesName = seriesCombo.Text.Trim(),
                    ChannelId = channel.Id,
                    ChannelName = channel.Name,
                    StreamUrl = streamUrl,
                    MatchMode = (SeriesMatchMode)matchModeCombo.SelectedItem,
                    OnlyNewEpisodes = onlyNewCheckBox.IsChecked == true,
                    PreBufferMinutes = int.TryParse(preBufferBox.Text, out var pre) ? pre : 2,
                    PostBufferMinutes = int.TryParse(postBufferBox.Text, out var post) ? post : 5
                };

                _scheduler.AddSeriesRecording(seriesRecording);
                Log($"Added series recording: {seriesRecording.SeriesName} on {seriesRecording.ChannelName}\n");

                dialog.DialogResult = true;
                dialog.Close();

                // Immediately load EPG and check for episodes
                _ = LoadEpgAndCheckForEpisodesAsync(seriesRecording, channel);
            };

            viewScheduleButton.Click += async (s, args) =>
            {
                if (string.IsNullOrWhiteSpace(seriesCombo.Text) || channelCombo.SelectedItem == null || !channelEpgData.Any())
                {
                    MessageBox.Show("Please select a channel and show first.", "No Schedule", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var selectedShow = seriesCombo.Text.Trim();
                var channel = (Channel)channelCombo.SelectedItem;
                ShowScheduleWindow(selectedShow, channel, channelEpgData);
            };

            cancelButton.Click += (s, args) =>
            {
                dialog.DialogResult = false;
                dialog.Close();
            };

            buttonPanel.Children.Add(viewScheduleButton);
            buttonPanel.Children.Add(saveButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            dialog.Content = panel;
            dialog.ShowDialog();
        }

        private void ShowScheduleWindow(string showName, Channel channel, List<EpgEntry> epgData)
        {
            var now = DateTime.UtcNow;
            var futureEpisodes = epgData
                .Where(e => e.StartUtc > now)
                .Where(e => StripEpisodeMetadata(e.Title).Equals(showName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.StartUtc)
                .ToList();

            var scheduleWindow = new Window
            {
                Title = $"Schedule: {showName} on {channel.Name}",
                Width = 700,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = System.Windows.Media.Brushes.DarkGray
            };

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(10)
            };

            var panel = new StackPanel();

            if (!futureEpisodes.Any())
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No upcoming episodes found in EPG data.",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 14,
                    Margin = new Thickness(0, 10, 0, 10)
                });
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Found {futureEpisodes.Count} upcoming episode(s):",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 15)
                });

                foreach (var episode in futureEpisodes)
                {
                    var episodePanel = new Border
                    {
                        Background = System.Windows.Media.Brushes.White,
                        Padding = new Thickness(10),
                        Margin = new Thickness(0, 0, 0, 10),
                        CornerRadius = new CornerRadius(5)
                    };

                    var episodeStack = new StackPanel();

                    var localStart = episode.StartUtc.ToLocalTime();
                    var localEnd = episode.EndUtc.ToLocalTime();
                    var duration = episode.EndUtc - episode.StartUtc;

                    episodeStack.Children.Add(new TextBlock
                    {
                        Text = episode.Title,
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = System.Windows.Media.Brushes.Black,
                        TextWrapping = TextWrapping.Wrap
                    });

                    var dateText = $"📅 {localStart:dddd, MMMM d, yyyy}";
                    var timeText = $"🕐 {localStart:h:mm tt} - {localEnd:h:mm tt} ({(int)duration.TotalMinutes} min)";

                    episodeStack.Children.Add(new TextBlock
                    {
                        Text = dateText,
                        FontSize = 12,
                        Foreground = System.Windows.Media.Brushes.DarkBlue,
                        Margin = new Thickness(0, 5, 0, 0)
                    });

                    episodeStack.Children.Add(new TextBlock
                    {
                        Text = timeText,
                        FontSize = 12,
                        Foreground = System.Windows.Media.Brushes.DarkGreen,
                        Margin = new Thickness(0, 2, 0, 0)
                    });

                    if (!string.IsNullOrWhiteSpace(episode.Description))
                    {
                        episodeStack.Children.Add(new TextBlock
                        {
                            Text = episode.Description,
                            FontSize = 11,
                            Foreground = System.Windows.Media.Brushes.Gray,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 5, 0, 0)
                        });
                    }

                    episodePanel.Child = episodeStack;
                    panel.Children.Add(episodePanel);
                }
            }

            scrollViewer.Content = panel;
            scheduleWindow.Content = scrollViewer;
            scheduleWindow.ShowDialog();
        }

        internal async void ViewSeriesSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is SeriesRecording series)
            {
                try
                {
                    // Load EPG data for this series' channel
                    var channel = new Channel
                    {
                        Id = series.ChannelId,
                        Name = series.ChannelName
                    };

                    Log($"Loading schedule for '{series.SeriesName}' on {series.ChannelName}...\n");
                    var epgData = await LoadEpgEntriesForChannel(channel);

                    if (!epgData.Any())
                    {
                        MessageBox.Show($"No EPG data available for {series.ChannelName}.", "No Schedule",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    ShowScheduleWindow(series.SeriesName, channel, epgData);
                }
                catch (Exception ex)
                {
                    Log($"Error loading schedule: {ex.Message}\n");
                    MessageBox.Show($"Error loading schedule: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Event handler called when the RecordingScheduler timer fires to refresh EPG data.
        /// This ensures series recordings continue to find new episodes automatically.
        /// </summary>
        private async void OnEpgRefreshNeeded(SeriesRecording series)
        {
            try
            {
                Log($"[Auto-refresh] Loading EPG for '{series.SeriesName}' on {series.ChannelName}...\n");

                var channel = new Channel
                {
                    Id = series.ChannelId,
                    Name = series.ChannelName
                };

                var epgData = await LoadEpgEntriesForChannel(channel);

                if (epgData.Any())
                {
                    _scheduler.CheckForNewEpisodes(series.ChannelId, epgData);
                    Log($"[Auto-refresh] Checked for new episodes of '{series.SeriesName}'\n");
                }
                else
                {
                    Log($"[Auto-refresh] No EPG data available for {series.ChannelName}\n");
                }
            }
            catch (Exception ex)
            {
                Log($"[Auto-refresh] Error for '{series.SeriesName}': {ex.Message}\n");
            }
        }

        internal void EditSeriesRecording_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is SeriesRecording series)
            {
                MessageBox.Show($"Edit functionality for '{series.SeriesName}' coming soon!", "Series Recording", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        internal void DeleteSeriesRecording_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is SeriesRecording series)
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to delete the series recording for '{series.SeriesName}'?\n\nThis will also cancel any upcoming scheduled recordings for this series.",
                    "Delete Series Recording",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _scheduler.RemoveSeriesRecording(series.Id);
                    LoadSeriesRecordings();
                    Log($"Deleted series recording: {series.SeriesName}\n");
                }
            }
        }

        /// <summary>
        /// Checks if a program title is marked as a new episode.
        /// </summary>
        private static bool IsNewEpisode(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            // Check for common NEW indicators:
            // - *** at the end (e.g., "Show Name ***")
            // - [NEW], (NEW)
            // - Standalone NEW tag
            return title.TrimEnd().EndsWith("***") ||
                   System.Text.RegularExpressions.Regex.IsMatch(title, @"[\[\(]NEW[\]\)]|\sNEW\s|\sNEW$|^NEW\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Strips episode tags and metadata from a show title to get the base series name.
        /// </summary>
        private static string StripEpisodeMetadata(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            // Remove *** NEW indicator
            title = title.Replace("***", "").Trim();

            // Remove emojis and LIVE indicators
            title = title.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");

            // Remove common tags in brackets or parentheses
            // Matches: [NEW], (NEW), [REPEAT], (HD), etc.
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s*[\[\(](NEW|REPEAT|RERUN|ENCORE|HD|4K|CC|DVS)[\]\)]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Remove episode numbers like S01E05, 1x05, Ep. 5, Episode 5
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+S\d+E\d+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+\d+x\d+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+Ep\.?\s*\d+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+Episode\s+\d+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Remove standalone tags at the end like "NEW", "REPEAT", etc.
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+(NEW|REPEAT|RERUN|ENCORE|HD|4K|CC)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Remove dates in various formats (YYYY-MM-DD, MM/DD/YY, etc.)
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+\d{4}-\d{2}-\d{2}", "");
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+\d{1,2}/\d{1,2}/\d{2,4}", "");

            // Remove extra whitespace
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+", " ");

            return title.Trim();
        }

        /// <summary>
        /// Loads unique program titles from EPG data for the specified channel.
        /// Strips episode metadata to show unique series names.
        /// </summary>
        private async Task<List<EpgEntry>> LoadEpgEntriesForChannel(Channel channel)
        {
            var epgEntries = new List<EpgEntry>();

            try
            {
                if (Session.Mode == SessionMode.Xtream)
                {
                    // Load EPG from Xtream API
                    var url = Session.BuildApi("get_simple_data_table") + "&stream_id=" + channel.Id;
                    Log($"[EPG] Fetching from: {url}\n");

                    using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    var json = await resp.Content.ReadAsStringAsync(_cts.Token);

                    Log($"[EPG] Response length: {json.Length} chars\n");
                    if (json.Length < 500)
                    {
                        Log($"[EPG] Full response: {json}\n");
                    }
                    else
                    {
                        Log($"[EPG] Response starts with: {json.Substring(0, Math.Min(300, json.Length))}...\n");
                    }

                    var trimmed = json.AsSpan().TrimStart();
                    if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                    {
                        using var doc = JsonDocument.Parse(json);
                        Log($"[EPG] JSON parsed. Looking for 'epg_listings' property...\n");

                        if (doc.RootElement.TryGetProperty("epg_listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                        {
                            var listingCount = listings.GetArrayLength();
                            Log($"[EPG] Found 'epg_listings' array with {listingCount} entries\n");

                            foreach (var el in listings.EnumerateArray())
                            {
                                string titleRaw = TryGetString(el, "title", "name", "programme", "program");
                                string descRaw = TryGetString(el, "description", "desc");

                                if (!string.IsNullOrWhiteSpace(titleRaw))
                                {
                                    var title = DecodeMaybeBase64(titleRaw);
                                    var description = !string.IsNullOrWhiteSpace(descRaw) ? DecodeMaybeBase64(descRaw) : "";

                                    var startStr = TryGetString(el, "start", "start_timestamp");
                                    var endStr = TryGetString(el, "stop", "end", "end_timestamp");

                                    DateTime startUtc, endUtc;

                                    // Try parsing as Unix timestamp first (long)
                                    if (long.TryParse(startStr, out var startUnix) && long.TryParse(endStr, out var endUnix))
                                    {
                                        startUtc = DateTimeOffset.FromUnixTimeSeconds(startUnix).UtcDateTime;
                                        endUtc = DateTimeOffset.FromUnixTimeSeconds(endUnix).UtcDateTime;
                                    }
                                    // Try parsing as datetime string (e.g., "2025-10-07 08:00:00")
                                    else if (DateTime.TryParse(startStr, out var startDt) && DateTime.TryParse(endStr, out var endDt))
                                    {
                                        startUtc = DateTime.SpecifyKind(startDt, DateTimeKind.Utc);
                                        endUtc = DateTime.SpecifyKind(endDt, DateTimeKind.Utc);
                                    }
                                    else
                                    {
                                        continue; // Skip entries with invalid dates
                                    }

                                    epgEntries.Add(new EpgEntry
                                    {
                                        Title = title,
                                        Description = description,
                                        StartUtc = startUtc,
                                        EndUtc = endUtc
                                    });
                                }
                            }
                        }
                        else
                        {
                            Log($"[EPG] 'epg_listings' property not found or not an array\n");
                        }
                    }
                    else
                    {
                        Log($"[EPG] Response is not valid JSON\n");
                    }

                    var uniqueShows = epgEntries
                        .Select(e => StripEpisodeMetadata(e.Title))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    Log($"[EPG] Channel '{channel.Name}': {epgEntries.Count} total programs, {uniqueShows} unique shows\n");
                }
                else if (Session.Mode == SessionMode.M3u)
                {
                    var playlist = Session.PlaylistChannels.FirstOrDefault(p => p.Id == channel.Id);
                    if (playlist != null && !string.IsNullOrWhiteSpace(playlist.TvgId))
                    {
                        if (Session.M3uEpgByChannel.TryGetValue(playlist.TvgId, out var epgList))
                        {
                            epgEntries.AddRange(epgList);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Series] Error loading EPG for '{channel.Name}': {ex.Message}\n");
            }

            return epgEntries;
        }

        private async Task<List<string>> LoadProgramTitlesForChannel(Channel channel)
        {
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalPrograms = 0;

            try
            {
                if (Session.Mode == SessionMode.Xtream)
                {
                    // Load EPG from Xtream API
                    var url = Session.BuildApi("get_simple_data_table") + "&stream_id=" + channel.Id;
                    using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    var json = await resp.Content.ReadAsStringAsync(_cts.Token);

                    var trimmed = json.AsSpan().TrimStart();
                    if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("epg_listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in listings.EnumerateArray())
                            {
                                string titleRaw = TryGetString(el, "title", "name", "programme", "program");
                                if (!string.IsNullOrWhiteSpace(titleRaw))
                                {
                                    totalPrograms++;
                                    string title = DecodeMaybeBase64(titleRaw);

                                    // Strip episode metadata to get base series name
                                    string cleanTitle = StripEpisodeMetadata(title);
                                    if (!string.IsNullOrWhiteSpace(cleanTitle))
                                    {
                                        titles.Add(cleanTitle);
                                    }
                                }
                            }
                        }
                    }

                    Log($"[Series] Channel '{channel.Name}': {totalPrograms} total programs, {titles.Count} unique shows\n");
                }
                else if (Session.Mode == SessionMode.M3u)
                {
                    // Load EPG from M3U XMLTV data
                    var playlist = Session.PlaylistChannels.FirstOrDefault(p => p.Id == channel.Id);
                    if (playlist != null && !string.IsNullOrWhiteSpace(playlist.TvgId))
                    {
                        if (Session.M3uEpgByChannel.TryGetValue(playlist.TvgId, out var epgList))
                        {
                            foreach (var epg in epgList)
                            {
                                if (!string.IsNullOrWhiteSpace(epg.Title))
                                {
                                    totalPrograms++;
                                    string cleanTitle = StripEpisodeMetadata(epg.Title);
                                    if (!string.IsNullOrWhiteSpace(cleanTitle))
                                    {
                                        titles.Add(cleanTitle);
                                    }
                                }
                            }
                        }
                    }

                    Log($"[Series] Channel '{channel.Name}': {totalPrograms} total programs, {titles.Count} unique shows\n");
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading program titles for channel {channel.Name}: {ex.Message}\n");
            }

            return titles.OrderBy(t => t).ToList();
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

                    Log($"[RecordingScheduler] Fetching EPG for channel {channel.Name}: {url}\n");

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
                    Log($"[RecordingScheduler] Error loading EPG for channel {channel.Name}: {ex.Message}\n");
                }
            }

            if (FindDashboardElement("ProgramCombo") is ComboBox programCombo)
            {
                programCombo.ItemsSource = programs;

                if (programs.Any())
                {
                    programCombo.SelectedIndex = 0;
                }
            }
        }

        private void UpdateOutputFilePath()
        {
            if (FindDashboardElement("OutputFileBox") is not TextBox outputFileBox) return;

            if (FindDashboardElement("EpgRadio") is RadioButton epgRadio && epgRadio.IsChecked == true && _schedulerSelectedChannel != null && _schedulerSelectedProgram != null)
            {
                // Clean the title by removing emoji and LIVE NOW indicator
                var cleanTitle = _schedulerSelectedProgram.Title.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");
                var sanitizedTitle = SanitizeFileName(cleanTitle);
                var sanitizedChannel = SanitizeFileName(_schedulerSelectedChannel.Name);
                var timestamp = _schedulerSelectedProgram.StartUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm");
                var fileName = $"{sanitizedChannel}_{sanitizedTitle}_{timestamp}.ts";
                var recordingDir = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                outputFileBox.Text = Path.Combine(recordingDir, fileName);
            }
            else if (FindDashboardElement("CustomRadio") is RadioButton customRadio && customRadio.IsChecked == true && FindDashboardElement("CustomChannelCombo") is ComboBox customChannelCombo && customChannelCombo.SelectedItem is Channel customChannel)
            {
                var titleBox = FindDashboardElement("TitleBox") as TextBox;
                var title = string.IsNullOrWhiteSpace(titleBox?.Text) ? "Custom_Recording" : titleBox.Text;
                var sanitizedTitle = SanitizeFileName(title);
                var sanitizedChannel = SanitizeFileName(customChannel.Name);
                var startDatePicker = FindDashboardElement("StartDatePicker") as DatePicker;
                var startDate = startDatePicker?.SelectedDate ?? DateTime.Today;
                var timestamp = startDate.ToString("yyyy-MM-dd_HH-mm");
                var fileName = $"{sanitizedChannel}_{sanitizedTitle}_{timestamp}.ts";
                var recordingDir = Session.RecordingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                outputFileBox.Text = Path.Combine(recordingDir, fileName);
            }
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

        // Helper methods for recording scheduler EPG parsing
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

        // Recording Scheduler Properties and Methods
        private readonly RecordingScheduler _scheduler = RecordingScheduler.Instance;
        private Channel? _schedulerSelectedChannel { get => SchedulerPageModel.SelectedChannel; set => SchedulerPageModel.SelectedChannel = value; }
        private EpgEntry? _schedulerSelectedProgram { get => SchedulerPageModel.SelectedProgram; set => SchedulerPageModel.SelectedProgram = value; }

        internal void RecordingType_Changed(object sender, RoutedEventArgs e)
        {
            if (FindDashboardElement("EpgPanel") is Panel epgPanel && FindDashboardElement("CustomPanel") is Panel customPanel)
            {
                if (FindDashboardElement("EpgRadio") is RadioButton epgRadio && epgRadio.IsChecked == true)
                {
                    epgPanel.IsEnabled = true;
                    customPanel.IsEnabled = false;
                }
                else
                {
                    epgPanel.IsEnabled = false;
                    customPanel.IsEnabled = true;
                }

                UpdateOutputFilePath();
            }
        }
        internal void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FindDashboardElement("ChannelCombo") is ComboBox channelCombo && channelCombo.SelectedItem is Channel channel)
            {
                _schedulerSelectedChannel = channel;
                LoadProgramsForChannel(channel);
                UpdateOutputFilePath();
            }
        }
        internal void ProgramCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FindDashboardElement("ProgramCombo") is ComboBox programCombo && programCombo.SelectedItem is EpgEntry program)
            {
                _schedulerSelectedProgram = program;
                if (FindDashboardElement("TitleBox") is TextBox titleBox)
                {
                    // Remove the LIVE NOW indicator for the title box
                    var cleanTitle = program.Title.Replace("🔴 ", "").Replace(" (LIVE NOW)", "");
                    titleBox.Text = cleanTitle;
                }
                if (FindDashboardElement("ProgramTimeText") is TextBlock programTimeText)
                {
                    var startLocal = program.StartUtc.ToLocalTime();
                    var endLocal = program.EndUtc.ToLocalTime();
                    var duration = endLocal - startLocal;
                    programTimeText.Text = $"📅 {startLocal:ddd, MMM dd yyyy}  •  🕐 {program.TimeRangeLocal}  •  ⏱ {duration.Hours}h {duration.Minutes}m";
                }
                UpdateOutputFilePath();
            }
        }
        internal void ProgramCombo_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is ComboBox comboBox && FindDashboardElement("ProgramTimeText") is TextBlock programTimeText)
            {
                var point = e.GetPosition(comboBox);
                var element = comboBox.InputHitTest(point) as DependencyObject;

                // Walk up the visual tree to find the ComboBoxItem
                while (element != null && element is not ComboBoxItem)
                {
                    element = VisualTreeHelper.GetParent(element);
                }

                if (element is ComboBoxItem item && item.Content is EpgEntry program)
                {
                    var startLocal = program.StartUtc.ToLocalTime();
                    var endLocal = program.EndUtc.ToLocalTime();
                    var duration = endLocal - startLocal;
                    programTimeText.Text = $"📅 {startLocal:ddd, MMM dd yyyy}  •  🕐 {program.TimeRangeLocal}  •  ⏱ {duration.Hours}h {duration.Minutes}m";
                }
            }
        }
        internal void ProgramComboItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is ComboBoxItem item && item.Content is EpgEntry program)
            {
                if (FindDashboardElement("ProgramTimeText") is TextBlock programTimeText)
                {
                    var startLocal = program.StartUtc.ToLocalTime();
                    var endLocal = program.EndUtc.ToLocalTime();
                    var duration = endLocal - startLocal;
                    programTimeText.Text = $"📅 {startLocal:ddd, MMM dd yyyy}  •  🕐 {program.TimeRangeLocal}  •  ⏱ {duration.Hours}h {duration.Minutes}m";
                }
            }
        }
        internal void ProgramComboItem_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is ComboBoxItem item && item.Content is EpgEntry program)
            {
                if (FindDashboardElement("ProgramTimeText") is TextBlock programTimeText)
                {
                    var startLocal = program.StartUtc.ToLocalTime();
                    var endLocal = program.EndUtc.ToLocalTime();
                    var duration = endLocal - startLocal;
                    programTimeText.Text = $"📅 {startLocal:ddd, MMM dd yyyy}  •  🕐 {program.TimeRangeLocal}  •  ⏱ {duration.Hours}h {duration.Minutes}m";
                }
            }
        }
        internal void ProgramComboItem_MouseLeave(object sender, MouseEventArgs e)
        {
            if (FindDashboardElement("ProgramTimeText") is TextBlock programTimeText)
            {
                // Restore to selected item's info or default message
                if (_schedulerSelectedProgram != null)
                {
                    var startLocal = _schedulerSelectedProgram.StartUtc.ToLocalTime();
                    var endLocal = _schedulerSelectedProgram.EndUtc.ToLocalTime();
                    var duration = endLocal - startLocal;
                    programTimeText.Text = $"📅 {startLocal:ddd, MMM dd yyyy}  •  🕐 {_schedulerSelectedProgram.TimeRangeLocal}  •  ⏱ {duration.Hours}h {duration.Minutes}m";
                }
                else
                {
                    programTimeText.Text = "Hover over a show to see air time";
                }
            }
        }
        internal void CustomChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateOutputFilePath();
        }
        internal void CustomTime_Changed(object sender, EventArgs e)
        {
            UpdateOutputFilePath();
        }
        internal void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Transport Stream|*.ts|MP4 Video|*.mp4|All Files|*.*",
                DefaultExt = ".ts"
            };

            if (FindDashboardElement("OutputFileBox") is TextBox outputFileBox && !string.IsNullOrEmpty(outputFileBox.Text))
            {
                dialog.FileName = Path.GetFileName(outputFileBox.Text);
                dialog.InitialDirectory = Path.GetDirectoryName(outputFileBox.Text);
            }

            if (dialog.ShowDialog() == true)
            {
                if (FindDashboardElement("OutputFileBox") is TextBox box)
                    box.Text = dialog.FileName;
            }
        }
        internal void ScheduleRecording_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ScheduledRecording recording;

                if (FindDashboardElement("EpgRadio") is RadioButton epgRadio && epgRadio.IsChecked == true)
                {
                    // EPG-based recording
                    if (_schedulerSelectedChannel == null || _schedulerSelectedProgram == null)
                    {
                        MessageBox.Show("Please select a channel and program.", "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // For currently airing shows, start recording now instead of at the original start time
                    var now = DateTime.UtcNow;
                    var isCurrentlyAiring = _schedulerSelectedProgram.StartUtc <= now && _schedulerSelectedProgram.EndUtc > now;
                    var effectiveStartTime = isCurrentlyAiring ? now : _schedulerSelectedProgram.StartUtc;

                    recording = new ScheduledRecording
                    {
                        Title = _schedulerSelectedProgram.Title.Replace("🔴 ", "").Replace(" (LIVE NOW)", ""),
                        Description = _schedulerSelectedProgram.Description ?? "",
                        ChannelId = _schedulerSelectedChannel.Id,
                        ChannelName = _schedulerSelectedChannel.Name,
                        StreamUrl = GetStreamUrlForChannel(_schedulerSelectedChannel),
                        StartTime = effectiveStartTime,
                        EndTime = _schedulerSelectedProgram.EndUtc,
                        IsEpgBased = true,
                        EpgProgramId = _schedulerSelectedProgram.GetHashCode().ToString()
                    };
                }
                else
                {
                    // Custom time recording
                    if (FindDashboardElement("CustomChannelCombo") is not ComboBox customChannelCombo || customChannelCombo.SelectedItem is not Channel customChannel)
                    {
                        MessageBox.Show("Please select a channel.", "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var startTimeBox = FindDashboardElement("StartTimeBox") as TextBox;
                    var endTimeBox = FindDashboardElement("EndTimeBox") as TextBox;

                    if (!DateTime.TryParse(startTimeBox?.Text, out var startTime) ||
                        !DateTime.TryParse(endTimeBox?.Text, out var endTime))
                    {
                        MessageBox.Show("Please enter valid start and end times (HH:mm format).", "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var startDatePicker = FindDashboardElement("StartDatePicker") as DatePicker;
                    var startDate = startDatePicker?.SelectedDate ?? DateTime.Today;
                    var startDateTime = startDate.Date.Add(startTime.TimeOfDay);
                    var endDateTime = startDate.Date.Add(endTime.TimeOfDay);

                    // If end time is before start time, assume it's the next day
                    if (endDateTime <= startDateTime)
                    {
                        endDateTime = endDateTime.AddDays(1);
                    }

                    var titleBox = FindDashboardElement("TitleBox") as TextBox;
                    recording = new ScheduledRecording
                    {
                        Title = string.IsNullOrWhiteSpace(titleBox?.Text) ? "Custom Recording" : titleBox.Text,
                        ChannelId = customChannel.Id,
                        ChannelName = customChannel.Name,
                        StreamUrl = GetStreamUrlForChannel(customChannel),
                        StartTime = startDateTime.ToUniversalTime(),
                        EndTime = endDateTime.ToUniversalTime(),
                        IsEpgBased = false
                    };
                }

                // Set buffer times
                if (FindDashboardElement("PreBufferBox") is TextBox preBufferBox && int.TryParse(preBufferBox.Text, out var preBuffer))
                    recording.PreBufferMinutes = preBuffer;
                if (FindDashboardElement("PostBufferBox") is TextBox postBufferBox && int.TryParse(postBufferBox.Text, out var postBuffer))
                    recording.PostBufferMinutes = postBuffer;

                // Set output file path
                if (FindDashboardElement("OutputFileBox") is TextBox outputFileBox && !string.IsNullOrWhiteSpace(outputFileBox.Text))
                    recording.OutputFilePath = outputFileBox.Text;

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
                ResetSchedulerForm();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error scheduling recording: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetSchedulerForm()
        {
            if (FindDashboardElement("TitleBox") is TextBox titleBox)
                titleBox.Text = "";
            if (FindDashboardElement("ChannelCombo") is ComboBox channelCombo)
                channelCombo.SelectedIndex = -1;
            if (FindDashboardElement("ProgramCombo") is ComboBox programCombo)
                programCombo.ItemsSource = null;
            if (FindDashboardElement("ProgramTimeText") is TextBlock programTimeText)
                programTimeText.Text = "";
            if (FindDashboardElement("CustomChannelCombo") is ComboBox customChannelCombo)
                customChannelCombo.SelectedIndex = -1;
            if (FindDashboardElement("StartDatePicker") is DatePicker startDatePicker)
                startDatePicker.SelectedDate = DateTime.Today;
            if (FindDashboardElement("StartTimeBox") is TextBox startTimeBox)
                startTimeBox.Text = "20:00";
            if (FindDashboardElement("EndTimeBox") is TextBox endTimeBox)
                endTimeBox.Text = "21:00";
            if (FindDashboardElement("PreBufferBox") is TextBox preBufferBox)
                preBufferBox.Text = "2";
            if (FindDashboardElement("PostBufferBox") is TextBox postBufferBox)
                postBufferBox.Text = "5";
            if (FindDashboardElement("OutputFileBox") is TextBox outputFileBox)
                outputFileBox.Text = "";

            _schedulerSelectedChannel = null;
            _schedulerSelectedProgram = null;
        }
        internal void RefreshScheduled_Click(object sender, RoutedEventArgs e)
        {
            LoadScheduledRecordings();
        }
        internal void DeleteCompleted_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will delete all completed, failed, and cancelled recordings from the list. Continue?",
                "Delete Completed Recordings", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _scheduler.DeleteCompletedRecordings();
            }
        }
        internal void PropertiesRecording_Click(object sender, RoutedEventArgs e)
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
                            $"Stream URL: {DesktopApp.Security.DiagnosticRedactor.Redact(recording.StreamUrl)}\n";

            if (recording.ExitCode.HasValue)
                properties += $"FFmpeg exit code: {recording.ExitCode}\n";
            if (!string.IsNullOrWhiteSpace(recording.FailureReason))
                properties += $"Failure details: {recording.FailureReason}\n";

            if (!string.IsNullOrEmpty(recording.Description))
            {
                properties += $"Description: {recording.Description}\n";
            }

            MessageBox.Show(properties, "Recording Properties", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnScheduledRecordingFailed(ScheduledRecording recording)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || !_scheduler.ScheduledRecordings.Contains(recording)) return;
                var summary = recording.FailureReason?.Split('\n')[0] ?? "Open recording properties for details.";
                ShowToast("Recording failed", $"{recording.Title}: {summary}", "#DC3545");
            });
        }
        internal void EditRecording_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not ScheduledRecording recording)
                return;

            // Simple edit dialog using message boxes for now
            var editMessage = $"Current recording details:\n\n" +
                             $"Title: {recording.Title}\n" +
                             $"Pre-buffer: {recording.PreBufferMinutes} minutes\n" +
                             $"Post-buffer: {recording.PostBufferMinutes} minutes\n\n" +
                             $"This is a basic edit confirmation. Would you like to add 1 minute to both pre and post buffer?";

            var result = MessageBox.Show(editMessage, "Edit Recording",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            // Update the recording with increased buffer times
            var updatedRecording = new ScheduledRecording
            {
                Id = recording.Id,
                Title = recording.Title,
                Description = recording.Description,
                ChannelId = recording.ChannelId,
                ChannelName = recording.ChannelName,
                StreamUrl = recording.StreamUrl,
                StartTime = recording.StartTime,
                EndTime = recording.EndTime,
                Status = recording.Status,
                OutputFilePath = recording.OutputFilePath,
                IsEpgBased = recording.IsEpgBased,
                EpgProgramId = recording.EpgProgramId,
                PreBufferMinutes = recording.PreBufferMinutes + 1,
                PostBufferMinutes = recording.PostBufferMinutes + 1,
                CreatedAt = recording.CreatedAt
            };

            _scheduler.UpdateRecording(updatedRecording);

            MessageBox.Show("Recording updated successfully!", "Edit Recording",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        internal void CancelRecording_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not ScheduledRecording recording)
                return;

            var result = MessageBox.Show($"Cancel recording '{recording.Title}'?", "Cancel Recording",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _scheduler.CancelRecording(recording.Id);
            }
        }

        // Favorites page methods
}
