using System;
using System.Windows;
using Microsoft.Win32;
using DesktopApp.Models;
using System.Windows.Controls;
using System.IO;
using DesktopApp.Models;

namespace DesktopApp.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadFromSession();
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
        if (FindName("FfmpegPathTextBox") is TextBox ff) ff.Text = Session.FfmpegPath ?? string.Empty;
        if (FindName("RecordingDirTextBox") is TextBox rd) rd.Text = Session.RecordingDirectory ?? string.Empty;
        if (FindName("FfmpegArgsTextBox") is TextBox fa) fa.Text = Session.FfmpegArgsTemplate ?? string.Empty;
        if (FindName("LastEpgUpdateTextBox") is TextBox last)
            last.Text = Session.LastEpgUpdateUtc.HasValue ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g") : "(never)";
        if (FindName("EpgIntervalTextBox") is TextBox interval)
            interval.Text = ((int)Session.EpgRefreshInterval.TotalMinutes).ToString();
    }

    private void BrowsePlayer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog { Title = "Select player executable", Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) PlayerExeTextBox.Text = dlg.FileName;
        }
        catch { }
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog { Title = "Select ffmpeg executable", Filter = "ffmpeg (ffmpeg.exe)|ffmpeg.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true && FindName("FfmpegPathTextBox") is TextBox ff) ff.Text = dlg.FileName;
        }
        catch { }
    }

    private void BrowseRecordingDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Use OpenFileDialog hack: user picks any file (or types name) inside target directory.
            var dlg = new OpenFileDialog
            {
                Title = "Select or create recording folder (pick or type any filename then OK)",
                CheckFileExists = false,
                FileName = "SelectFolderPlaceholder"
            };
            if (dlg.ShowDialog(this) == true && FindName("RecordingDirTextBox") is TextBox rd)
            {
                var path = Path.GetDirectoryName(dlg.FileName) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path)) rd.Text = path;
            }
        }
        catch { }
    }

    private void PlayerKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var kind = GetSelectedKind();
        if (string.IsNullOrWhiteSpace(ArgsTemplateTextBox.Text) || ArgsTemplateTextBox.Text == "{url}" || ArgsTemplateTextBox.Text.Contains("meta-title") || ArgsTemplateTextBox.Text.Contains("force-media-title"))
        {
            ArgsTemplateTextBox.Text = kind switch
            {
                PlayerKind.VLC => "\"{url}\" --meta-title=\"{title}\"",
                PlayerKind.MPCHC => "\"{url}\" /play",
                PlayerKind.MPV => "--force-media-title=\"{title}\" \"{url}\"",
                PlayerKind.Custom => "{url}",
                _ => "{url}"
            };
        }
    }

    private PlayerKind GetSelectedKind()
    {
        if (PlayerKindCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag && Enum.TryParse<PlayerKind>(tag, out var val)) return val;
        return PlayerKind.VLC;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Session.PreferredPlayer = GetSelectedKind();
        Session.PlayerExePath = string.IsNullOrWhiteSpace(PlayerExeTextBox.Text) ? null : PlayerExeTextBox.Text.Trim();
        Session.PlayerArgsTemplate = string.IsNullOrWhiteSpace(ArgsTemplateTextBox.Text) ? string.Empty : ArgsTemplateTextBox.Text.Trim();
        if (FindName("FfmpegPathTextBox") is TextBox ff) Session.FfmpegPath = string.IsNullOrWhiteSpace(ff.Text) ? null : ff.Text.Trim();
        if (FindName("RecordingDirTextBox") is TextBox rd) Session.RecordingDirectory = string.IsNullOrWhiteSpace(rd.Text) ? null : rd.Text.Trim();
        if (FindName("FfmpegArgsTextBox") is TextBox fa) Session.FfmpegArgsTemplate = string.IsNullOrWhiteSpace(fa.Text) ? Session.FfmpegArgsTemplate : fa.Text.Trim();
        if (FindName("EpgIntervalTextBox") is TextBox interval && int.TryParse(interval.Text.Trim(), out var minutes) && minutes > 0 && minutes <= 720)
            Session.EpgRefreshInterval = TimeSpan.FromMinutes(minutes);
        SettingsStore.SaveFromSession();
        DialogResult = true; Close();
    }

    private void UpdateEpgNow_Click(object sender, RoutedEventArgs e)
    {
        Session.RaiseEpgRefreshRequested();
        if (FindName("LastEpgUpdateTextBox") is TextBox last)
            last.Text = Session.LastEpgUpdateUtc.HasValue ? Session.LastEpgUpdateUtc.Value.ToLocalTime().ToString("g") : "(never)";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
