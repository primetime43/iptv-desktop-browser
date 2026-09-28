using System.Diagnostics;
using System.IO;
using System.Windows;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Win32;

namespace DesktopApp.Views.Dashboard;

// Dialog ownership and process launching belong to the desktop UI, not the form model.
public sealed class SettingsInteraction(Func<Window?> owner) : ISettingsInteraction
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string DetectPlayer(PlayerKind kind)
    {
        var paths = kind switch
        {
            PlayerKind.VLC => new[]
            {
                @"C:\Program Files\VideoLAN\VLC\vlc.exe",
                @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\VideoLAN\VLC\vlc.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\VideoLAN\VLC\vlc.exe")
            },
            PlayerKind.MPCHC => new[]
            {
                @"C:\Program Files\MPC-HC\mpc-hc64.exe",
                @"C:\Program Files (x86)\MPC-HC\mpc-hc.exe",
                @"C:\Program Files\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                @"C:\Program Files (x86)\K-Lite Codec Pack\MPC-HC\mpc-hc.exe"
            },
            PlayerKind.MPV => new[] { @"C:\Program Files\mpv\mpv.exe", @"C:\Program Files (x86)\mpv\mpv.exe" },
            _ => Array.Empty<string>()
        };
        return paths.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    public string DetectFfmpeg()
    {
        var paths = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\ffmpeg\bin\ffmpeg.exe")
        };
        var found = paths.FirstOrDefault(File.Exists);
        if (found != null) return found;

        // Search the same locations as `where ffmpeg`, without launching and waiting on a process.
        var folders = new[] { Environment.CurrentDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var path = Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')), "ffmpeg.exe");
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return string.Empty;
    }

    public string? BrowseExecutable(bool ffmpeg)
    {
        var dialog = new OpenFileDialog
        {
            Title = ffmpeg ? "Select ffmpeg executable" : "Select player executable",
            Filter = ffmpeg ? "ffmpeg (ffmpeg.exe)|ffmpeg.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*" :
                "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };
        var selected = owner() is { } window ? dialog.ShowDialog(window) : dialog.ShowDialog();
        return selected == true ? dialog.FileName : null;
    }

    public string? BrowseDirectory(string currentPath)
    {
        var dialog = new OpenFolderDialog { Title = "Select recording directory" };
        if (Directory.Exists(currentPath)) dialog.InitialDirectory = currentPath;
        var selected = owner() is { } window ? dialog.ShowDialog(window) : dialog.ShowDialog();
        return selected == true ? dialog.FolderName : null;
    }

    public bool Confirm(string message, string title) =>
        ShowMessage(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Notify(string message, string title, bool isError = false) =>
        ShowMessage(message, title, MessageBoxButton.OK, isError ? MessageBoxImage.Warning : MessageBoxImage.Information);

    private MessageBoxResult ShowMessage(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        owner() is { } window ? MessageBox.Show(window, message, title, buttons, image) : MessageBox.Show(message, title, buttons, image);

    public bool LaunchPlayer(string path, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = path, Arguments = arguments, UseShellExecute = false });
        return process != null;
    }

    public void OpenFfmpegDownload()
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = "https://ffmpeg.org/download.html", UseShellExecute = true });
    }

    public string CredentialsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IPTV-Desktop-Browser");

    public void OpenCredentialsFolder()
    {
        Directory.CreateDirectory(CredentialsFolder);
        using var process = Process.Start(new ProcessStartInfo { FileName = CredentialsFolder, UseShellExecute = true, Verb = "open" });
    }
}
