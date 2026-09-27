using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DesktopApp.Models;
using DesktopApp.Security;

internal static class RecordingMutationChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application();
            try
            {
                Run(root, app, check);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { app.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void Run(string root, Application app, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "mutations");
        Session.Username = "edit-owner";
        var owner = Key();
        var scheduler = new RecordingScheduler(directory);
        try
        {
            var original = Recording("Original");
            scheduler.ScheduleRecording(original);
            // Seed a second account to check that a visible-list edit preserves it.
            Session.Username = "other-edit-owner";
            scheduler.ReloadForCurrentSession();
            var otherOwner = Key();
            var other = Recording("Other account");
            scheduler.ScheduleRecording(other);
            Session.Username = "edit-owner";
            scheduler.ReloadForCurrentSession();

            var edited = Recording("Edited title");
            edited.Id = original.Id;
            edited.StartTime = original.StartTime.AddHours(1);
            edited.EndTime = original.EndTime.AddHours(1);
            edited.PreBufferMinutes = 3;
            edited.PostBufferMinutes = 7;
            scheduler.UpdateRecording(edited);
            // No dispatcher pumping, timer tick, Dispose, or reload save before reading.
            var stored = Read()[owner].Single();
            check(stored.Title == edited.Title && stored.StartTime == edited.StartTime &&
                stored.EndTime == edited.EndTime && stored.PreBufferMinutes == 3 && stored.PostBufferMinutes == 7,
                "Edit is persisted before returning without pumping the dispatcher");
            check(ReferenceEquals(scheduler.ScheduledRecordings.Single(), edited), "Edit updates the bound collection immediately");
            check(Read()[otherOwner].Single().Id == other.Id, "Edit preserves another account's schedule");
            var reopened = new RecordingScheduler(directory);
            check(reopened.ScheduledRecordings.Single().Title == edited.Title, "Immediate reopen sees the edited recording");
            reopened.Dispose();

            scheduler.CancelRecording(edited.Id);
            check(Read()[owner].Single().Status == RecordingScheduleStatus.Cancelled,
                "Pending cancellation is persisted before returning");
            var forbiddenEdit = Recording("Must not replace cancelled recording");
            forbiddenEdit.Id = edited.Id;
            scheduler.UpdateRecording(forbiddenEdit);
            check(Read()[owner].Single().Status == RecordingScheduleStatus.Cancelled &&
                Read()[owner].Single().Title == edited.Title, "Cancelled recordings remain uneditable");

            var completed = Recording("Completed", RecordingScheduleStatus.Completed);
            var failed = Recording("Failed", RecordingScheduleStatus.Failed);
            var pending = Recording("Keep pending");
            var missed = Recording("Keep missed", RecordingScheduleStatus.Missed);
            foreach (var item in new[] { completed, failed, pending, missed }) scheduler.ScheduleRecording(item);
            scheduler.DeleteCompletedRecordings();
            var expectedIds = new[] { pending.Id, missed.Id }.Order().ToArray();
            check(Read()[owner].Select(r => r.Id).Order().SequenceEqual(expectedIds),
                "Delete persists removal of Completed, Failed and Cancelled records immediately");
            check(scheduler.ScheduledRecordings.Select(r => r.Id).Order().SequenceEqual(expectedIds),
                "Delete updates the bound collection before returning");
            check(Read()[otherOwner].Single().Id == other.Id, "Delete preserves another account's schedule");
            reopened = new RecordingScheduler(directory);
            check(reopened.ScheduledRecordings.Select(r => r.Id).Order().SequenceEqual(expectedIds),
                "Immediate reopen does not restore deleted recordings");
            reopened.Dispose();

            // Worker callers must marshal the complete operation to the UI before returning.
            var workerEdit = Recording("Worker edit");
            workerEdit.Id = pending.Id;
            scheduler.ScheduleRecording(Recording("Delete from worker", RecordingScheduleStatus.Completed));
            var notificationsOnUi = true;
            var changes = 0;
            scheduler.ScheduledRecordings.CollectionChanged += (_, _) =>
            {
                notificationsOnUi &= app.Dispatcher.CheckAccess();
                changes++;
            };
            var frame = new DispatcherFrame();
            var worker = Task.Run(() =>
            {
                scheduler.UpdateRecording(workerEdit);
                check(Read()[owner].Single(r => r.Id == pending.Id).Title == "Worker edit",
                    "Worker edit returns only after persistence");
                scheduler.DeleteCompletedRecordings();
                check(Read()[owner].Select(r => r.Id).Order().SequenceEqual(expectedIds),
                    "Worker delete returns only after persistence");
            });
            _ = worker.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            worker.GetAwaiter().GetResult();
            check(notificationsOnUi && changes == 2, "Worker edits and deletes notify collections on the UI thread");
        }
        finally { scheduler.Dispose(); }

        Dictionary<string, List<ScheduledRecording>> Read() =>
            JsonSerializer.Deserialize<Dictionary<string, List<ScheduledRecording>>>(
                ProtectedRecordingFile.ReadAllText(Path.Combine(directory, "scheduled_recordings.json")))!;
    }

    private static string Key() => $"{Environment.UserName}_{Session.Host}_{Session.Port}_{Session.Username}";

    private static ScheduledRecording Recording(string title, RecordingScheduleStatus status = RecordingScheduleStatus.Scheduled) => new()
    {
        Title = title,
        StartTime = DateTime.UtcNow.AddHours(2),
        EndTime = DateTime.UtcNow.AddHours(3),
        OutputFilePath = "unused.ts",
        Status = status
    };
}
