using DesktopApp.Models;

namespace DesktopApp.Services;

public interface IRecordingScheduleService
{
    Task<List<EpgEntry>> LoadProgramsAsync(Channel channel, CancellationToken cancellationToken);
    string RecordingDirectory { get; }
    string GetStreamUrl(Channel channel);
    bool HasConflict(DateTime startUtc, DateTime endUtc);
    void Schedule(ScheduledRecording recording);
}

// Adapts the existing session and process scheduler; the form never accesses either directly.
public sealed class RecordingScheduleService(IChannelService channels, RecordingScheduler scheduler) : IRecordingScheduleService
{
    public Task<List<EpgEntry>> LoadProgramsAsync(Channel channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Session.Mode == SessionMode.M3u)
        {
            var epgId = channel.EpgChannelId;
            if (string.IsNullOrWhiteSpace(epgId))
                epgId = Session.PlaylistChannels.FirstOrDefault(entry => entry.Id == channel.Id)?.TvgId;
            var programs = !string.IsNullOrWhiteSpace(epgId) &&
                Session.M3uEpgByChannel.TryGetValue(epgId, out var entries)
                ? entries.ToList() : new List<EpgEntry>();
            return Task.FromResult(programs);
        }

        // Reuse cached schedules and parsing without letting a canceled request mutate a bound channel.
        var snapshot = new Channel { Id = channel.Id, Name = channel.Name, EpgChannelId = channel.EpgChannelId,
            EpgSchedule = channel.EpgSchedule };
        return Task.Run(async () =>
        {
            await channels.LoadEpgForChannelAsync(snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.EpgSchedule?.IsStillValid() != true)
                throw new InvalidOperationException("The program guide could not be loaded. Try again.");
            return snapshot.EpgSchedule.Programs!.ToList();
        }, cancellationToken);
    }

    public string RecordingDirectory => Session.RecordingDirectory ??
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public string GetStreamUrl(Channel channel) => Session.Mode == SessionMode.M3u
        ? Session.PlaylistChannels.FirstOrDefault(entry => entry.Id == channel.Id)?.StreamUrl ?? string.Empty
        : Session.BuildStreamUrl(channel.Id, "ts");
    public bool HasConflict(DateTime startUtc, DateTime endUtc) => scheduler.HasConflictingRecording(startUtc, endUtc);
    public void Schedule(ScheduledRecording recording) => scheduler.ScheduleRecording(recording);
}
