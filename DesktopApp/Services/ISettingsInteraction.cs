using DesktopApp.Models;

namespace DesktopApp.Services;

// Windows-only interactions are kept outside the form and can be replaced in tests.
public interface ISettingsInteraction
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string DetectPlayer(PlayerKind kind);
    string DetectFfmpeg();
    string? BrowseExecutable(bool ffmpeg);
    string? BrowseDirectory(string currentPath);
    bool Confirm(string message, string title);
    void Notify(string message, string title, bool isError = false);
    bool LaunchPlayer(string path, string arguments);
    void OpenFfmpegDownload();
    string CredentialsFolder { get; }
    void OpenCredentialsFolder();
}
