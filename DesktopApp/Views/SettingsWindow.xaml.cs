using System;
using System.Windows;
using Microsoft.Win32;
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
        // Set player kind combo selection
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
    }

    private void BrowsePlayer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select player executable",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                PlayerExeTextBox.Text = dlg.FileName;
            }
        }
        catch { }
    }

    private void PlayerKindCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var kind = GetSelectedKind();
        // Provide default arg templates if box empty or previously matched old default
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
        if (PlayerKindCombo.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag is string tag)
        {
            if (Enum.TryParse<PlayerKind>(tag, out var val)) return val;
        }
        return PlayerKind.VLC;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Session.PreferredPlayer = GetSelectedKind();
        Session.PlayerExePath = string.IsNullOrWhiteSpace(PlayerExeTextBox.Text) ? null : PlayerExeTextBox.Text.Trim();
        Session.PlayerArgsTemplate = string.IsNullOrWhiteSpace(ArgsTemplateTextBox.Text) ? string.Empty : ArgsTemplateTextBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
