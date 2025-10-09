using System.Windows;
using System.IO;
using DesktopApp.Configuration;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopApp;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Configure dependency injection
        _host = CreateHostBuilder().Build();

        // Initialize Session with configuration settings
        var apiSettings = _host.Services.GetRequiredService<ApiSettings>();
        var playerSettings = _host.Services.GetRequiredService<PlayerSettings>();
        var recordingSettings = _host.Services.GetRequiredService<RecordingSettings>();
        var epgSettings = _host.Services.GetRequiredService<EpgSettings>();
        var m3uSettings = _host.Services.GetRequiredService<M3uSettings>();
        var networkSettings = _host.Services.GetRequiredService<NetworkSettings>();

        Session.InitializeConfiguration(apiSettings, playerSettings, recordingSettings, epgSettings, m3uSettings, networkSettings);

        // Load settings
        SettingsStore.LoadIntoSession();

        // Start with main window
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsStore.SaveFromSession();
        _host?.Dispose();
        base.OnExit(e);
    }

    private static IHostBuilder CreateHostBuilder()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                // Get the base directory where the executable is located
                var basePath = AppDomain.CurrentDomain.BaseDirectory;

                // Build configuration from appsettings.json
                config.SetBasePath(basePath)
                      .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                // Bind and register strongly-typed configuration
                var appConfig = new AppConfiguration();
                context.Configuration.Bind(appConfig);
                services.AddSingleton(appConfig);

                // Register individual configuration sections for easier injection
                services.AddSingleton(appConfig.AppSettings);
                services.AddSingleton(appConfig.GitHub);
                services.AddSingleton(appConfig.Api);
                services.AddSingleton(appConfig.Http);
                services.AddSingleton(appConfig.Network);
                services.AddSingleton(appConfig.UI);
                services.AddSingleton(appConfig.Players);
                services.AddSingleton(appConfig.Recording);
                services.AddSingleton(appConfig.Epg);
                services.AddSingleton(appConfig.Cache);
                services.AddSingleton(appConfig.BatchProcessing);
                services.AddSingleton(appConfig.M3u);

                // Register services
                services.AddSingleton<ISessionService, SessionService>();
                services.AddSingleton<ICacheService, PersistentCacheService>();
                services.AddTransient<IChannelService, ChannelService>();
                services.AddTransient<IVodService, VodService>();
                services.AddTransient<IHttpService, HttpService>();

                // Register HTTP client with configuration
                services.AddHttpClient<IHttpService, HttpService>((serviceProvider, client) =>
                {
                    var httpSettings = serviceProvider.GetRequiredService<HttpSettings>();
                    client.Timeout = TimeSpan.FromSeconds(httpSettings.DefaultTimeoutSeconds);
                    client.DefaultRequestHeaders.Add("User-Agent", httpSettings.UserAgent);
                });

                // Register ViewModels
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<MainWindowViewModel>();

                // Register Views
                services.AddTransient<MainWindow>();
                services.AddTransient<DashboardWindow>();
                services.AddTransient<CacheInspectorWindow>();

                // Configure logging from configuration
                services.AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.AddDebug();
                    builder.SetMinimumLevel(LogLevel.Warning);
                    // Reduce HTTP client logging noise
                    builder.AddFilter("System.Net.Http", LogLevel.Warning);
                    builder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
                    // Reduce service logging noise
                    builder.AddFilter("DesktopApp.Services", LogLevel.Warning);
                });
            });
    }

    public static T GetRequiredService<T>() where T : notnull
    {
        if (Current is App app && app._host != null)
        {
            return app._host.Services.GetRequiredService<T>();
        }
        throw new InvalidOperationException("Host not initialized");
    }
}
