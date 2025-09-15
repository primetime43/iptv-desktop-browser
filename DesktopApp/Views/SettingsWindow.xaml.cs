using System;
using System.Windows;
using Microsoft.Win32;
using DesktopApp.Models;
using System.Windows.Controls;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Windows.Media;

namespace DesktopApp.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadFromSession();
        ValidateAllFields();
    }

    private void LoadFromSession()
    {
        PlayerKindCombo.SelectedIndex = Session.PreferredPlayer switch
        {
            PlayerKind.VLC => 0,
            PlayerKind.MPCHC => 1,
            PlayerKind.MPV => 2,
            PlayerKind.Custom => 3,
            _ => 0
        };
        PlayerExeTextBox.Text = Session.PlayerExePath ?? string.Empty;
        ArgsTemplateTextBox.Text = Session.PlayerArgsTemplate ?? string.Empty;
        FfmpegPathTextBox.Text = Session.FfmpegPath ?? string.Empty;
        RecordingDirTextBox.Text = Session.RecordingDirectory ?? string.Empty;
        FfmpegArgsTextBox.Text = Session.FfmpegArgsTemplate ?? string.Empty;
        LastEpgUpdateTextBox.Text = Session.LastEpgUpdateUtc.HasValue
            ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g")
            : "(never)";
        EpgIntervalTextBox.Text = ((int)Session.EpgRefreshInterval.TotalMinutes).ToString();
    }

    private void SetStatusMessage(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(isError ? Color.FromRgb(0xF8, 0x81, 0x66) : Color.FromRgb(0x8B, 0xA1, 0xB9));
    }

    private void ValidateAllFields()
    {
        ValidatePlayerPath();
        ValidateFfmpegPath();
        ValidateRecordingDirectory();
        ValidateEpgInterval();
    }

    // Validation methods
    private void ValidatePlayerPath()
    {
        var path = PlayerExeTextBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            PlayerPathStatus.Text = "Path is required for custom players";
            PlayerPathStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
        }
        else if (!File.Exists(path))
        {
            PlayerPathStatus.Text = "File not found";
            PlayerPathStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
        }
        else
        {
            PlayerPathStatus.Text = "✓ Valid executable";
            PlayerPathStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
        }
    }

    private void ValidateFfmpegPath()
    {
        var path = FfmpegPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            FfmpegPathStatus.Text = "FFmpeg path not set (recording disabled)";
            FfmpegPathStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));
        }
        else if (!File.Exists(path))
        {
            FfmpegPathStatus.Text = "FFmpeg executable not found";
            FfmpegPathStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
        }
        else
        {
            FfmpegPathStatus.Text = "✓ FFmpeg ready for recording";
            FfmpegPathStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
        }
    }

    private void ValidateRecordingDirectory()
    {
        var path = RecordingDirTextBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            RecordingDirStatus.Text = "Using default: My Videos folder";
            RecordingDirStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));
        }
        else if (!Directory.Exists(path))
        {
            RecordingDirStatus.Text = "Directory will be created when recording";
            RecordingDirStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xC5, 0x55));
        }
        else
        {
            RecordingDirStatus.Text = "✓ Directory exists";
            RecordingDirStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
        }
    }

    private void ValidateEpgInterval()
    {
        var text = EpgIntervalTextBox.Text.Trim();
        if (int.TryParse(text, out var minutes) && minutes >= 5 && minutes <= 720)
        {
            EpgIntervalStatus.Text = $"✓ EPG will refresh every {minutes} minutes";
            EpgIntervalStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
        }
        else
        {
            EpgIntervalStatus.Text = "Invalid interval (5-720 minutes allowed)";
            EpgIntervalStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
        }
    }

    // Auto-detection methods
    private void AutoDetectPlayer_Click(object sender, RoutedEventArgs e)
    {
        var kind = GetSelectedKind();
        var detectedPath = DetectPlayerPath(kind);

        if (!string.IsNullOrEmpty(detectedPath))
        {
            PlayerExeTextBox.Text = detectedPath;
            SetStatusMessage($"Auto-detected {kind} player");
            ValidatePlayerPath();
        }
        else
        {
            SetStatusMessage($"Could not auto-detect {kind} player", true);
        }
    }

    private void AutoDetectFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var detectedPath = DetectFfmpegPath();

        if (!string.IsNullOrEmpty(detectedPath))
        {
            FfmpegPathTextBox.Text = detectedPath;
            SetStatusMessage("Auto-detected FFmpeg");
            ValidateFfmpegPath();
        }
        else
        {
            SetStatusMessage("Could not auto-detect FFmpeg", true);
        }
    }

    private string DetectPlayerPath(PlayerKind kind)
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

    private string DetectFfmpegPath()
    {
        var commonPaths = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\ffmpeg\bin\ffmpeg.exe"),
            "ffmpeg.exe" // Check if it's in PATH
        };

        foreach (var path in commonPaths)
        {
            try
            {
                if (Path.GetFileName(path) == "ffmpeg.exe" && path == "ffmpeg.exe")
                {
                    // Check if ffmpeg is in PATH
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
                // Continue to next path
            }
        }

        return string.Empty;
    }

    // Event handlers for text changes
    private void PlayerExeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidatePlayerPath();
    }

    private void FfmpegPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateFfmpegPath();
    }

    private void RecordingDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateRecordingDirectory();
    }

    private void EpgIntervalTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateEpgInterval();
    }

    private void ArgsTemplateTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Update status based on template validity
        var template = ArgsTemplateTextBox.Text.Trim();
        if (string.IsNullOrEmpty(template))
        {
            SetStatusMessage("Using default arguments for selected player");
        }
        else if (template.Contains("{url}"))
        {
            SetStatusMessage("Arguments template looks valid");
        }
        else
        {
            SetStatusMessage("Warning: Template should contain {url} token", true);
        }
    }

    // Test functionality
    private void TestPlayer_Click(object sender, RoutedEventArgs e)
    {
        TestPlayerStatus.Text = "Testing...";
        TestPlayerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xB9));

        var path = PlayerExeTextBox.Text.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            TestPlayerStatus.Text = "❌ Invalid player path";
            TestPlayerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            return;
        }

        try
        {
            // Test with a simple test URL
            var testUrl = "https://sample-videos.com/zip/10/mp4/SampleVideo_1280x720_1mb.mp4";
            var args = string.IsNullOrEmpty(ArgsTemplateTextBox.Text)
                ? GetDefaultArgsForPlayer()
                : ArgsTemplateTextBox.Text;

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
                TestPlayerStatus.Text = "✅ Player launched successfully";
                TestPlayerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x86, 0x3A));
            }
            else
            {
                TestPlayerStatus.Text = "❌ Failed to start player";
                TestPlayerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
            }
        }
        catch (Exception ex)
        {
            TestPlayerStatus.Text = $"❌ Error: {ex.Message}";
            TestPlayerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x66));
        }
    }

    private string GetDefaultArgsForPlayer()
    {
        return GetSelectedKind() switch
        {
            PlayerKind.VLC => "\"{url}\" --meta-title=\"{title}\"",
            PlayerKind.MPCHC => "\"{url}\" /play",
            PlayerKind.MPV => "--force-media-title=\"{title}\" \"{url}\"",
            _ => "{url}"
        };
    }

    // Browse methods with improved error handling
    private void BrowsePlayer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select player executable",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dlg.ShowDialog(this) == true)
            {
                PlayerExeTextBox.Text = dlg.FileName;
                SetStatusMessage("Player executable selected");
            }
        }
        catch (Exception ex)
        {
            SetStatusMessage($"Error selecting player: {ex.Message}", true);
        }
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select ffmpeg executable",
                Filter = "ffmpeg (ffmpeg.exe)|ffmpeg.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dlg.ShowDialog(this) == true)
            {
                FfmpegPathTextBox.Text = dlg.FileName;
                SetStatusMessage("FFmpeg executable selected");
            }
        }
        catch (Exception ex)
        {
            SetStatusMessage($"Error selecting FFmpeg: {ex.Message}", true);
        }
    }

    private void BrowseRecordingDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Use OpenFileDialog as folder picker
            var dialog = new OpenFileDialog
            {
                Title = "Select recording directory (choose any file in the target folder)",
                Filter = "All files (*.*)|*.*",
                CheckFileExists = false,
                FileName = "SelectFolder"
            };

            var currentPath = RecordingDirTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
            {
                dialog.InitialDirectory = currentPath;
            }

            if (dialog.ShowDialog(this) == true)
            {
                var selectedDir = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(selectedDir))
                {
                    RecordingDirTextBox.Text = selectedDir;
                    SetStatusMessage("Recording directory selected");
                }
            }
        }
        catch (Exception ex)
        {
            SetStatusMessage($"Error selecting directory: {ex.Message}", true);
        }
    }

    private void PlayerKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;

        var kind = GetSelectedKind();

        // Auto-update arguments template if it's empty or contains default patterns
        var currentArgs = ArgsTemplateTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(currentArgs) ||
            currentArgs == "{url}" ||
            currentArgs.Contains("meta-title") ||
            currentArgs.Contains("force-media-title") ||
            currentArgs.Contains("/play"))
        {
            ArgsTemplateTextBox.Text = GetDefaultArgsForPlayer();
        }

        // Auto-detect player if path is empty
        if (string.IsNullOrEmpty(PlayerExeTextBox.Text.Trim()))
        {
            var detectedPath = DetectPlayerPath(kind);
            if (!string.IsNullOrEmpty(detectedPath))
            {
                PlayerExeTextBox.Text = detectedPath;
                SetStatusMessage($"Auto-detected {kind} player");
            }
        }

        ValidateAllFields();
    }

    private PlayerKind GetSelectedKind()
    {
        if (PlayerKindCombo.SelectedItem is ComboBoxItem cbi &&
            cbi.Tag is string tag &&
            Enum.TryParse<PlayerKind>(tag, out var val))
            return val;
        return PlayerKind.VLC;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Validate all fields before saving
        var isValid = true;
        var errorMessages = new List<string>();

        // Validate EPG interval
        if (!int.TryParse(EpgIntervalTextBox.Text.Trim(), out var minutes) || minutes < 5 || minutes > 720)
        {
            errorMessages.Add("EPG refresh interval must be between 5 and 720 minutes");
            isValid = false;
        }

        // Validate player path if specified
        var playerPath = PlayerExeTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(playerPath) && !File.Exists(playerPath))
        {
            errorMessages.Add("Player executable path is invalid");
            isValid = false;
        }

        // Validate FFmpeg path if specified
        var ffmpegPath = FfmpegPathTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(ffmpegPath) && !File.Exists(ffmpegPath))
        {
            errorMessages.Add("FFmpeg executable path is invalid");
            isValid = false;
        }

        if (!isValid)
        {
            var message = "Please fix the following issues:\n\n" + string.Join("\n", errorMessages);
            MessageBox.Show(this, message, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // Save all settings
            Session.PreferredPlayer = GetSelectedKind();
            Session.PlayerExePath = string.IsNullOrWhiteSpace(playerPath) ? null : playerPath;
            Session.PlayerArgsTemplate = string.IsNullOrWhiteSpace(ArgsTemplateTextBox.Text)
                ? string.Empty
                : ArgsTemplateTextBox.Text.Trim();
            Session.FfmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath;
            Session.RecordingDirectory = string.IsNullOrWhiteSpace(RecordingDirTextBox.Text)
                ? null
                : RecordingDirTextBox.Text.Trim();
            Session.FfmpegArgsTemplate = string.IsNullOrWhiteSpace(FfmpegArgsTextBox.Text)
                ? Session.FfmpegArgsTemplate
                : FfmpegArgsTextBox.Text.Trim();
            Session.EpgRefreshInterval = TimeSpan.FromMinutes(minutes);

            SettingsStore.SaveFromSession();
            SetStatusMessage("Settings saved successfully!");

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"Error saving settings: {ex.Message}", true);
            MessageBox.Show(this, $"Failed to save settings: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateEpgNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Session.RaiseEpgRefreshRequested();
            LastEpgUpdateTextBox.Text = Session.LastEpgUpdateUtc.HasValue
                ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g")
                : "(never)";
            SetStatusMessage("EPG refresh requested");
        }
        catch (Exception ex)
        {
            SetStatusMessage($"Error requesting EPG refresh: {ex.Message}", true);
        }
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "This will reset all settings to their default values. Continue?",
            "Restore Defaults",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            // Reset UI fields to built-in defaults
            PlayerKindCombo.SelectedIndex = 0; // VLC
            PlayerExeTextBox.Text = string.Empty;
            ArgsTemplateTextBox.Text = "\"{url}\" --meta-title=\"{title}\"";
            FfmpegPathTextBox.Text = string.Empty;
            RecordingDirTextBox.Text = string.Empty;
            FfmpegArgsTextBox.Text = "-i \"{url}\" -c copy -f mpegts \"{output}\"";
            EpgIntervalTextBox.Text = "30";

            ValidateAllFields();
            SetStatusMessage("Settings restored to defaults (not saved yet)");
        }
        catch (Exception ex)
        {
            SetStatusMessage($"Error restoring defaults: {ex.Message}", true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
