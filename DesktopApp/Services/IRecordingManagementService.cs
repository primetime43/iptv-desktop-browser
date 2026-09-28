using System.Collections.ObjectModel;
using DesktopApp.Models;

namespace DesktopApp.Services;

public interface IRecordingManagementService
{
    ReadOnlyObservableCollection<ScheduledRecording> Recordings { get; }
    string RecordingDirectory { get; }
    bool Update(ScheduledRecording recording);
    void Cancel(Guid id);
    void DeleteCompleted();
}

public sealed class RecordingManagementService : IRecordingManagementService
{
    private readonly RecordingScheduler _scheduler;
    public RecordingManagementService(RecordingScheduler scheduler)
    {
        _scheduler = scheduler;
        Recordings = new ReadOnlyObservableCollection<ScheduledRecording>(scheduler.ScheduledRecordings);
    }
    public ReadOnlyObservableCollection<ScheduledRecording> Recordings { get; }
    public string RecordingDirectory => string.IsNullOrWhiteSpace(Session.RecordingDirectory)
        ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) : Session.RecordingDirectory;
    public bool Update(ScheduledRecording recording)
    {
        _scheduler.UpdateRecording(recording);
        // The scheduler rechecks status under its lock; a recording may start while an editor is open.
        return _scheduler.ScheduledRecordings.Contains(recording);
    }
    public void Cancel(Guid id) => _scheduler.CancelRecording(id);
    public void DeleteCompleted() => _scheduler.DeleteCompletedRecordings();
}
