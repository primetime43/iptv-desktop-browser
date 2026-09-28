using CommunityToolkit.Mvvm.ComponentModel;
using DesktopApp.Models;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public partial class SchedulerPageViewModel : ObservableObject
{
    public SchedulerPageViewModel(IRecordingScheduleService service, IRecordingFormInteraction interaction, TimeProvider? clock = null)
    {
        NewRecording = new RecordingFormViewModel(service, interaction, clock);
    }

    public RecordingFormViewModel NewRecording { get; }
}
