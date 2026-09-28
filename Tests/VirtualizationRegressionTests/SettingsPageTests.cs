using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

internal static partial class Program
{
    private static (SettingsPageViewModel Model, FakeSettingsStore Store, FakeSettingsInteraction Interaction, SettingsCache Cache) CreateSettingsFixture()
    {
        var store = new FakeSettingsStore();
        var interaction = new FakeSettingsInteraction();
        var cache = new SettingsCache();
        return (new SettingsPageViewModel(store, interaction, cache), store, interaction, cache);
    }

    private static void VerifySettingsBindings(SettingsPageView view, SettingsPageViewModel settings)
    {
        settings.Load();
        Layout(view);
        var player = (TextBox)view.FindName("SettingsPlayerExeTextBox");
        Check(player.Text == "player.exe", "Settings form loads its own view model without a dashboard host");
        player.SetCurrentValue(TextBox.TextProperty, "missing.exe");
        Layout(view);
        Check(settings.PlayerPath == "missing.exe", "Typing updates the settings draft immediately");
        var status = (TextBlock)view.FindName("SettingsPlayerPathStatus");
        Check(status.Text == "File not found" && ((SolidColorBrush)status.Foreground).Color == Color.FromRgb(0xF8, 0x81, 0x66),
            "Validation text and error color are bindings, not control mutations");
        settings.PlayerPath = "player.exe";
        Layout(view);
        Check(player.Text == "player.exe" && status.Text.Contains("Valid"), "Model changes update the editor and validation");
        var combo = (ComboBox)view.FindName("SettingsPlayerKindCombo");
        combo.SetCurrentValue(ComboBox.SelectedValueProperty, PlayerKind.MPV);
        Layout(view);
        Check(settings.PreferredPlayer == PlayerKind.MPV && settings.PlayerArguments.Contains("force-media-title"),
            "Player selection binds enum values and updates default arguments through the model");
        var interval = (TextBox)view.FindName("SettingsEpgIntervalTextBox");
        interval.SetCurrentValue(TextBox.TextProperty, "not a number");
        Layout(view);
        Check(settings.EpgIntervalMinutes == "not a number" && settings.EpgIntervalStatus.Kind == SettingsStatusKind.Error,
            "Invalid interval text remains editable and gets model validation");
        var buttons = Descendants(view).OfType<Button>().ToList();
        Check(buttons.Any(b => ReferenceEquals(b.Command, settings.SaveCommand)) &&
            buttons.Any(b => ReferenceEquals(b.Command, settings.RestoreDefaultsCommand)) &&
            buttons.Any(b => ReferenceEquals(b.Command, settings.ClearCacheCommand)), "Settings action buttons resolve page-model commands");
        var lastUpdate = (TextBox)view.FindName("SettingsLastEpgUpdateTextBox");
        Check(lastUpdate.Text == "(never)", "Read-only settings fields bind one-way without source update errors");
        var requests = 0;
        view.CacheInspectorRequested += (_, _) => requests++;
        buttons.Single(b => Equals(b.Content, "Cache Inspector")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(requests == 1, "Shared settings actions raise host events without a DashboardWindow dependency");
    }

    private static void VerifySettingsCommands()
    {
        var (model, store, interaction, cache) = CreateSettingsFixture();
        model.Load();
        Check(model.PlayerPath == "player.exe" && model.EpgIntervalMinutes == "30" && store.Saves == 0 && store.CacheToggles == 0,
            "Loading settings only populates a detached draft");
        Check(model.PlayerPathStatus.Kind == SettingsStatusKind.Success && model.FfmpegPathStatus.Kind == SettingsStatusKind.Success,
            "Loaded paths are validated without constructing controls");
        model.EpgIntervalMinutes = "60";
        model.PlayerPath = "missing.exe";
        model.SaveCommand.Execute(null);
        Check(store.Saves == 0 && store.Value.EpgRefreshIntervalMinutes == 30 && store.Value.PlayerExePath == "player.exe",
            "Invalid save cannot partially change the live interval or player path");
        Check(model.StatusIsError && interaction.Notifications.Last().IsError, "Validation failures surface through both status and dialog");
        model.PlayerPath = " player.exe ";
        model.FfmpegPath = "missing.exe";
        model.SaveCommand.Execute(null);
        Check(store.Saves == 0, "An invalid FFmpeg path prevents the entire save");
        model.FfmpegPath = " ffmpeg.exe ";
        foreach (var invalid in new[] { "4", "721", "text", "" })
        {
            model.EpgIntervalMinutes = invalid;
            model.SaveCommand.Execute(null);
            Check(store.Saves == 0 && model.EpgIntervalStatus.Kind == SettingsStatusKind.Error, $"Interval '{invalid}' is rejected before persistence");
        }
        model.EpgIntervalMinutes = " 5 ";
        model.PlayerArguments = " --custom {url} ";
        model.FfmpegArguments = " ";
        model.RecordingDirectory = " new recordings ";
        model.SaveCommand.Execute(null);
        Check(store.Saves == 1 && store.Value.PlayerExePath == "player.exe" && store.Value.FfmpegPath == "ffmpeg.exe" &&
            store.Value.PlayerArgsTemplate == "--custom {url}" && store.Value.RecordingDirectory == "new recordings" &&
            store.Value.EpgRefreshIntervalMinutes == 5, "Valid save applies one normalized snapshot");
        Check(store.Value.FfmpegArgsTemplate == "-i {url} {output}", "Blank FFmpeg template retains the previously saved template");
        model.EpgIntervalMinutes = "720";
        model.SaveCommand.Execute(null);
        Check(store.Value.EpgRefreshIntervalMinutes == 720 && !model.StatusIsError, "The upper interval boundary is accepted");
        store.FailSave = true;
        model.EpgIntervalMinutes = "90";
        model.SaveCommand.Execute(null);
        Check(store.Value.EpgRefreshIntervalMinutes == 720 && model.StatusIsError && model.StatusMessage.Contains("disk full"),
            "A persistence failure keeps live settings intact and cannot report success");
        store.FailSave = false;
        model.CachingEnabled = !model.CachingEnabled;
        Check(store.CacheToggles == 1 && store.Value.CachingEnabled == model.CachingEnabled, "Disk caching retains its immediate toggle behavior");
        var saves = store.Saves;
        interaction.Confirmation = false;
        model.RestoreDefaultsCommand.Execute(null);
        Check(model.EpgIntervalMinutes == "90", "Declining restore preserves the draft");
        interaction.Confirmation = true;
        model.RestoreDefaultsCommand.Execute(null);
        Check(model.PreferredPlayer == PlayerKind.VLC && model.PlayerPath == "" && model.EpgIntervalMinutes == "30" &&
            model.FfmpegArguments.Contains("-c copy") && store.Saves == saves && store.Value.EpgRefreshIntervalMinutes == 720,
            "Restore defaults changes only the editor until Save is pressed");
        Check(store.CacheToggles == 1, "Restore does not accidentally toggle live caching while populating fields");
        model.Load();
        Check(model.EpgIntervalMinutes == "720" && model.PlayerPath == "player.exe" && store.CacheToggles == 1,
            "Reopening settings restores saved values without side effects");
        interaction.BrowseResult = null;
        model.BrowsePlayerCommand.Execute(null);
        Check(model.PlayerPath == "player.exe", "Canceling a file dialog leaves the draft untouched");
        interaction.BrowseResult = "new-player.exe";
        model.BrowsePlayerCommand.Execute(null);
        Check(model.PlayerPath == "new-player.exe", "The browse command writes to model state");
        model.AutoDetectPlayerCommand.Execute(null);
        model.AutoDetectFfmpegCommand.Execute(null);
        Check(model.PlayerPath == "player.exe" && model.FfmpegPath == "ffmpeg.exe", "Auto-detection updates paths through the platform service");
        model.PlayerArguments = "";
        model.TestPlayerCommand.Execute(null);
        Check(model.TestPlayerStatus.Kind == SettingsStatusKind.Success && interaction.Launches == 1 &&
            interaction.LastArguments.Contains("\"https://") && !interaction.LastArguments.Contains("\"\""),
            "Player test uses default arguments with a single layer of URL quoting");
        model.PlayerPath = "missing.exe";
        model.TestPlayerCommand.Execute(null);
        Check(model.TestPlayerStatus.Kind == SettingsStatusKind.Error && interaction.Launches == 1, "Invalid player paths never launch a process");
        model.PlayerPath = "player.exe";
        interaction.FailLaunch = true;
        model.TestPlayerCommand.Execute(null);
        Check(model.TestPlayerStatus.Kind == SettingsStatusKind.Error && model.TestPlayerStatus.Message.Contains("launch failed"),
            "Player process failures become bound validation status");
        model.UpdateEpgNowCommand.Execute(null);
        Check(store.RefreshRequests == 1 && model.LastEpgUpdate != "(never)", "EPG command delegates refresh and updates the timestamp");

        interaction.Confirmation = false;
        AwaitSettings(model.ClearCacheCommand.ExecuteAsync(null));
        Check(cache.ImageClears == 0 && cache.DataClears == 0, "Declining cache clearing leaves all caches untouched");
        interaction.Confirmation = true;
        var pending = new TaskCompletionSource();
        cache.ClearTask = pending.Task;
        var clear = model.ClearCacheCommand.ExecuteAsync(null);
        Check(model.IsClearingCache && !model.ClearCacheCommand.CanExecute(null), "An in-flight cache clear disables duplicate submissions");
        pending.SetException(new IOException("cache locked"));
        AwaitSettings(clear);
        Check(!model.IsClearingCache && model.ClearCacheCommand.CanExecute(null) && model.StatusIsError && model.StatusMessage.Contains("cache locked"),
            "A failed cache clear restores the command and surfaces its error");
        cache.ClearTask = Task.CompletedTask;
        AwaitSettings(model.ClearCacheCommand.ExecuteAsync(null));
        Check(!model.IsClearingCache && !model.StatusIsError && cache.DataClears == 2 && cache.ImageClears == 2,
            "Cache clearing can be retried successfully after failure");
    }

    private static void AwaitSettings(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private sealed class FakeSettingsStore : IApplicationSettingsService
    {
        public SettingsStore Value = new() { PlayerExePath = "player.exe", FfmpegPath = "ffmpeg.exe",
            PlayerArgsTemplate = "\"{url}\" --meta-title=\"{title}\"", FfmpegArgsTemplate = "-i {url} {output}" };
        public int Saves, CacheToggles, RefreshRequests;
        public bool FailSave;
        public SettingsStore Load() => System.Text.Json.JsonSerializer.Deserialize<SettingsStore>(System.Text.Json.JsonSerializer.Serialize(Value))!;
        public void Save(SettingsStore settings)
        {
            if (FailSave) throw new IOException("disk full");
            Saves++;
            Value = settings;
        }
        public void SetCachingEnabled(bool enabled) { CacheToggles++; Value.CachingEnabled = enabled; }
        public DateTime? LastEpgUpdateUtc { get; private set; }
        public void RequestEpgRefresh() { RefreshRequests++; LastEpgUpdateUtc = DateTime.UtcNow; }
    }

    private sealed class FakeSettingsInteraction : ISettingsInteraction
    {
        public bool Confirmation = true, FailLaunch;
        public string? BrowseResult;
        public int Launches;
        public string LastArguments = "";
        public List<(string Message, bool IsError)> Notifications = new();
        public bool FileExists(string path) => path is "player.exe" or "ffmpeg.exe" or "new-player.exe";
        public bool DirectoryExists(string path) => path == "recordings";
        public string DetectPlayer(PlayerKind kind) => "player.exe";
        public string DetectFfmpeg() => "ffmpeg.exe";
        public string? BrowseExecutable(bool ffmpeg) => BrowseResult;
        public string? BrowseDirectory(string currentPath) => BrowseResult;
        public bool Confirm(string message, string title) => Confirmation;
        public void Notify(string message, string title, bool isError = false) => Notifications.Add((message, isError));
        public bool LaunchPlayer(string path, string arguments)
        {
            if (FailLaunch) throw new IOException("launch failed");
            Launches++;
            LastArguments = arguments;
            return true;
        }
        public void OpenFfmpegDownload() { }
        public string CredentialsFolder => "fixture credentials";
        public void OpenCredentialsFolder() { }
    }

    private sealed class SettingsCache : ICacheService
    {
        public int ImageClears, DataClears;
        public Task ClearTask = Task.CompletedTask;
        public void ClearImageCache() => ImageClears++;
        public Task ClearAllDataAsync(CancellationToken token = default) { DataClears++; return ClearTask; }
        public Task<BitmapImage?> GetImageAsync(string url, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BitmapImage?> GetChannelLogoAsync(int channelId, string url, CancellationToken token = default) => throw new NotSupportedException();
        public Task<T?> GetDataAsync<T>(string key, CancellationToken token = default) where T : class => throw new NotSupportedException();
        public Task SetDataAsync<T>(string key, T data, TimeSpan? expiry = null, CancellationToken token = default) where T : class => throw new NotSupportedException();
        public Task<bool> HasDataAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveDataAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
        public Task ClearExpiredDataAsync(CancellationToken token = default) => throw new NotSupportedException();
        public int ImageCacheCount => 0;
        public int DataCacheCount => 0;
        public long EstimatedMemoryUsage => 0;
        public string CurrentCacheStatus => "";
        public event Action<string>? CacheOperationStatusChanged { add { } remove { } }
    }
}
