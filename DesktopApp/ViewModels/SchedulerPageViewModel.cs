using CommunityToolkit.Mvvm.ComponentModel;
using DesktopApp.Models;

namespace DesktopApp.ViewModels;

public partial class SchedulerPageViewModel : ObservableObject
{
    public BulkObservableCollection<Channel> Channels { get; } = new();
    [ObservableProperty] private Channel? _selectedChannel;
    [ObservableProperty] private EpgEntry? _selectedProgram;
}
