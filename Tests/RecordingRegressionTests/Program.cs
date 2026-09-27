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

TestRun Begin(string mode, bool missingExecutable = false, Action<TestRun>? beforeStarted = null)
{
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var run = new TestRun(new RecordingScheduler(directory), new ScheduledRecording
    {
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
    run.Scheduler.RecordingStarted += _ =>
    {
        Interlocked.Increment(ref run.StartedCount);
        beforeStarted?.Invoke(run);
        run.Started.TrySetResult();
    };
    run.Scheduler.RecordingFailed += _ => { Interlocked.Increment(ref run.FailedCount); run.Finished.TrySetResult(); };
    run.Scheduler.RecordingStopped += _ => { Interlocked.Increment(ref run.StoppedCount); run.Finished.TrySetResult(); };
    run.Scheduler.ScheduleRecording(run.Recording);
    return run;
}

async Task finished(TestRun run) => await run.Finished.Task.WaitAsync(TimeSpan.FromSeconds(15));

ScheduledRecording ReadStored(TestRun run) => JsonSerializer.Deserialize<Dictionary<string, List<ScheduledRecording>>>(
    ProtectedRecordingFile.ReadAllText(Path.Combine(run.Directory, "scheduled_recordings.json")))!.Values.Single().Single();

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
