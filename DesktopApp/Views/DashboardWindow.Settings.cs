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
        private void LoadSettingsPage()
        {
            try
            {
                if (FindDashboardElement("SettingsPlayerKindCombo") is ComboBox playerCombo)
                {
                    playerCombo.SelectedIndex = Session.PreferredPlayer switch
                    {
                        PlayerKind.VLC => 0,
                        PlayerKind.MPCHC => 1,
                        PlayerKind.MPV => 2,
                        PlayerKind.Custom => 3,
                        _ => 0
                    };
                }

                if (FindDashboardElement("SettingsPlayerExeTextBox") is TextBox playerExe)
                    playerExe.Text = Session.PlayerExePath ?? string.Empty;

                if (FindDashboardElement("SettingsArgsTemplateTextBox") is TextBox argsTemplate)
                    argsTemplate.Text = Session.PlayerArgsTemplate ?? string.Empty;

                if (FindDashboardElement("SettingsFfmpegPathTextBox") is TextBox ffmpegPath)
                    ffmpegPath.Text = Session.FfmpegPath ?? string.Empty;

                if (FindDashboardElement("SettingsRecordingDirTextBox") is TextBox recordingDir)
                    recordingDir.Text = Session.RecordingDirectory ?? string.Empty;

                if (FindDashboardElement("SettingsFfmpegArgsTextBox") is TextBox ffmpegArgs)
                    ffmpegArgs.Text = Session.FfmpegArgsTemplate ?? string.Empty;

                if (FindDashboardElement("SettingsLastEpgUpdateTextBox") is TextBox lastEpg)
                {
                    lastEpg.Text = Session.LastEpgUpdateUtc.HasValue
                        ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g")
                        : "(never)";
                }

                if (FindDashboardElement("SettingsEpgIntervalTextBox") is TextBox epgInterval)
                    epgInterval.Text = ((int)Session.EpgRefreshInterval.TotalMinutes).ToString();

                if (FindDashboardElement("SettingsCachingEnabledCheckBox") is CheckBox cachingEnabled)
                    cachingEnabled.IsChecked = Session.CachingEnabled;

                // Set credentials folder path
                if (FindDashboardElement("SettingsCredentialsFolderTextBox") is TextBox credentialsFolder)
                {
                    credentialsFolder.Text = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "IPTV-Desktop-Browser");
                }

                ValidateAllSettingsFields();

                // Initialize logging display
                Log("Settings page loaded. Raw output logging is active.\n");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error loading settings: {ex.Message}", true);
            }
        }

        private void SetSettingsStatusMessage(string message, bool isError = false) =>
            SettingsPageModel.SetStatus(message, isError);
        private void ValidateAllSettingsFields()
        {
            ValidateSettingsPlayerPath();
            ValidateSettingsFfmpegPath();
            ValidateSettingsRecordingDirectory();
            ValidateSettingsEpgInterval();
        }

        private void ValidateSettingsPlayerPath()
        {
            if (FindDashboardElement("SettingsPlayerExeTextBox") is not TextBox pathBox || FindDashboardElement("SettingsPlayerPathStatus") is not TextBlock status)
                return;

            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                status.Text = "Path is required for custom players";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
            else if (!File.Exists(path))
            {
                status.Text = "File not found";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
            else
            {
                status.Text = "✓ Valid executable";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
        }

        private void ValidateSettingsFfmpegPath()
        {
            if (FindDashboardElement("SettingsFfmpegPathTextBox") is not TextBox pathBox || FindDashboardElement("SettingsFfmpegPathStatus") is not TextBlock status)
                return;

            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                status.Text = "FFmpeg path not set (recording disabled)";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));
            }
            else if (!File.Exists(path))
            {
                status.Text = "FFmpeg executable not found";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
            else
            {
                status.Text = "✓ FFmpeg ready for recording";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
        }

        private void ValidateSettingsRecordingDirectory()
        {
            if (FindDashboardElement("SettingsRecordingDirTextBox") is not TextBox pathBox || FindDashboardElement("SettingsRecordingDirStatus") is not TextBlock status)
                return;

            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                status.Text = "Using default: My Videos folder";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));
            }
            else if (!Directory.Exists(path))
            {
                status.Text = "Directory will be created when recording";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xC5, 0x55));
            }
            else
            {
                status.Text = "✓ Directory exists";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
        }

        private void ValidateSettingsEpgInterval()
        {
            if (FindDashboardElement("SettingsEpgIntervalTextBox") is not TextBox textBox || FindDashboardElement("SettingsEpgIntervalStatus") is not TextBlock status)
                return;

            var text = textBox.Text.Trim();
            if (int.TryParse(text, out var minutes) && minutes >= 5 && minutes <= 720)
            {
                status.Text = $"✓ EPG will refresh every {minutes} minutes";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
            else
            {
                status.Text = "Invalid interval (5-720 minutes allowed)";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
        }

        internal void SettingsAutoDetectPlayer_Click(object sender, RoutedEventArgs e)
        {
            var kind = GetSettingsSelectedPlayerKind();
            var detectedPath = DetectSettingsPlayerPath(kind);

            if (!string.IsNullOrEmpty(detectedPath))
            {
                if (FindDashboardElement("SettingsPlayerExeTextBox") is TextBox textBox)
                    textBox.Text = detectedPath;
                SetSettingsStatusMessage($"Auto-detected {kind} player");
                ValidateSettingsPlayerPath();
            }
            else
            {
                SetSettingsStatusMessage($"Could not auto-detect {kind} player", true);
            }
        }

        internal void SettingsAutoDetectFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            var detectedPath = DetectSettingsFfmpegPath();

            if (!string.IsNullOrEmpty(detectedPath))
            {
                if (FindDashboardElement("SettingsFfmpegPathTextBox") is TextBox textBox)
                    textBox.Text = detectedPath;
                SetSettingsStatusMessage("Auto-detected FFmpeg");
                ValidateSettingsFfmpegPath();
            }
            else
            {
                SetSettingsStatusMessage("Could not auto-detect FFmpeg", true);
            }
        }

        internal void SettingsDownloadFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ffmpegUrl = "https://ffmpeg.org/download.html";
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegUrl,
                    UseShellExecute = true
                };
                Process.Start(psi);
                SetSettingsStatusMessage("Opened FFmpeg download page in browser");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error opening browser: {ex.Message}", true);
            }
        }

        private string DetectSettingsPlayerPath(PlayerKind kind)
        {
            var commonPaths = kind switch
            {
                PlayerKind.VLC => new[]
                {
                    @"C:\Program Files\VideoLAN\VLC\vlc.exe",
                    @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\VideoLAN\VLC\vlc.exe"),
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\VideoLAN\VLC\vlc.exe")
                },
                PlayerKind.MPCHC => new[]
                {
                    @"C:\Program Files\MPC-HC\mpc-hc64.exe",
                    @"C:\Program Files (x86)\MPC-HC\mpc-hc.exe",
                    @"C:\Program Files\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                    @"C:\Program Files (x86)\K-Lite Codec Pack\MPC-HC\mpc-hc.exe"
                },
                PlayerKind.MPV => new[]
                {
                    @"C:\Program Files\mpv\mpv.exe",
                    @"C:\Program Files (x86)\mpv\mpv.exe"
                },
                _ => Array.Empty<string>()
            };

            return commonPaths.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private string DetectSettingsFfmpegPath()
        {
            var commonPaths = new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\ffmpeg\bin\ffmpeg.exe"),
                "ffmpeg.exe"
            };

            foreach (var path in commonPaths)
            {
                try
                {
                    if (Path.GetFileName(path) == "ffmpeg.exe" && path == "ffmpeg.exe")
                    {
                        var process = Process.Start(new ProcessStartInfo
                        {
                            FileName = "where",
                            Arguments = "ffmpeg",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        });

                        if (process != null)
                        {
                            var output = process.StandardOutput.ReadToEnd();
                            process.WaitForExit();
                            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                            {
                                return output.Split('\n')[0].Trim();
                            }
                        }
                    }
                    else if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        // Settings event handlers
        internal void SettingsPlayerExeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSettingsPlayerPath();
        }

        internal void SettingsFfmpegPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSettingsFfmpegPath();
        }

        internal void SettingsRecordingDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSettingsRecordingDirectory();
        }

        internal void SettingsEpgIntervalTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSettingsEpgInterval();
        }

        internal void SettingsArgsTemplateTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (FindDashboardElement("SettingsArgsTemplateTextBox") is not TextBox textBox) return;

            var template = textBox.Text.Trim();
            if (string.IsNullOrEmpty(template))
            {
                SetSettingsStatusMessage("Using default arguments for selected player");
            }
            else if (template.Contains("{url}"))
            {
                SetSettingsStatusMessage("Arguments template looks valid");
            }
            else
            {
                SetSettingsStatusMessage("Warning: Template should contain {url} token", true);
            }
        }

        internal void SettingsTestPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (FindDashboardElement("SettingsTestPlayerStatus") is not TextBlock status || FindDashboardElement("SettingsPlayerExeTextBox") is not TextBox pathBox)
                return;

            status.Text = "Testing...";
            status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));

            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                status.Text = "❌ Invalid player path";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
                return;
            }

            try
            {
                var testUrl = "https://sample-videos.com/zip/10/mp4/SampleVideo_1280x720_1mb.mp4";
                var args = string.Empty;

                if (FindDashboardElement("SettingsArgsTemplateTextBox") is TextBox argsBox)
                {
                    args = string.IsNullOrEmpty(argsBox.Text)
                        ? GetSettingsDefaultArgsForPlayer()
                        : argsBox.Text;
                }

                args = args.Replace("{url}", $"\"{testUrl}\"")
                          .Replace("{title}", "Test Video");

                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args,
                    UseShellExecute = false
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    status.Text = "✅ Player launched successfully";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
                }
                else
                {
                    status.Text = "❌ Failed to start player";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
                }
            }
            catch (Exception ex)
            {
                status.Text = $"❌ Error: {ex.Message}";
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
        }

        private string GetSettingsDefaultArgsForPlayer()
        {
            return GetSettingsSelectedPlayerKind() switch
            {
                PlayerKind.VLC => "\"{url}\" --meta-title=\"{title}\"",
                PlayerKind.MPCHC => "\"{url}\" /play",
                PlayerKind.MPV => "--force-media-title=\"{title}\" \"{url}\"",
                _ => "{url}"
            };
        }

        internal void SettingsBrowsePlayer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select player executable",
                    Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                    CheckFileExists = true
                };

                if (dlg.ShowDialog() == true)
                {
                    if (FindDashboardElement("SettingsPlayerExeTextBox") is TextBox textBox)
                        textBox.Text = dlg.FileName;
                    SetSettingsStatusMessage("Player executable selected");
                }
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error selecting player: {ex.Message}", true);
            }
        }

        internal void SettingsBrowseFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select ffmpeg executable",
                    Filter = "ffmpeg (ffmpeg.exe)|ffmpeg.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
                    CheckFileExists = true
                };

                if (dlg.ShowDialog() == true)
                {
                    if (FindDashboardElement("SettingsFfmpegPathTextBox") is TextBox textBox)
                        textBox.Text = dlg.FileName;
                    SetSettingsStatusMessage("FFmpeg executable selected");
                }
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error selecting FFmpeg: {ex.Message}", true);
            }
        }

        internal void SettingsBrowseRecordingDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Select recording directory"
                };

                if (FindDashboardElement("SettingsRecordingDirTextBox") is TextBox textBox)
                {
                    var currentPath = textBox.Text.Trim();
                    if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
                    {
                        dialog.InitialDirectory = currentPath;
                    }

                    if (dialog.ShowDialog() == true)
                    {
                        textBox.Text = dialog.FolderName;
                        SetSettingsStatusMessage("Recording directory selected");
                    }
                }
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error selecting directory: {ex.Message}", true);
            }
        }

        internal void SettingsPlayerKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized) return;

            var kind = GetSettingsSelectedPlayerKind();

            if (FindDashboardElement("SettingsArgsTemplateTextBox") is TextBox argsBox)
            {
                var currentArgs = argsBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(currentArgs) ||
                    currentArgs == "{url}" ||
                    currentArgs.Contains("meta-title") ||
                    currentArgs.Contains("force-media-title") ||
                    currentArgs.Contains("/play"))
                {
                    argsBox.Text = GetSettingsDefaultArgsForPlayer();
                }
            }

            if (FindDashboardElement("SettingsPlayerExeTextBox") is TextBox playerBox && string.IsNullOrEmpty(playerBox.Text.Trim()))
            {
                var detectedPath = DetectSettingsPlayerPath(kind);
                if (!string.IsNullOrEmpty(detectedPath))
                {
                    playerBox.Text = detectedPath;
                    SetSettingsStatusMessage($"Auto-detected {kind} player");
                }
            }

            ValidateAllSettingsFields();
        }

        private PlayerKind GetSettingsSelectedPlayerKind()
        {
            if (FindDashboardElement("SettingsPlayerKindCombo") is ComboBox combo &&
                combo.SelectedItem is ComboBoxItem cbi &&
                cbi.Tag is string tag &&
                Enum.TryParse<PlayerKind>(tag, out var val))
                return val;
            return PlayerKind.VLC;
        }

        internal void SettingsSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var isValid = true;
                var errorMessages = new List<string>();

                if (FindDashboardElement("SettingsEpgIntervalTextBox") is TextBox epgBox)
                {
                    if (!int.TryParse(epgBox.Text.Trim(), out var minutes) || minutes < 5 || minutes > 720)
                    {
                        errorMessages.Add("EPG refresh interval must be between 5 and 720 minutes");
                        isValid = false;
                    }
                    else
                    {
                        Session.EpgRefreshInterval = TimeSpan.FromMinutes(minutes);
                    }
                }

                if (FindDashboardElement("SettingsPlayerExeTextBox") is TextBox playerBox)
                {
                    var playerPath = playerBox.Text.Trim();
                    if (!string.IsNullOrEmpty(playerPath) && !File.Exists(playerPath))
                    {
                        errorMessages.Add("Player executable path is invalid");
                        isValid = false;
                    }
                    else
                    {
                        Session.PlayerExePath = string.IsNullOrWhiteSpace(playerPath) ? null : playerPath;
                    }
                }

                if (FindDashboardElement("SettingsFfmpegPathTextBox") is TextBox ffmpegBox)
                {
                    var ffmpegPath = ffmpegBox.Text.Trim();
                    if (!string.IsNullOrEmpty(ffmpegPath) && !File.Exists(ffmpegPath))
                    {
                        errorMessages.Add("FFmpeg executable path is invalid");
                        isValid = false;
                    }
                    else
                    {
                        Session.FfmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath;
                    }
                }

                if (!isValid)
                {
                    var message = "Please fix the following issues:\n\n" + string.Join("\n", errorMessages);
                    MessageBox.Show(message, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Session.PreferredPlayer = GetSettingsSelectedPlayerKind();

                if (FindDashboardElement("SettingsArgsTemplateTextBox") is TextBox argsBox)
                {
                    Session.PlayerArgsTemplate = string.IsNullOrWhiteSpace(argsBox.Text)
                        ? string.Empty
                        : argsBox.Text.Trim();
                }

                if (FindDashboardElement("SettingsRecordingDirTextBox") is TextBox recordingBox)
                {
                    Session.RecordingDirectory = string.IsNullOrWhiteSpace(recordingBox.Text)
                        ? null
                        : recordingBox.Text.Trim();
                }

                if (FindDashboardElement("SettingsFfmpegArgsTextBox") is TextBox ffmpegArgsBox)
                {
                    Session.FfmpegArgsTemplate = string.IsNullOrWhiteSpace(ffmpegArgsBox.Text)
                        ? Session.FfmpegArgsTemplate
                        : ffmpegArgsBox.Text.Trim();
                }

                SettingsStore.SaveFromSession();
                SetSettingsStatusMessage("Settings saved successfully!");
                MessageBox.Show("Settings saved successfully!", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error saving settings: {ex.Message}", true);
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void SettingsUpdateEpgNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Session.RaiseEpgRefreshRequested();
                if (FindDashboardElement("SettingsLastEpgUpdateTextBox") is TextBox lastEpgBox)
                {
                    lastEpgBox.Text = Session.LastEpgUpdateUtc.HasValue
                        ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g")
                        : "(never)";
                }
                SetSettingsStatusMessage("EPG refresh requested");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error requesting EPG refresh: {ex.Message}", true);
            }
        }

        internal void SettingsRestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("This will reset all settings to their default values. Continue?",
                "Restore Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                if (FindDashboardElement("SettingsPlayerKindCombo") is ComboBox playerCombo)
                    playerCombo.SelectedIndex = 0;

                if (FindDashboardElement("SettingsPlayerExeTextBox") is TextBox playerBox)
                    playerBox.Text = string.Empty;

                if (FindDashboardElement("SettingsArgsTemplateTextBox") is TextBox argsBox)
                    argsBox.Text = "\"{url}\" --meta-title=\"{title}\"";

                if (FindDashboardElement("SettingsFfmpegPathTextBox") is TextBox ffmpegBox)
                    ffmpegBox.Text = string.Empty;

                if (FindDashboardElement("SettingsRecordingDirTextBox") is TextBox recordingBox)
                    recordingBox.Text = string.Empty;

                if (FindDashboardElement("SettingsFfmpegArgsTextBox") is TextBox ffmpegArgsBox)
                    ffmpegArgsBox.Text = "-i \"{url}\" -c copy -f mpegts \"{output}\"";

                if (FindDashboardElement("SettingsEpgIntervalTextBox") is TextBox epgBox)
                    epgBox.Text = "30";

                ValidateAllSettingsFields();
                SetSettingsStatusMessage("Settings restored to defaults (not saved yet)");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error restoring defaults: {ex.Message}", true);
            }
        }

        // Log control event handlers
        private void SettingsEnableLogging_Changed(object sender, RoutedEventArgs e)
        {
            if (FindDashboardElement("RawOutputLogTextBlock") is TextBlock logTextBlock)
            {
                if (FindDashboardElement("SettingsEnableLoggingCheckBox") is CheckBox checkBox && checkBox.IsChecked == true)
                {
                    if (logTextBlock.Text == "Raw output log will appear here when logging is enabled...")
                    {
                        logTextBlock.Text = $"[{DateTime.Now:HH:mm:ss.fff}] Logging enabled.\n";
                    }
                    else
                    {
                        Log("Logging enabled.\n");
                    }
                }
                else
                {
                    Log("Logging disabled.\n");
                }
            }
        }

        private void SettingsClearLog_Click(object sender, RoutedEventArgs e)
        {
            if (FindDashboardElement("RawOutputLogTextBlock") is TextBlock logTextBlock)
            {
                logTextBlock.Text = $"[{DateTime.Now:HH:mm:ss.fff}] Log cleared.\n";
            }
        }

        private void SettingsCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (FindDashboardElement("RawOutputLogTextBlock") is TextBlock logTextBlock)
                {
                    Clipboard.SetText(logTextBlock.Text);
                    Log("Log copied to clipboard.\n");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy log to clipboard: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SettingsSaveLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (FindDashboardElement("RawOutputLogTextBlock") is TextBlock logTextBlock)
                {
                    var saveDialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
                        DefaultExt = ".txt",
                        FileName = $"iptv-log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt"
                    };

                    if (saveDialog.ShowDialog() == true)
                    {
                        System.IO.File.WriteAllText(saveDialog.FileName, logTextBlock.Text);
                        Log($"Log saved to: {saveDialog.FileName}\n");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save log: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void SettingsCachingEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (SettingsCachingEnabledCheckBox.IsChecked.HasValue)
            {
                Session.CachingEnabled = SettingsCachingEnabledCheckBox.IsChecked.Value;
                Log($"Disk caching {(Session.CachingEnabled ? "enabled" : "disabled")} (in-memory caching always active)\n");
            }
        }

        internal async void SettingsClearCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("Clear all cached data and images? This will remove all cached EPG data, VOD content, and images.",
                    "Confirm Clear Cache", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    var button = sender as Button;
                    if (button != null)
                    {
                        button.IsEnabled = false;
                        button.Content = "🧹 Clearing...";
                    }

                    _cacheService.ClearImageCache();
                    await _cacheService.ClearAllDataAsync();

                    Log("All cache cleared successfully\n");
                    MessageBox.Show("Cache cleared successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    if (button != null)
                    {
                        button.IsEnabled = true;
                        button.Content = "Clear All Cache";
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to clear cache: {ex.Message}\n");
                MessageBox.Show($"Failed to clear cache: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void SettingsCacheInspector_Click(object sender, RoutedEventArgs e)
        {
            OpenCacheInspector(sender, e);
        }

        internal void SettingsOpenCredentialsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var credentialsFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "IPTV-Desktop-Browser");

                // Create the folder if it doesn't exist
                if (!Directory.Exists(credentialsFolder))
                {
                    Directory.CreateDirectory(credentialsFolder);
                }

                // Open the folder in Windows Explorer
                Process.Start(new ProcessStartInfo
                {
                    FileName = credentialsFolder,
                    UseShellExecute = true,
                    Verb = "open"
                });

                SetSettingsStatusMessage("Credentials folder opened in Explorer");
            }
            catch (Exception ex)
            {
                SetSettingsStatusMessage($"Error opening folder: {ex.Message}", true);
                MessageBox.Show(this, $"Failed to open credentials folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

}
