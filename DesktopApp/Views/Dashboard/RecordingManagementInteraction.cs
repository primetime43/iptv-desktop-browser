using System.Diagnostics;
using System.IO;
using System.Windows;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;

namespace DesktopApp.Views.Dashboard;

public sealed class RecordingManagementInteraction(Func<Window?> owner) : IRecordingManagementInteraction
{
    public RecordingEdits? Edit(ScheduledRecording recording)
    {
        var model = new RecordingEditViewModel(recording);
        var dialog = new RecordingEditWindow(model);
        if (owner() is { } window) dialog.Owner = window;
        return dialog.ShowDialog() == true ? model.Result : null;
    }

    public bool Confirm(string message, string title) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public void ShowMessage(string message, string title, bool isError = false) =>
        Show(message, title, MessageBoxButton.OK, isError ? MessageBoxImage.Warning : MessageBoxImage.Information);
    private MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        owner() is { } window ? MessageBox.Show(window, message, title, buttons, image) : MessageBox.Show(message, title, buttons, image);

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        using var process = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
