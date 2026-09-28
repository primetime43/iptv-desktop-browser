using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Security;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public partial class RecordingManagementViewModel : ObservableObject, IDisposable
{
    private readonly IRecordingManagementService _service;
    private readonly IRecordingManagementInteraction _interaction;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HashSet<ScheduledRecording> _observed = new();
    private bool _disposed;

    public RecordingManagementViewModel(IRecordingManagementService service, IRecordingManagementInteraction interaction)
    {
        _service = service;
        _interaction = interaction;
        Recordings = CollectionViewSource.GetDefaultView(service.Recordings);
        ((INotifyCollectionChanged)service.Recordings).CollectionChanged += CollectionChanged;
        ObserveRecordings();
    }

    public ICollectionView Recordings { get; }
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _statusIsError;

    private bool IsCurrent(ScheduledRecording? recording) => !_disposed && recording != null && _service.Recordings.Contains(recording);
    private bool CanEdit(ScheduledRecording? recording) => IsCurrent(recording) && recording!.CanEdit;
    private bool CanCancel(ScheduledRecording? recording) => IsCurrent(recording) && recording!.CanCancel;
    private bool CanDeleteCompleted() => !_disposed && _service.Recordings.Any(r =>
        r.Status is RecordingScheduleStatus.Completed or RecordingScheduleStatus.Failed or RecordingScheduleStatus.Cancelled);

    [RelayCommand]
    private void Refresh() => Run(() => Recordings.Refresh());

    [RelayCommand(CanExecute = nameof(IsCurrent))]
    private void Properties(ScheduledRecording? recording)
    {
        if (!IsCurrent(recording)) return;
        Run(() => _interaction.ShowMessage(FormatProperties(recording!), "Recording Properties"));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit(ScheduledRecording? recording)
    {
        if (!CanEdit(recording)) return;
        Run(() =>
        {
            var edits = _interaction.Edit(recording!);
            if (edits == null) return;
            // Modal dialogs pump the dispatcher: status or the account may have changed meanwhile.
            if (!CanEdit(recording)) { SetStatus("This recording changed while the editor was open. Reopen it to edit.", true); return; }
            var validation = new RecordingEditViewModel(recording!)
                { Title = edits.Title, PreBuffer = edits.PreBufferMinutes.ToString(), PostBuffer = edits.PostBufferMinutes.ToString() };
            if (validation.ValidationMessage.Length > 0) { SetStatus(validation.ValidationMessage, true); return; }
            var updated = CopyWithEdits(recording!, edits);
            if (!_service.Update(updated)) { SetStatus("This recording can no longer be edited.", true); return; }
            SetStatus("Recording updated successfully.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel(ScheduledRecording? recording)
    {
        if (!CanCancel(recording)) return;
        Run(() =>
        {
            if (!_interaction.Confirm($"Cancel recording '{recording!.Title}'?", "Cancel Recording")) return;
            if (!CanCancel(recording)) { SetStatus("This recording can no longer be cancelled.", true); return; }
            _service.Cancel(recording.Id);
            SetStatus("Recording cancellation requested.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanDeleteCompleted))]
    private void DeleteCompleted()
    {
        if (!CanDeleteCompleted()) return;
        Run(() =>
        {
            // Only delete records from the collection the user reviewed before the modal prompt.
            var reviewed = _service.Recordings.Select(r => (Recording: r, r.Status)).ToArray();
            if (!_interaction.Confirm("Delete completed, failed, and cancelled recordings from the list? Recording files are kept.",
                "Delete Completed Recordings")) return;
            if (_disposed || !reviewed.SequenceEqual(_service.Recordings.Select(r => (Recording: r, r.Status))))
            {
                SetStatus("The recording list changed. Review it and try again.", true);
                return;
            }
            _service.DeleteCompleted();
            SetStatus("Finished recordings removed from the list.");
        });
    }

    [RelayCommand]
    private void OpenFolder() => Run(() => _interaction.OpenFolder(_service.RecordingDirectory), showError: true);

    private void Run(Action action, bool showError = false)
    {
        if (_disposed) return;
        try { action(); }
        catch (Exception ex)
        {
            SetStatus(DiagnosticRedactor.Redact(ex.Message), true);
            if (showError) _interaction.ShowMessage(StatusMessage, "Recording Error", true);
        }
    }
    private void SetStatus(string message, bool error = false)
    {
        StatusIsError = error;
        StatusMessage = message;
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { ObserveRecordings(); return; }
        // Apply deltas rather than rescanning the whole history for each add/remove.
        if (e.OldItems != null)
            foreach (ScheduledRecording recording in e.OldItems)
                if (_observed.Remove(recording)) recording.PropertyChanged -= RecordingChanged;
        if (e.NewItems != null)
            foreach (ScheduledRecording recording in e.NewItems)
                if (_observed.Add(recording)) recording.PropertyChanged += RecordingChanged;
        NotifyCommands();
    }
    private void ObserveRecordings()
    {
        var current = _service.Recordings.ToHashSet();
        foreach (var removed in _observed.Except(current).ToArray())
        {
            removed.PropertyChanged -= RecordingChanged;
            _observed.Remove(removed);
        }
        foreach (var added in current.Except(_observed))
        {
            added.PropertyChanged += RecordingChanged;
            _observed.Add(added);
        }
        NotifyCommands();
    }
    private void RecordingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScheduledRecording.Status)) NotifyCommands();
    }
    private void NotifyCommands()
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(NotifyCommands); return; }
        EditCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        PropertiesCommand.NotifyCanExecuteChanged();
        DeleteCompletedCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ((INotifyCollectionChanged)_service.Recordings).CollectionChanged -= CollectionChanged;
        foreach (var recording in _observed) recording.PropertyChanged -= RecordingChanged;
        _observed.Clear();
    }

    private static ScheduledRecording CopyWithEdits(ScheduledRecording source, RecordingEdits edits) => new()
    {
        Id = source.Id, Title = edits.Title.Trim(), Description = source.Description,
        ChannelId = source.ChannelId, ChannelName = source.ChannelName, StreamUrl = source.StreamUrl,
        StartTime = source.StartTime, EndTime = source.EndTime, Status = source.Status,
        OutputFilePath = source.OutputFilePath, IsEpgBased = source.IsEpgBased, EpgProgramId = source.EpgProgramId,
        IsSeriesRecording = source.IsSeriesRecording, SeriesRecordingId = source.SeriesRecordingId,
        PreBufferMinutes = edits.PreBufferMinutes, PostBufferMinutes = edits.PostBufferMinutes,
        CreatedAt = source.CreatedAt, ExitCode = source.ExitCode, FailureReason = source.FailureReason
    };

    private static string FormatProperties(ScheduledRecording recording)
    {
        var text = $"Title: {recording.Title}\nChannel: {recording.ChannelName}\nStatus: {recording.StatusText}\n" +
            $"Start: {recording.StartTimeLocal}\nEnd: {recording.EndTimeLocal}\nDuration: {recording.DurationText}\n" +
            $"Pre-buffer: {recording.PreBufferMinutes} minutes\nPost-buffer: {recording.PostBufferMinutes} minutes\n" +
            $"EPG-based: {(recording.IsEpgBased ? "Yes" : "No")}\nOutput file: {recording.OutputFilePath}\nStream URL: {recording.StreamUrl}\n";
        if (recording.ExitCode.HasValue) text += $"FFmpeg exit code: {recording.ExitCode}\n";
        if (!string.IsNullOrWhiteSpace(recording.FailureReason)) text += $"Failure details: {recording.FailureReason}\n";
        if (!string.IsNullOrWhiteSpace(recording.Description)) text += $"Description: {recording.Description}\n";
        return DiagnosticRedactor.Redact(text);
    }
}
