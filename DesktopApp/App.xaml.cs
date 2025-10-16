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
using Serilog;
using System.Windows.Threading;

namespace DesktopApp;

public partial class App : Application
{
    private IHost? _host;

    public App()
    {
        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            // Initialize logging first
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IPTV-Desktop-Browser",
                "Logs",
                "app-.log"
            );

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();

            Log.Information("=== Application Starting ===");
            Log.Information("Version: {Version}", "2.1.1");
            Log.Information("OS: {OS}", Environment.OSVersion);
            Log.Information(".NET Version: {DotNetVersion}", Environment.Version);

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

            Log.Information("Configuration loaded successfully");

            // Start with main window
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            Log.Information("Main window displayed");

            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error during application startup");
            MessageBox.Show(
                $"A fatal error occurred during startup:\n\n{ex.Message}\n\nPlease check the log file at:\n{GetLogDirectory()}",
                "Application Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        Log.Fatal(exception, "Unhandled exception in AppDomain");

        if (e.IsTerminating)
        {
            MessageBox.Show(
                $"A fatal error occurred:\n\n{exception?.Message}\n\nThe application will now close.\n\nLog location: {GetLogDirectory()}",
                "Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Log.CloseAndFlush();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled dispatcher exception");

        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}\n\nLog location: {GetLogDirectory()}",
            "Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );

        // Mark as handled to prevent application crash
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private static string GetLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IPTV-Desktop-Browser",
            "Logs"
        );
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("Application shutting down");
            SettingsStore.SaveFromSession();
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during application shutdown");
        }
        finally
        {
            Log.CloseAndFlush();
        }

        base.OnExit(e);
    }

    private static IHostBuilder CreateHostBuilder()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                // Get configuration path from AppData (auto-creates if missing)
                var configPath = Configuration.ConfigurationManager.GetConfigurationPath();
                var configDir = Path.GetDirectoryName(configPath) ?? AppDomain.CurrentDomain.BaseDirectory;

                Log.Information("Loading configuration from: {ConfigPath}", configPath);

                // Build configuration from appsettings.json in AppData
                config.SetBasePath(configDir)
                      .AddJsonFile(Path.GetFileName(configPath), optional: true, reloadOnChange: true);

                if (File.Exists(configPath))
                {
                    Log.Information("Configuration file loaded successfully from AppData");
                }
                else
                {
                    Log.Warning("Configuration file not found, using default values");
                }
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

                // Configure logging with Serilog
                services.AddLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.AddSerilog(dispose: true);
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
