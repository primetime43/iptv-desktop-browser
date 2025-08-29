using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DesktopApp.Models;

namespace DesktopApp.Views;

public partial class RecordingStatusWindow : Window
{
    public RecordingStatusWindow()
    {
        InitializeComponent();
        DataContext = RecordingManager.Instance;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StopBtn.IsEnabled = false;
            await Task.Run(() => RecordingStoppedRequested?.Invoke());
        }
        finally
        {
            StopBtn.IsEnabled = true;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = RecordingManager.Instance.FilePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
            }
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public static event Action? RecordingStoppedRequested;
}
