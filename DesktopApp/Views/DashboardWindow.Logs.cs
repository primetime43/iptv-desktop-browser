using System;
using System.Windows;
using System.Windows.Controls;

namespace DesktopApp.Views;

public partial class DashboardWindow
{
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

}
