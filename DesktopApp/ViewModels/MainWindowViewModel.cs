using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;
using Microsoft.Extensions.Logging;

namespace DesktopApp.ViewModels;

public partial class MainWindowViewModel : BaseViewModel
{
    private readonly ISessionService _sessionService;
    private readonly ILogger<MainWindowViewModel> _logger;

    public MainWindowViewModel(
        ISessionService sessionService,
        ILogger<MainWindowViewModel> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port = 80;

    [ObservableProperty]
    private bool _useSsl;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private SessionMode _sessionMode = SessionMode.Xtream;

    [ObservableProperty]
    private string _m3uUrl = string.Empty;

    [ObservableProperty]
    private string _xmltvUrl = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await ExecuteAsync(async cancellationToken =>
        {
            _logger.LogInformation("Attempting to connect to IPTV service");

            // Update session service with connection details
            _sessionService.Mode = SessionMode;
            _sessionService.Host = Host;
            _sessionService.Port = Port;
            _sessionService.UseSsl = UseSsl;
            _sessionService.Username = Username;
            _sessionService.Password = Password;

            if (SessionMode == SessionMode.Xtream)
            {
                await ConnectXtreamAsync(cancellationToken);
            }
            else
            {
                await ConnectM3uAsync(cancellationToken);
            }

            StatusMessage = "Connected successfully";
            _logger.LogInformation("Successfully connected to IPTV service");

        }, "Connecting...");
    }

    [RelayCommand]
    private void LoadM3uFile()
    {
        // This would open a file dialog and load M3U file
        // Implementation would be moved from MainWindow code-behind
    }

    [RelayCommand]
    private void OpenCredentialManager()
    {
        // This would open the credential manager window
        // Implementation would be moved from MainWindow code-behind
    }

    [RelayCommand]
    private void OpenSettings()
    {
        // This would open the settings window
        // Implementation would be moved from MainWindow code-behind
    }

    private async Task ConnectXtreamAsync(CancellationToken cancellationToken)
    {
        // Validate connection by testing player_api
        var testUrl = _sessionService.BuildApi("get_live_categories");

        // This would be implemented using the HTTP service
        // var response = await _httpService.GetStringAsync(testUrl, cancellationToken);

        // Parse and validate response
        // If successful, the connection is established
    }

    private async Task ConnectM3uAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(M3uUrl))
        {
            throw new InvalidOperationException("M3U URL is required");
        }

        // Load M3U playlist
        // This would be implemented using the HTTP service
        // var m3uContent = await _httpService.GetStringAsync(M3uUrl, cancellationToken);

        // Parse M3U content and populate session
        // If XMLTV URL is provided, also load EPG data
    }

    partial void OnUseSslChanged(bool value)
    {
        // Update default port based on SSL setting
        if (Port == 80 || Port == 443)
        {
            Port = value ? 443 : 80;
        }
    }
}