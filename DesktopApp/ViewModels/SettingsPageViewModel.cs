using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public enum SettingsStatusKind { Neutral, Success, Warning, Error }
public sealed record SettingsFieldStatus(string Message, SettingsStatusKind Kind = SettingsStatusKind.Neutral);
public sealed record PlayerChoice(PlayerKind Kind, string Name);

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly IApplicationSettingsService _settings;
    private readonly ISettingsInteraction _interaction;
    private readonly ICacheService _cache;
    private bool _loading;
    private string _savedFfmpegArguments = string.Empty;

    public SettingsPageViewModel(IApplicationSettingsService settings, ISettingsInteraction interaction, ICacheService cache)
    {
        _settings = settings;
        _interaction = interaction;
        _cache = cache;
    }

    public IReadOnlyList<PlayerChoice> Players { get; } =
    [new(PlayerKind.VLC, "VLC Media Player"), new(PlayerKind.MPCHC, "MPC-HC"),
        new(PlayerKind.MPV, "mpv"), new(PlayerKind.Custom, "Custom Player")];
    public string CredentialsFolder => _interaction.CredentialsFolder;

    [ObservableProperty] private PlayerKind _preferredPlayer = PlayerKind.VLC;
    [ObservableProperty] private string _playerPath = string.Empty;
    [ObservableProperty] private string _playerArguments = string.Empty;
    [ObservableProperty] private string _ffmpegPath = string.Empty;
    [ObservableProperty] private string _recordingDirectory = string.Empty;
    [ObservableProperty] private string _ffmpegArguments = string.Empty;
    [ObservableProperty] private string _epgIntervalMinutes = "30";
    [ObservableProperty] private bool _cachingEnabled;
    [ObservableProperty] private string _lastEpgUpdate = "(never)";
    [ObservableProperty] private string _statusMessage = "Configure your IPTV player and recording settings";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private SettingsFieldStatus _playerPathStatus = new("");
    [ObservableProperty] private SettingsFieldStatus _ffmpegPathStatus = new("");
    [ObservableProperty] private SettingsFieldStatus _recordingDirectoryStatus = new("");
    [ObservableProperty] private SettingsFieldStatus _epgIntervalStatus = new("");
    [ObservableProperty] private SettingsFieldStatus _testPlayerStatus = new("");
    [ObservableProperty] private bool _isClearingCache;

    public void Load()
    {
        try
        {
            _loading = true;
            var value = _settings.Load();
            PreferredPlayer = value.PreferredPlayer;
            PlayerPath = value.PlayerExePath ?? string.Empty;
            PlayerArguments = value.PlayerArgsTemplate ?? string.Empty;
            FfmpegPath = value.FfmpegPath ?? string.Empty;
            RecordingDirectory = value.RecordingDirectory ?? string.Empty;
            FfmpegArguments = _savedFfmpegArguments = value.FfmpegArgsTemplate ?? string.Empty;
            EpgIntervalMinutes = value.EpgRefreshIntervalMinutes.ToString();
            CachingEnabled = value.CachingEnabled;
            RefreshLastEpgUpdate();
            ValidateFields();
            TestPlayerStatus = new("");
            SetStatus("Configure your IPTV player and recording settings");
        }
        catch (Exception ex) { SetStatus($"Error loading settings: {ex.Message}", true); }
        finally { _loading = false; }
    }

    public void SetStatus(string message, bool isError = false)
    {
        StatusIsError = isError;
        StatusMessage = message;
    }

    partial void OnPlayerPathChanged(string value) => ValidatePlayerPath();
    partial void OnFfmpegPathChanged(string value) => ValidateFfmpegPath();
    partial void OnRecordingDirectoryChanged(string value) => ValidateRecordingDirectory();
    partial void OnEpgIntervalMinutesChanged(string value) => ValidateEpgInterval();
    partial void OnPlayerArgumentsChanged(string value)
    {
        if (_loading) return;
        SetStatus(string.IsNullOrWhiteSpace(value) ? "Using default arguments for selected player" :
            value.Contains("{url}") ? "Arguments template looks valid" : "Warning: Template should contain {url} token",
            !string.IsNullOrWhiteSpace(value) && !value.Contains("{url}"));
    }

    partial void OnPreferredPlayerChanged(PlayerKind value)
    {
        if (_loading) return;
        var args = PlayerArguments.Trim();
        if (string.IsNullOrEmpty(args) || args == "{url}" || args.Contains("meta-title") ||
            args.Contains("force-media-title") || args.Contains("/play"))
            PlayerArguments = DefaultPlayerArguments(value);
        if (string.IsNullOrWhiteSpace(PlayerPath)) AutoDetectPlayer();
        ValidatePlayerPath();
    }

    partial void OnCachingEnabledChanged(bool value)
    {
        // Preserve the immediate disk-cache toggle; loading a snapshot has no side effects.
        if (!_loading) _settings.SetCachingEnabled(value);
    }

    private void ValidateFields()
    {
        ValidatePlayerPath();
        ValidateFfmpegPath();
        ValidateRecordingDirectory();
        ValidateEpgInterval();
    }

    private void ValidatePlayerPath()
    {
        var path = PlayerPath.Trim();
        PlayerPathStatus = path.Length == 0 ? new("Path is required for custom players", SettingsStatusKind.Warning) :
            !_interaction.FileExists(path) ? new("File not found", SettingsStatusKind.Error) :
            new("✓ Valid executable", SettingsStatusKind.Success);
    }

    private void ValidateFfmpegPath()
    {
        var path = FfmpegPath.Trim();
        FfmpegPathStatus = path.Length == 0 ? new("FFmpeg path not set (recording disabled)") :
            !_interaction.FileExists(path) ? new("FFmpeg executable not found", SettingsStatusKind.Error) :
            new("✓ FFmpeg ready for recording", SettingsStatusKind.Success);
    }

    private void ValidateRecordingDirectory()
    {
        var path = RecordingDirectory.Trim();
        RecordingDirectoryStatus = path.Length == 0 ? new("Using default: My Videos folder") :
            !_interaction.DirectoryExists(path) ? new("Directory will be created when recording", SettingsStatusKind.Warning) :
            new("✓ Directory exists", SettingsStatusKind.Success);
    }

    private bool HasValidInterval(out int minutes) =>
        int.TryParse(EpgIntervalMinutes.Trim(), out minutes) && minutes >= 5 && minutes <= 720;

    private void ValidateEpgInterval() => EpgIntervalStatus = HasValidInterval(out var minutes)
        ? new($"✓ EPG will refresh every {minutes} minutes", SettingsStatusKind.Success)
        : new("Invalid interval (5-720 minutes allowed)", SettingsStatusKind.Error);

    [RelayCommand]
    private void Save()
    {
        try
        {
            ValidateFields();
            var errors = new List<string>();
            if (!HasValidInterval(out var minutes)) errors.Add("EPG refresh interval must be between 5 and 720 minutes");
            if (PlayerPathStatus.Kind == SettingsStatusKind.Error) errors.Add("Player executable path is invalid");
            if (FfmpegPathStatus.Kind == SettingsStatusKind.Error) errors.Add("FFmpeg executable path is invalid");
            if (errors.Count > 0)
            {
                var message = "Please fix the following issues:\n\n" + string.Join("\n", errors);
                SetStatus(message, true);
                _interaction.Notify(message, "Validation Error", true);
                return;
            }

            var value = new SettingsStore
            {
                PreferredPlayer = PreferredPlayer,
                PlayerExePath = OptionalPath(PlayerPath),
                PlayerArgsTemplate = PlayerArguments.Trim(),
                FfmpegPath = OptionalPath(FfmpegPath),
                RecordingDirectory = OptionalPath(RecordingDirectory),
                FfmpegArgsTemplate = string.IsNullOrWhiteSpace(FfmpegArguments) ? _savedFfmpegArguments : FfmpegArguments.Trim(),
                EpgRefreshIntervalMinutes = minutes,
                CachingEnabled = CachingEnabled
            };
            _settings.Save(value);
            _savedFfmpegArguments = value.FfmpegArgsTemplate ?? string.Empty;
            SetStatus("Settings saved successfully!");
            _interaction.Notify(StatusMessage, "Settings");
        }
        catch (Exception ex)
        {
            SetStatus($"Error saving settings: {ex.Message}", true);
            _interaction.Notify(StatusMessage, "Save Error", true);
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        if (!_interaction.Confirm("This will reset all settings to their default values. Continue?", "Restore Defaults")) return;
        _loading = true;
        try
        {
            PreferredPlayer = PlayerKind.VLC;
            PlayerPath = FfmpegPath = RecordingDirectory = string.Empty;
            PlayerArguments = DefaultPlayerArguments(PreferredPlayer);
            FfmpegArguments = "-i \"{url}\" -c copy -f mpegts \"{output}\"";
            EpgIntervalMinutes = "30";
            ValidateFields();
            SetStatus("Settings restored to defaults (not saved yet)");
        }
        finally { _loading = false; }
    }

    [RelayCommand]
    private void AutoDetectPlayer() => RunInteraction(() =>
    {
        var path = _interaction.DetectPlayer(PreferredPlayer);
        if (path.Length > 0) PlayerPath = path;
        SetStatus(path.Length > 0 ? $"Auto-detected {PreferredPlayer} player" : $"Could not auto-detect {PreferredPlayer} player", path.Length == 0);
    });

    [RelayCommand]
    private void AutoDetectFfmpeg() => RunInteraction(() =>
    {
        var path = _interaction.DetectFfmpeg();
        if (path.Length > 0) FfmpegPath = path;
        SetStatus(path.Length > 0 ? "Auto-detected FFmpeg" : "Could not auto-detect FFmpeg", path.Length == 0);
    });

    [RelayCommand]
    private void BrowsePlayer() => RunInteraction(() =>
    {
        if (_interaction.BrowseExecutable(false) is not { } path) return;
        PlayerPath = path;
        SetStatus("Player executable selected");
    });

    [RelayCommand]
    private void BrowseFfmpeg() => RunInteraction(() =>
    {
        if (_interaction.BrowseExecutable(true) is not { } path) return;
        FfmpegPath = path;
        SetStatus("FFmpeg executable selected");
    });

    [RelayCommand]
    private void BrowseRecordingDirectory() => RunInteraction(() =>
    {
        if (_interaction.BrowseDirectory(RecordingDirectory.Trim()) is not { } path) return;
        RecordingDirectory = path;
        SetStatus("Recording directory selected");
    });

    [RelayCommand]
    private void DownloadFfmpeg() => RunInteraction(() =>
    {
        _interaction.OpenFfmpegDownload();
        SetStatus("Opened FFmpeg download page in browser");
    });

    [RelayCommand]
    private void OpenCredentialsFolder() => RunInteraction(() =>
    {
        _interaction.OpenCredentialsFolder();
        SetStatus("Credentials folder opened in Explorer");
    });

    [RelayCommand]
    private void UpdateEpgNow() => RunInteraction(() =>
    {
        _settings.RequestEpgRefresh();
        RefreshLastEpgUpdate();
        SetStatus("EPG refresh requested");
    });

    public void RefreshLastEpgUpdate() => LastEpgUpdate = _settings.LastEpgUpdateUtc?.ToLocalTime().ToString("g") ?? "(never)";

    [RelayCommand]
    private void TestPlayer()
    {
        var path = PlayerPath.Trim();
        if (path.Length == 0 || !_interaction.FileExists(path))
        {
            TestPlayerStatus = new("❌ Invalid player path", SettingsStatusKind.Error);
            return;
        }
        try
        {
            var args = string.IsNullOrWhiteSpace(PlayerArguments) ? DefaultPlayerArguments(PreferredPlayer) : PlayerArguments;
            const string testUrl = "https://sample-videos.com/zip/10/mp4/SampleVideo_1280x720_1mb.mp4";
            args = args.Replace("{url}", testUrl).Replace("{title}", "Test Video");
            TestPlayerStatus = _interaction.LaunchPlayer(path, args)
                ? new("✅ Player launched successfully", SettingsStatusKind.Success)
                : new("❌ Failed to start player", SettingsStatusKind.Error);
        }
        catch (Exception ex) { TestPlayerStatus = new($"❌ Error: {ex.Message}", SettingsStatusKind.Error); }
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (!_interaction.Confirm("Clear all cached data and images? This will remove all cached EPG data, VOD content, and images.", "Confirm Clear Cache")) return;
        IsClearingCache = true;
        try
        {
            _cache.ClearImageCache();
            await _cache.ClearAllDataAsync();
            SetStatus("Cache cleared successfully!");
            _interaction.Notify(StatusMessage, "Success");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to clear cache: {ex.Message}", true);
            _interaction.Notify(StatusMessage, "Error", true);
        }
        finally { IsClearingCache = false; }
    }

    private void RunInteraction(Action action)
    {
        try { action(); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private static string? OptionalPath(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string DefaultPlayerArguments(PlayerKind kind) => kind switch
    {
        PlayerKind.VLC => "\"{url}\" --meta-title=\"{title}\"",
        PlayerKind.MPCHC => "\"{url}\" /play",
        PlayerKind.MPV => "--force-media-title=\"{title}\" \"{url}\"",
        _ => "{url}"
    };
}
