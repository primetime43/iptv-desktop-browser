using CommunityToolkit.Mvvm.ComponentModel;
using DesktopApp.Models;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public partial class SchedulerPageViewModel : ObservableObject, IDisposable
{
    public SchedulerPageViewModel(RecordingFormViewModel newRecording, RecordingManagementViewModel recordings)
    {
        NewRecording = newRecording;
        Recordings = recordings;
    }

    public RecordingFormViewModel NewRecording { get; }
    public RecordingManagementViewModel Recordings { get; }

    public void Dispose()
    {
        NewRecording.Deactivate();
        Recordings.Dispose();
    }
}
