using System;
using System.IO;
using System.Text.Json;

namespace DesktopApp.Models;

public sealed class SettingsStore
{
    public PlayerKind PreferredPlayer { get; set; } = PlayerKind.VLC;
    public string? PlayerExePath { get; set; }
    public string? PlayerArgsTemplate { get; set; }
    public string? FfmpegPath { get; set; }
    public string? RecordingDirectory { get; set; }
    public string? FfmpegArgsTemplate { get; set; }
    public int EpgRefreshIntervalMinutes { get; set; } = 30;
    public bool CachingEnabled { get; set; } = true;

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true }; 
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IPTVDashboard");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static void LoadIntoSession()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<SettingsStore>(json, _jsonOptions);
            if (data == null) return;
            Session.PreferredPlayer = data.PreferredPlayer;
            Session.PlayerExePath = data.PlayerExePath;
            if (!string.IsNullOrWhiteSpace(data.PlayerArgsTemplate)) Session.PlayerArgsTemplate = data.PlayerArgsTemplate;
            Session.FfmpegPath = data.FfmpegPath;
            Session.RecordingDirectory = data.RecordingDirectory;
            if (!string.IsNullOrWhiteSpace(data.FfmpegArgsTemplate)) Session.FfmpegArgsTemplate = data.FfmpegArgsTemplate;
            if (data.EpgRefreshIntervalMinutes > 0 && data.EpgRefreshIntervalMinutes <= 720)
                Session.EpgRefreshInterval = TimeSpan.FromMinutes(data.EpgRefreshIntervalMinutes);
            Session.CachingEnabled = data.CachingEnabled;
        }
        catch { }
    }

    public static void SaveFromSession()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var data = new SettingsStore
            {
                PreferredPlayer = Session.PreferredPlayer,
                PlayerExePath = Session.PlayerExePath,
                PlayerArgsTemplate = string.IsNullOrWhiteSpace(Session.PlayerArgsTemplate) ? null : Session.PlayerArgsTemplate,
                FfmpegPath = Session.FfmpegPath,
                RecordingDirectory = Session.RecordingDirectory,
                FfmpegArgsTemplate = string.IsNullOrWhiteSpace(Session.FfmpegArgsTemplate) ? null : Session.FfmpegArgsTemplate,
                EpgRefreshIntervalMinutes = (int)Session.EpgRefreshInterval.TotalMinutes,
                CachingEnabled = Session.CachingEnabled
            };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}
