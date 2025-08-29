using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopApp.Models;

namespace DesktopApp.Views;

public partial class RecordingStatusWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public RecordingStatusWindow()
    {
        InitializeComponent();
        DataContext = RecordingManager.Instance;
        _timer.Tick += (_, _) => RecordingManager.Instance.Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
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
