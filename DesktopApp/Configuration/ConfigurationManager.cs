using System.IO;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace DesktopApp.Configuration;

public static class ConfigurationManager
{
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IPTV-Desktop-Browser"
    );

    private static readonly string ConfigFilePath = Path.Combine(AppDataPath, "appsettings.json");

    public static string GetConfigurationPath()
    {
        EnsureConfigurationExists();
        return ConfigFilePath;
    }

    private static void EnsureConfigurationExists()
    {
        try
        {
            // Create AppData directory if it doesn't exist
            if (!Directory.Exists(AppDataPath))
            {
                Directory.CreateDirectory(AppDataPath);
                Log.Information("Created AppData directory: {Path}", AppDataPath);
            }

            var currentVersion = GetAssemblyVersion();

            // Check if config file exists
            if (!File.Exists(ConfigFilePath))
            {
                Log.Information("Configuration file not found, creating default at: {Path}", ConfigFilePath);
                CreateDefaultConfiguration(currentVersion);
            }
            else
            {
                // Check if version needs updating
                UpdateConfigurationVersion(currentVersion);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to ensure configuration exists");
        }
    }

    private static void CreateDefaultConfiguration(string version)
    {
        var defaultConfig = new AppConfiguration
        {
            AppSettings = new AppSettings
            {
                ApplicationName = "IPTV Desktop Browser",
                Version = version
            },
            GitHub = new GitHubSettings
            {
                RepositoryOwner = "primetime43",
                RepositoryName = "iptv-desktop-browser",
                CheckForUpdates = true
            },
            Api = new ApiSettings
            {
                Endpoints = new ApiEndpoints
                {
                    PlayerApi = "player_api.php",
                    PanelApi = "panel_api.php",
                    GetPlaylist = "get.php"
                },
                Actions = new ApiActions
                {
                    GetLiveCategories = "get_live_categories",
                    GetLiveStreams = "get_live_streams",
                    GetSimpleDataTable = "get_simple_data_table",
                    GetVodCategories = "get_vod_categories",
                    GetVodStreams = "get_vod_streams"
                },
                StreamPaths = new StreamPaths
                {
                    Live = "live",
                    Movie = "movie",
                    Series = "series"
                },
                DefaultExtensions = new DefaultExtensions
                {
                    LiveStream = "ts",
                    VodStream = "mp4",
                    SeriesStream = "mp4"
                }
            },
            Http = new HttpSettings
            {
                DefaultTimeoutSeconds = 30,
                UserAgent = $"IPTV-Desktop-Browser/{version}",
                Timeouts = new TimeoutSettings
                {
                    LoginRequestSeconds = 12,
                    M3uLoadSeconds = 30,
                    XmltvLoadSeconds = 40
                }
            },
            Network = new NetworkSettings
            {
                DefaultPorts = new DefaultPorts
                {
                    Http = 80,
                    Https = 443
                },
                Schemes = new Schemes
                {
                    Http = "http",
                    Https = "https"
                }
            },
            UI = new UISettings
            {
                Colors = new UIColors
                {
                    Info = new ColorRgb { R = 108, G = 130, B = 152 },
                    Success = new ColorRgb { R = 76, G = 209, B = 100 },
                    Error = new ColorRgb { R = 229, G = 96, B = 96 }
                },
                Text = new UIText
                {
                    XtreamMode = new ModeText
                    {
                        Header = "Xtream Login",
                        HelpHeader = "What is Xtream Codes?",
                        HelpText = "Enter the portal URL (or IP), your account username & password provided by your IPTV provider. SSL will prefix https:// and default port 443 if selected."
                    },
                    M3uMode = new ModeText
                    {
                        Header = "M3U Playlist",
                        HelpHeader = "What is M3U?",
                        HelpText = "Provide a remote URL or local file path to an .m3u / .m3u8 playlist. Optionally supply XMLTV URL for EPG."
                    }
                }
            },
            Players = new PlayerSettings
            {
                VLC = new PlayerConfig
                {
                    DefaultExecutable = "vlc",
                    DefaultArguments = "\"{url}\" --meta-title=\"{title}\""
                },
                MPCHC = new PlayerConfig
                {
                    DefaultExecutable = "mpc-hc64.exe",
                    DefaultArguments = "\"{url}\" /play"
                },
                MPV = new PlayerConfig
                {
                    DefaultExecutable = "mpv",
                    DefaultArguments = "--force-media-title=\"{title}\" \"{url}\""
                },
                Custom = new PlayerConfig
                {
                    DefaultExecutable = "",
                    DefaultArguments = "\"{url}\""
                }
            },
            Recording = new RecordingSettings
            {
                FFmpeg = new FFmpegSettings
                {
                    DefaultArguments = "-i \"{url}\" -c copy -f mpegts \"{output}\""
                }
            },
            Epg = new EpgSettings
            {
                RefreshIntervalMinutes = 30,
                MinCacheValidityMinutes = 30,
                MaxCacheValidityHours = 24,
                SmartCacheExpirationMinutesBeforeShowEnd = 5
            },
            Cache = new CacheSettings
            {
                Enabled = false,
                Durations = new CacheDurations
                {
                    CategoriesHours = 2,
                    ChannelsMinutes = 30,
                    EpgMinimumMinutes = 30,
                    EpgMaximumHours = 24
                },
                KeyPrefixes = new CacheKeyPrefixes
                {
                    LiveCategories = "live_categories",
                    Channels = "channels",
                    Epg = "epg",
                    VodCategories = "vod_categories",
                    VodStreams = "vod_streams"
                }
            },
            BatchProcessing = new BatchProcessingSettings
            {
                EpgBatchSize = 10,
                DelayBetweenBatchesMilliseconds = 100
            },
            M3u = new M3uSettings
            {
                DefaultCategories = new M3uDefaultCategories
                {
                    AllId = "all",
                    AllName = "All Channels"
                },
                SessionKeyPrefix = "m3u_"
            }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(defaultConfig, options);
        File.WriteAllText(ConfigFilePath, json);

        Log.Information("Created default configuration file with version {Version}", version);
    }

    private static void UpdateConfigurationVersion(string currentVersion)
    {
        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfiguration>(json);

            if (config?.AppSettings?.Version != currentVersion)
            {
                Log.Information("Updating configuration version from {OldVersion} to {NewVersion}",
                    config?.AppSettings?.Version ?? "unknown", currentVersion);

                if (config != null && config.AppSettings != null)
                {
                    config.AppSettings.Version = currentVersion;

                    // Also update UserAgent if it exists
                    if (config.Http != null)
                    {
                        config.Http.UserAgent = $"IPTV-Desktop-Browser/{currentVersion}";
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                    var updatedJson = JsonSerializer.Serialize(config, options);
                    File.WriteAllText(ConfigFilePath, updatedJson);

                    Log.Information("Configuration version updated successfully");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update configuration version, will use existing file");
        }
    }

    private static string GetAssemblyVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "2.1.0";
    }
}
