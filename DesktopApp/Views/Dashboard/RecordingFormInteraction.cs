using System.IO;
using System.Windows;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Win32;

namespace DesktopApp.Views.Dashboard;

public sealed class RecordingFormInteraction(Func<Window?> owner) : IRecordingFormInteraction
{
    public string? BrowseOutput(string currentPath)
    {
        var dialog = new SaveFileDialog { Filter = "Transport Stream|*.ts|MP4 Video|*.mp4|All Files|*.*", DefaultExt = ".ts" };
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            dialog.FileName = Path.GetFileName(currentPath);
            dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
        }
        var result = owner() is { } window ? dialog.ShowDialog(window) : dialog.ShowDialog();
        return result == true ? dialog.FileName : null;
    }

    public bool ConfirmConflict(ScheduledRecording recording) => Show(
        "This recording conflicts with an existing scheduled recording. Do you want to schedule it anyway?",
        "Recording Conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void Notify(string message, bool isError = false) => Show(message,
        isError ? "Recording Error" : "Recording Scheduled", MessageBoxButton.OK,
        isError ? MessageBoxImage.Warning : MessageBoxImage.Information);

    private MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        owner() is { } window ? MessageBox.Show(window, message, title, buttons, image) : MessageBox.Show(message, title, buttons, image);
}
