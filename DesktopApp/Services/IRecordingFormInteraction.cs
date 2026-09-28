using DesktopApp.Models;

namespace DesktopApp.Services;

public interface IRecordingFormInteraction
{
    string? BrowseOutput(string currentPath);
    bool ConfirmConflict(ScheduledRecording recording);
    void Notify(string message, bool isError = false);
}
