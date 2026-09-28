using CommunityToolkit.Mvvm.ComponentModel;

namespace DesktopApp.ViewModels;

public partial class SettingsPageViewModel : ObservableObject
{
    [ObservableProperty] private string _statusMessage = "Configure your IPTV player and recording settings";
    [ObservableProperty] private bool _statusIsError;
    public void SetStatus(string message, bool isError = false)
    {
        StatusIsError = isError;
        StatusMessage = message;
    }
}
