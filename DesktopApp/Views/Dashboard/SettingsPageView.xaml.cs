using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopApp.Views.Dashboard;

// Interaction forwarding is retained while page behavior migrates into its view model.
public partial class SettingsPageView : UserControl
{
    public SettingsPageView() => InitializeComponent();
    private void SettingsPlayerKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsPlayerKindCombo_SelectionChanged(sender, e);
    private void SettingsPlayerExeTextBox_TextChanged(object sender, TextChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsPlayerExeTextBox_TextChanged(sender, e);
    private void SettingsBrowsePlayer_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsBrowsePlayer_Click(sender, e);
    private void SettingsAutoDetectPlayer_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsAutoDetectPlayer_Click(sender, e);
    private void SettingsArgsTemplateTextBox_TextChanged(object sender, TextChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsArgsTemplateTextBox_TextChanged(sender, e);
    private void SettingsTestPlayer_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsTestPlayer_Click(sender, e);
    private void SettingsFfmpegPathTextBox_TextChanged(object sender, TextChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsFfmpegPathTextBox_TextChanged(sender, e);
    private void SettingsBrowseFfmpeg_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsBrowseFfmpeg_Click(sender, e);
    private void SettingsAutoDetectFfmpeg_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsAutoDetectFfmpeg_Click(sender, e);
    private void SettingsDownloadFfmpeg_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsDownloadFfmpeg_Click(sender, e);
    private void SettingsRecordingDirTextBox_TextChanged(object sender, TextChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsRecordingDirTextBox_TextChanged(sender, e);
    private void SettingsBrowseRecordingDir_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsBrowseRecordingDir_Click(sender, e);
    private void SettingsEpgIntervalTextBox_TextChanged(object sender, TextChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsEpgIntervalTextBox_TextChanged(sender, e);
    private void SettingsUpdateEpgNow_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsUpdateEpgNow_Click(sender, e);
    private void SettingsCachingEnabledCheckBox_Changed(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsCachingEnabledCheckBox_Changed(sender, e);
    private void SettingsClearCache_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsClearCache_Click(sender, e);
    private void SettingsCacheInspector_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsCacheInspector_Click(sender, e);
    private void SettingsOpenCredentialsFolder_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsOpenCredentialsFolder_Click(sender, e);
    private void ExportFavorites_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ExportFavorites_Click(sender, e);
    private void ImportFavorites_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ImportFavorites_Click(sender, e);
    private void SettingsRestoreDefaults_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsRestoreDefaults_Click(sender, e);
    private void SettingsSave_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.SettingsSave_Click(sender, e);
}