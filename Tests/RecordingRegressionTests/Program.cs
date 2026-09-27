using System.IO;
using System.Text.Json;
using DesktopApp.Models;
using DesktopApp.Security;

// This executable doubles as a controlled FFmpeg stand-in. The scheduler uses
// actual OS processes, redirected streams, exit codes, and its production storage.
if (args.Length > 0 && args[0] == "--fake-ffmpeg")
{
    switch (args[1])
    {
        case "fail":
            for (var i = 0; i < 30; i++)
                Console.Error.WriteLine($"Diagnostic {i}: " + new string('x', 800));
            Console.Error.WriteLine("Cannot open https://example.test/live/test-user/test-password/1.ts");
            Console.Error.WriteLine("Disk full: could not write output");
            return 7;
        case "early-zero":
            return 0;
        case "ignore-stop":
            await Task.Delay(TimeSpan.FromMinutes(1));
            return 0;
        default:
            var command = Console.ReadLine();
            if (args[1] == "fail-on-stop")
            {
                Console.Error.WriteLine("Error finalizing output");
                return 9;
            }
            return command is "q" or "exit0" ? 0 : 8;
    }
}

var root = Path.Combine(Path.GetTempPath(), "iptv-recording-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var runs = new List<TestRun>();
var passed = 0;
Session.Mode = SessionMode.Xtream;
Session.Host = "example.test";
Session.Username = "test-user";
Session.Password = "test-password";
try
{
    var failed = Begin("fail");
    await finished(failed);
    Check(failed.Recording.Status == RecordingScheduleStatus.Failed && failed.Recording.ExitCode == 7,
        "Nonzero exit immediately becomes Failed");
    Check(failed.Recording.FailureReason?.Contains("Disk full") == true, "Final stderr reaches failure details");
    Check(failed.Recording.FailureReason!.Length < 4500, "Stored stderr is bounded");
    Check(!failed.Recording.FailureReason.Contains("test-password") && !failed.Recording.FailureReason.Contains("test-user"),
        "Failure details redact stream credentials");
    Check(failed.Recording.RecordingProcess == null && failed.FailedCount == 1 && failed.StoppedCount == 0,
        "Failure releases process and sends one failure event");
    failed.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    failed.Scheduler.CheckScheduledRecordings();
    failed.Scheduler.CancelRecording(failed.Recording.Id);
    Check(failed.Recording.Status == RecordingScheduleStatus.Failed && failed.FailedCount == 1,
        "Later timer ticks and cancellation cannot overwrite failure");
    var stored = ReadStored(failed);
    Check(stored.Status == RecordingScheduleStatus.Failed && stored.ExitCode == 7 && stored.FailureReason == failed.Recording.FailureReason,
        "Failure is persisted before notification");
    var reloaded = new RecordingScheduler(failed.Directory);
    Check(reloaded.ScheduledRecordings.Single().FailureReason == failed.Recording.FailureReason,
        "Failed recording remains available after reload");
    reloaded.Dispose();

    var earlyZero = Begin("early-zero");
    await finished(earlyZero);
    Check(earlyZero.Recording.Status == RecordingScheduleStatus.Failed && earlyZero.Recording.ExitCode == 0 &&
        earlyZero.Recording.FailureReason!.Contains("before the scheduled end"), "Early zero exit is incomplete, not successful");

    var clean = Begin("wait");
    await clean.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    clean.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    clean.Scheduler.CheckScheduledRecordings();
    clean.Scheduler.CheckScheduledRecordings();
    await finished(clean);
    Check(clean.Recording.Status == RecordingScheduleStatus.Completed && clean.Recording.ExitCode == 0 && clean.Recording.FailureReason == null,
        "Scheduled stop uses stdin q and accepts a clean exit");
    Check(clean.StoppedCount == 1 && clean.FailedCount == 0 && clean.StartedCount == 1, "Repeated ticks do not duplicate completion");

    var badStop = Begin("fail-on-stop");
    await badStop.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    badStop.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    badStop.Scheduler.CheckScheduledRecordings();
    await finished(badStop);
    Check(badStop.Recording.Status == RecordingScheduleStatus.Failed && badStop.Recording.ExitCode == 9 &&
        badStop.Recording.FailureReason!.Contains("finalizing"), "Nonzero exit during scheduled stop remains a failure");

    var forced = Begin("ignore-stop");
    await forced.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    forced.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    forced.Scheduler.CheckScheduledRecordings();
    await finished(forced);
    Check(forced.Recording.Status == RecordingScheduleStatus.Failed && forced.Recording.FailureReason!.Contains("terminated"),
        "Forced termination cannot report success");

    var cancel = Begin("wait");
    await cancel.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cancel.Scheduler.CancelRecording(cancel.Recording.Id);
    await finished(cancel);
    Check(cancel.Recording.Status == RecordingScheduleStatus.Cancelled && cancel.FailedCount == 0 && cancel.StoppedCount == 1,
        "User cancellation is distinct from failure");
    Check(ReadStored(cancel).Status == RecordingScheduleStatus.Cancelled, "Final cancellation is persisted");

    var natural = Begin("wait");
    await natural.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    natural.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    natural.Recording.RecordingProcess!.StandardInput.WriteLine("exit0");
    natural.Recording.RecordingProcess.StandardInput.Flush();
    await finished(natural);
    Check(natural.Recording.Status == RecordingScheduleStatus.Completed && natural.Recording.ExitCode == 0,
        "Natural clean exit after scheduled end succeeds");

    var missing = Begin("wait", missingExecutable: true);
    await finished(missing);
    Check(missing.Recording.Status == RecordingScheduleStatus.Failed && missing.Recording.ExitCode == null &&
        missing.Recording.FailureReason!.Contains("FFmpeg path"), "Launch failure has actionable details and no fabricated exit code");

    // Keep the worker in the Started callback until its child has already exited.
    // Then deliver the timer tick before the exit monitor can publish its result.
    var race = Begin("fail", beforeStarted: run =>
    {
        if (!run.Recording.RecordingProcess!.WaitForExit(10000)) throw new TimeoutException("Child did not exit");
        run.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
        run.Scheduler.CheckScheduledRecordings();
        run.Scheduler.CancelRecording(run.Recording.Id);
    });
    await finished(race);
    Check(race.Recording.Status == RecordingScheduleStatus.Failed && race.Recording.ExitCode == 7,
        "Exit racing with scheduled stop and cancellation preserves failure");

    var delayedMonitor = Begin("early-zero", beforeStarted: run =>
    {
        if (!run.Recording.RecordingProcess!.WaitForExit(10000)) throw new TimeoutException("Child did not exit");
        run.Recording.EndTime = DateTime.UtcNow.AddMilliseconds(100);
        Thread.Sleep(200);
    });
    await finished(delayedMonitor);
    Check(delayedMonitor.Recording.Status == RecordingScheduleStatus.Failed,
        "Actual process exit time determines early exit even when notification is delayed");

    // Recover a persisted Recording state without a managed live process.
    var interrupted = ReadStored(failed);
    interrupted.Status = RecordingScheduleStatus.Recording;
    interrupted.EndTime = DateTime.UtcNow.AddMinutes(10);
    var interruptedPath = Path.Combine(root, "interrupted");
    Directory.CreateDirectory(interruptedPath);
    ProtectedRecordingFile.WriteAllText(Path.Combine(interruptedPath, "scheduled_recordings.json"),
        JsonSerializer.Serialize(new Dictionary<string, List<ScheduledRecording>>
        {
            [$"{Environment.UserName}_{Session.Host}_{Session.Port}_{Session.Username}"] = [interrupted]
        }));
    var recovered = new RecordingScheduler(interruptedPath);
    Check(recovered.ScheduledRecordings.Single().Status == RecordingScheduleStatus.Failed &&
        recovered.ScheduledRecordings.Single().FailureReason!.Contains("could not be verified"), "Interrupted recordings cannot become false successes");
    recovered.Dispose();

    Session.Username = "account-a";
    Session.Password = "password-a";
    var accountA = Begin("wait");
    await accountA.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var keyA = SessionKey();
    var processA = accountA.Recording.RecordingProcess ?? throw new InvalidOperationException("A did not start");
    var pendingA = new ScheduledRecording
    {
        Title = "Later on A", StartTime = DateTime.UtcNow.AddMinutes(10),
        EndTime = DateTime.UtcNow.AddMinutes(20), OutputFilePath = Path.Combine(accountA.Directory, "later.ts")
    };
    accountA.Scheduler.ScheduleRecording(pendingA);
    var seriesA = new SeriesRecording { SeriesName = "A series" };
    accountA.Scheduler.AddSeriesRecording(seriesA);

    // Logout mutates Session before the next dashboard calls ReloadForCurrentSession.
    Session.Username = Session.Password = "";
    pendingA.StartTime = DateTime.UtcNow.AddSeconds(-1);
    accountA.Scheduler.CheckScheduledRecordings();
    Check(ReadAccounts(accountA).Keys.SequenceEqual([keyA]), "Logout tick does not save A's schedules under an empty username");
    Check(pendingA.Status == RecordingScheduleStatus.Scheduled && pendingA.RecordingProcess == null,
        "Pending work does not start with another login's settings");
    pendingA.StartTime = DateTime.UtcNow.AddMinutes(10);

    Session.Username = "account-b";
    Session.Password = "password-b";
    var keyB = SessionKey();
    accountA.Scheduler.CheckScheduledRecordings();
    Check(!ReadAccounts(accountA).ContainsKey(keyB), "Tick during login keeps the outgoing account's ownership");
    accountA.Scheduler.ReloadForCurrentSession();
    Check(accountA.Scheduler.ScheduledRecordings.Count == 0 && accountA.Scheduler.SeriesRecordings.Count == 0,
        "Switching accounts displays only the new account's schedules and series");

    // Deliberately reuse a GUID across accounts to check account-scoped process identity.
    var accountB = Begin("wait", sharedStore: accountA, recordingId: accountA.Recording.Id);
    await accountB.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(accountA.Recording.RecordingProcess != accountB.Recording.RecordingProcess && !processA.HasExited,
        "Both accounts can retain independent active processes");

    Session.Username = "account-a";
    Session.Password = "password-a";
    accountA.Scheduler.ReloadForCurrentSession();
    Check(ReferenceEquals(accountA.Scheduler.ScheduledRecordings.Single(r => r.Id == accountA.Recording.Id), accountA.Recording) &&
        ReferenceEquals(accountA.Recording.RecordingProcess, processA) && accountA.StartedCount == 1,
        "Returning to A reattaches the existing run without restarting or marking it interrupted");
    Check(accountA.Scheduler.SeriesRecordings.Single().Id == seriesA.Id, "Series rules retain their original account");

    Session.Username = "account-b";
    Session.Password = "password-b";
    accountA.Scheduler.ReloadForCurrentSession();
    accountA.Recording.PostBufferMinutes = 1;
    accountA.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    accountA.Scheduler.CheckScheduledRecordings();
    Check(accountA.Recording.Status == RecordingScheduleStatus.Recording && !processA.HasExited,
        "Hidden recording still honors its post-buffer");
    accountA.Recording.EndTime = DateTime.UtcNow.AddMinutes(-2);
    accountA.Scheduler.CheckScheduledRecordings();
    await finished(accountA);
    Check(accountA.Recording.Status == RecordingScheduleStatus.Completed && accountA.Recording.RecordingProcess == null,
        "A reaches its scheduled stop while B is visible");
    Check(accountB.Recording.Status == RecordingScheduleStatus.Recording && !accountB.Recording.RecordingProcess!.HasExited,
        "Stopping A leaves B's recording running");
    var accounts = ReadAccounts(accountA);
    Check(accounts[keyA].Single(r => r.Id == accountA.Recording.Id).Status == RecordingScheduleStatus.Completed &&
        accounts[keyA].Any(r => r.Id == pendingA.Id) && accounts[keyB].Single().Status == RecordingScheduleStatus.Recording,
        "Background completion updates only its owner and preserves unrelated schedules");
    accountB.Scheduler.CancelRecording(accountB.Recording.Id);
    await finished(accountB);
    Check(ReadAccounts(accountA)[keyB].Single().Status == RecordingScheduleStatus.Cancelled &&
        ReadAccounts(accountA)[keyA].Single(r => r.Id == accountA.Recording.Id).Status == RecordingScheduleStatus.Completed,
        "Cancelling a shared GUID affects only the visible account");

    Session.Username = "failure-owner";
    var backgroundFailure = Begin("fail-on-stop");
    await backgroundFailure.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var failureOwner = SessionKey();
    Session.Username = "other-viewer";
    backgroundFailure.Scheduler.ReloadForCurrentSession();
    backgroundFailure.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    backgroundFailure.Scheduler.CheckScheduledRecordings();
    await finished(backgroundFailure);
    Check(ReadAccounts(backgroundFailure)[failureOwner].Single().Status == RecordingScheduleStatus.Failed &&
        backgroundFailure.Scheduler.ScheduledRecordings.Count == 0,
        "Background failures persist under their owner without entering another account's list");
    Session.Username = "failure-owner";
    backgroundFailure.Scheduler.ReloadForCurrentSession();
    Check(backgroundFailure.Scheduler.ScheduledRecordings.Single().ExitCode == 9,
        "Returning to the owner displays the background failure details");

    Session.Username = "logged-out-owner";
    var loggedOut = Begin("wait");
    await loggedOut.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var loggedOutOwner = SessionKey();
    Session.Username = Session.Password = "";
    loggedOut.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    loggedOut.Scheduler.CheckScheduledRecordings();
    await finished(loggedOut);
    Check(ReadAccounts(loggedOut).Keys.SequenceEqual([loggedOutOwner]) && loggedOut.Recording.Status == RecordingScheduleStatus.Completed,
        "Recording stops and saves to its owner while logged out");

    Session.Username = "queued-owner";
    Session.Password = "queued-password";
    var queuedOwner = SessionKey();
    var queued = Begin("wait", whenQueued: () =>
    {
        // This runs before Task.Run: reproduce a switch before process startup.
        Session.Username = "next-account";
        Session.Password = "next-password";
        Session.FfmpegPath = Path.Combine(root, "missing-after-switch.exe");
        Session.FfmpegArgsTemplate = "--fake-ffmpeg fail";
    });
    await queued.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    queued.Scheduler.ReloadForCurrentSession();
    queued.Recording.EndTime = DateTime.UtcNow.AddSeconds(-1);
    queued.Scheduler.CheckScheduledRecordings();
    await finished(queued);
    Check(ReadAccounts(queued)[queuedOwner].Single().Status == RecordingScheduleStatus.Completed,
        "Queued runs retain their original account and FFmpeg settings across startup races");

    await RecordingMutationChecks.RunAsync(root, Check);

    Console.WriteLine($"Passed {passed} recording regression checks.");
    return 0;
}
finally
{
    foreach (var run in runs)
    {
        try { if (run.Recording.RecordingProcess is { HasExited: false } process) { process.Kill(true); process.WaitForExit(10000); } }
        catch (InvalidOperationException) { }
        run.Scheduler.Dispose();
    }
    // All storage is beneath this run's newly created temp directory.
    foreach (var directory in Directory.EnumerateDirectories(root))
    {
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
    Directory.Delete(root);
}

TestRun Begin(string mode, bool missingExecutable = false, Action<TestRun>? beforeStarted = null,
    TestRun? sharedStore = null, Guid? recordingId = null, Action? whenQueued = null)
{
    var directory = sharedStore?.Directory ?? Path.Combine(root, Guid.NewGuid().ToString("N"));
    var run = new TestRun(sharedStore?.Scheduler ?? new RecordingScheduler(directory), new ScheduledRecording
    {
        Id = recordingId ?? Guid.NewGuid(),
        Title = "Process test",
        StreamUrl = "https://example.test/live/test-user/test-password/1.ts",
        OutputFilePath = Path.Combine(directory, "recording.ts"),
        StartTime = DateTime.UtcNow.AddSeconds(-1),
        EndTime = DateTime.UtcNow.AddMinutes(10),
        PreBufferMinutes = 0,
        PostBufferMinutes = 0,
        IsEpgBased = true
    }, directory);
    runs.Add(run);
    Session.FfmpegPath = missingExecutable ? Path.Combine(directory, "missing.exe") : Environment.ProcessPath!;
    Session.FfmpegArgsTemplate = $"--fake-ffmpeg {mode}";
    run.Scheduler.RecordingStarted += recording =>
    {
        if (!ReferenceEquals(recording, run.Recording)) return;
        Interlocked.Increment(ref run.StartedCount);
        beforeStarted?.Invoke(run);
        run.Started.TrySetResult();
    };
    run.Scheduler.RecordingFailed += recording =>
    {
        if (!ReferenceEquals(recording, run.Recording)) return;
        Interlocked.Increment(ref run.FailedCount); run.Finished.TrySetResult();
    };
    run.Scheduler.RecordingStopped += recording =>
    {
        if (!ReferenceEquals(recording, run.Recording)) return;
        Interlocked.Increment(ref run.StoppedCount); run.Finished.TrySetResult();
    };
    run.Recording.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(ScheduledRecording.Status) && run.Recording.Status == RecordingScheduleStatus.Recording)
            whenQueued?.Invoke();
    };
    run.Scheduler.ScheduleRecording(run.Recording);
    return run;
}

async Task finished(TestRun run) => await run.Finished.Task.WaitAsync(TimeSpan.FromSeconds(15));

ScheduledRecording ReadStored(TestRun run) => JsonSerializer.Deserialize<Dictionary<string, List<ScheduledRecording>>>(
    ProtectedRecordingFile.ReadAllText(Path.Combine(run.Directory, "scheduled_recordings.json")))!.Values.Single().Single();

Dictionary<string, List<ScheduledRecording>> ReadAccounts(TestRun run) =>
    JsonSerializer.Deserialize<Dictionary<string, List<ScheduledRecording>>>(
        ProtectedRecordingFile.ReadAllText(Path.Combine(run.Directory, "scheduled_recordings.json")))!;

string SessionKey() => $"{Environment.UserName}_{Session.Host}_{Session.Port}_{Session.Username}";

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    passed++;
}

sealed record TestRun(RecordingScheduler Scheduler, ScheduledRecording Recording, string Directory)
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int StartedCount;
    public int FailedCount;
    public int StoppedCount;
}
