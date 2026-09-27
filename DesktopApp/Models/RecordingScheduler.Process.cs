using System.Diagnostics;
using System.IO;
using DesktopApp.Security;
using DesktopApp.Views;

namespace DesktopApp.Models;

public partial class RecordingScheduler
{
    private void StartRecording(ScheduledRecording recording)
    {
        lock (_lockObject)
        {
            if (recording.Status != RecordingScheduleStatus.Scheduled || _activeRecordings.ContainsKey(recording.Id))
                return;

            var run = new RecordingRun(recording);
            _activeRecordings.Add(recording.Id, run);
            recording.FailureReason = null;
            recording.ExitCode = null;
            recording.Status = RecordingScheduleStatus.Recording;
            SaveScheduledRecordings();
            _ = Task.Run(() => RunRecordingAsync(run));
        }
    }

    private async Task RunRecordingAsync(RecordingRun run)
    {
        var recording = run.Recording;
        Process? process = null;
        int? exitCode = null;
        DateTime exitedUtc = DateTime.UtcNow;
        string? failure = null;
        try
        {
            var psi = Session.BuildFfmpegRecordProcess(recording.StreamUrl, recording.Title, recording.OutputFilePath)
                ?? throw new InvalidOperationException("FFmpeg path not set or file not found. Configure FFmpeg in Settings.");
            psi.RedirectStandardInput = true;
            var outputDir = Path.GetDirectoryName(recording.OutputFilePath);
            if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

            lock (_lockObject)
            {
                // Cancellation may arrive before the worker starts the process.
                if (run.CancelRequested) return;
                process = new Process { StartInfo = psi };
                if (!process.Start()) throw new InvalidOperationException("Unable to start FFmpeg.");
                run.Process = process;
                recording.RecordingProcess = process;
                if (run.StopRequested) BeginStop(run);
            }

            var stdout = ReadProcessOutputAsync(process.StandardOutput, run, isError: false);
            var stderr = ReadProcessOutputAsync(process.StandardError, run, isError: true);
            UpdateRecordingIndicator();
            NotifyRecordingEvent(RecordingStarted, recording);
            Log($"Started FFmpeg recording: {recording.Title}");

            // A single completion path handles even processes which exit during startup.
            // Capture the exit time before draining the last diagnostics.
            await process.WaitForExitAsync();
            exitedUtc = process.ExitTime.ToUniversalTime();
            exitCode = process.ExitCode;
            await Task.WhenAll(stdout, stderr);
        }
        catch (Exception ex)
        {
            failure = $"Unable to record: {ex.Message}";
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    exitCode = process.ExitCode;
                }
                catch (Exception cleanupError) { Log($"Recording cleanup failed: {cleanupError.Message}"); }
            }
        }
        finally
        {
            // Do not dispose the process while the stop worker still owns stdin/kill,
            // or publish a result before it has reported a forced termination.
            Task? stopTask;
            lock (_lockObject) stopTask = run.StopTask;
            if (stopTask != null) await stopTask;

            lock (_lockObject)
            {
                if (run.CancelRequested)
                {
                    recording.Status = RecordingScheduleStatus.Cancelled;
                }
                else
                {
                    failure ??= run.StopError;
                    if (run.ForcedStop)
                        failure ??= "FFmpeg did not stop gracefully and had to be terminated. The recording may be incomplete.";
                    if (exitCode != 0)
                        failure ??= $"FFmpeg exited with code {exitCode?.ToString() ?? "unknown"}.";
                    if (!run.StopRequested && exitedUtc < recording.EndTime.AddMinutes(recording.PostBufferMinutes))
                        failure ??= "FFmpeg exited before the scheduled end (exit code 0). The recording is incomplete.";

                    if (failure != null)
                    {
                        string details;
                        lock (run.ErrorLines) details = string.Join(Environment.NewLine, run.ErrorLines);
                        recording.FailureReason = DiagnosticRedactor.Redact(
                            string.IsNullOrEmpty(details) ? failure : failure + Environment.NewLine + details, run.Secrets);
                        recording.Status = RecordingScheduleStatus.Failed;
                    }
                    else recording.Status = RecordingScheduleStatus.Completed;
                }

                recording.ExitCode = exitCode;
                recording.RecordingProcess = null;
                run.Process = null;
                _activeRecordings.Remove(recording.Id);
                process?.Dispose();
                SaveScheduledRecordings();
            }

            UpdateRecordingIndicator();
            Log($"Recording {recording.StatusText}: {recording.Title}. {recording.FailureReason}");
            NotifyRecordingEvent(recording.Status == RecordingScheduleStatus.Failed ? RecordingFailed : RecordingStopped, recording);
        }
    }

    private static async Task ReadProcessOutputAsync(StreamReader reader, RecordingRun run, bool isError)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Sanitize before truncating so neither stored details nor UI expose URLs.
            var safe = DiagnosticRedactor.Redact(line, run.Secrets);
            if (safe.Length > 500) safe = safe[..500] + "...";
            if (isError)
            {
                lock (run.ErrorLines)
                {
                    run.ErrorLines.Enqueue(safe);
                    while (run.ErrorLines.Count > 8) run.ErrorLines.Dequeue();
                }
            }
            Log($"FFMPEG [{run.Recording.Title}]: {safe}");
        }
    }

    private void StopRecording(ScheduledRecording recording, bool cancelled = false)
    {
        lock (_lockObject)
        {
            if (recording.Status != RecordingScheduleStatus.Recording ||
                !_activeRecordings.TryGetValue(recording.Id, out var run)) return;

            // An already-exited process retains its own result, even if a timer tick
            // or Cancel click arrives before the monitor finishes draining stderr.
            if (run.Process?.HasExited == true) return;
            run.CancelRequested |= cancelled;
            run.StopRequested = true;
            if (run.Process != null) BeginStop(run);
        }
    }

    private void BeginStop(RecordingRun run)
    {
        run.StopTask ??= Task.Run(async () =>
        {
            var process = run.Process!;
            try
            {
                if (process.HasExited) return;
                // FFmpeg is a console process: 'q' flushes buffers and finalizes the
                // container. CloseMainWindow cannot stop a hidden console process.
                try
                {
                    await process.StandardInput.WriteLineAsync("q");
                    await process.StandardInput.FlushAsync();
                }
                catch (IOException) { /* It may have exited while stdin was written. */ }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    lock (_lockObject) run.ForcedStop = true;
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                lock (_lockObject) run.StopError = $"Unable to stop FFmpeg cleanly: {ex.Message}";
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception killError) { Log($"Unable to terminate FFmpeg: {killError.Message}"); }
            }
        });
    }

    private void UpdateRecordingIndicator()
    {
        var application = System.Windows.Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted) return;
        var dispatcher = application.Dispatcher;
        dispatcher.BeginInvoke(() =>
        {
            lock (_lockObject)
            {
                // Scheduled exits must not clear the manual-recording indicator.
                if (RecordingManager.Instance.IsManualRecording) return;
                var active = _activeRecordings.Values.FirstOrDefault(r => r.Process != null)?.Recording;
                if (active == null) RecordingManager.Instance.StopRecording();
                else RecordingManager.Instance.StartRecording(active.StreamUrl, active.OutputFilePath, active.Title, active.ChannelId);
                foreach (System.Windows.Window window in application.Windows)
                    if (window is DashboardWindow dashboard && dashboard.FindName("RecordBtnText") is System.Windows.Controls.TextBlock text)
                        text.Text = active == null ? "Record" : "Scheduled";
            }
        });
    }

    private static void NotifyRecordingEvent(Action<ScheduledRecording>? handler, ScheduledRecording recording)
    {
        try { handler?.Invoke(recording); }
        catch (Exception ex) { Log($"Recording notification failed: {ex.Message}"); }
    }
}
