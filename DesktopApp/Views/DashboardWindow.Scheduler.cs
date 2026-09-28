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
            SchedulerPageModel.NewRecording.Activate(Channels);


            LoadSeriesRecordings();
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
        // Favorites page methods
}
