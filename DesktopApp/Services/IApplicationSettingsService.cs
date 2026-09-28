using DesktopApp.Models;

namespace DesktopApp.Services;

// The settings editor works on a detached snapshot until a validated save succeeds.
public interface IApplicationSettingsService
{
    SettingsStore Load();
    void Save(SettingsStore settings);
    void SetCachingEnabled(bool enabled);
    DateTime? LastEpgUpdateUtc { get; }
    void RequestEpgRefresh();
}

public sealed class ApplicationSettingsService : IApplicationSettingsService
{
    public SettingsStore Load() => SettingsStore.CaptureSession();

    public void Save(SettingsStore settings)
    {
        // Persist first: a failed write must not partially apply the edited settings.
        settings.Save();
        Session.PreferredPlayer = settings.PreferredPlayer;
        Session.PlayerExePath = settings.PlayerExePath;
        Session.PlayerArgsTemplate = settings.PlayerArgsTemplate ?? string.Empty;
        Session.FfmpegPath = settings.FfmpegPath;
        Session.RecordingDirectory = settings.RecordingDirectory;
        Session.FfmpegArgsTemplate = settings.FfmpegArgsTemplate ?? string.Empty;
        Session.EpgRefreshInterval = TimeSpan.FromMinutes(settings.EpgRefreshIntervalMinutes);
        Session.CachingEnabled = settings.CachingEnabled;
    }

    public void SetCachingEnabled(bool enabled) => Session.CachingEnabled = enabled;
    public DateTime? LastEpgUpdateUtc => Session.LastEpgUpdateUtc;
    public void RequestEpgRefresh() => Session.RaiseEpgRefreshRequested();
}
