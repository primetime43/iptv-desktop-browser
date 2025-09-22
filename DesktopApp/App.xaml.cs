using System.Windows;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views;
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
            .ConfigureServices((context, services) =>
            {
                // Register services
                services.AddSingleton<ISessionService, SessionService>();
                services.AddSingleton<ICacheService, PersistentCacheService>();
                services.AddTransient<IChannelService, ChannelService>();
                services.AddTransient<IVodService, VodService>();
                services.AddTransient<IHttpService, HttpService>();

                // Register HTTP client
                services.AddHttpClient<IHttpService, HttpService>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    client.DefaultRequestHeaders.Add("User-Agent", "IPTV-Desktop-Browser/1.0.5");
                });

                // Register ViewModels
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<MainWindowViewModel>();

                // Register Views
                services.AddTransient<MainWindow>();
                services.AddTransient<DashboardWindow>();
                services.AddTransient<CacheInspectorWindow>();

                // Configure logging
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
