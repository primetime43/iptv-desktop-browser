using DesktopApp.Models;

namespace DesktopApp.Services;

public sealed record RecordingEdits(string Title, int PreBufferMinutes, int PostBufferMinutes);

public interface IRecordingManagementInteraction
{
    RecordingEdits? Edit(ScheduledRecording recording);
    bool Confirm(string message, string title);
    void ShowMessage(string message, string title, bool isError = false);
    void OpenFolder(string path);
}
