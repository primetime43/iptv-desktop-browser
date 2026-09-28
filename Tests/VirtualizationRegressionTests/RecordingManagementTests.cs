using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DesktopApp.Models;
using DesktopApp.Services;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

internal static partial class Program
{
    private static ScheduledRecording ManagementRecording(string title = "Recording") => new()
    {
        Title = title, ChannelId = 7, ChannelName = "Channel", StartTime = DateTime.UtcNow.AddDays(1),
        EndTime = DateTime.UtcNow.AddDays(1).AddHours(1), StreamUrl = "https://provider.invalid/user/secret/7.ts",
        Description = "Description", OutputFilePath = @"C:\recordings\test.ts", IsEpgBased = true,
        EpgProgramId = "program", IsSeriesRecording = true, SeriesRecordingId = Guid.NewGuid()
    };

    private static void VerifyRecordingManagement()
    {
        var service = new ManagementService();
        var interaction = new ManagementInteraction();
        using var model = new RecordingManagementViewModel(service, interaction);
        var recording = ManagementRecording();
        service.Items.Add(recording);
        Check(model.Recordings.Cast<object>().Single() == recording && model.EditCommand.CanExecute(recording) && model.CancelCommand.CanExecute(recording),
            "Recording management observes the scheduler's live collection and status");
        var commandChanges = 0;
        model.EditCommand.CanExecuteChanged += (_, _) => commandChanges++;
        recording.Status = RecordingScheduleStatus.Recording;
        Check(!model.EditCommand.CanExecute(recording) && model.CancelCommand.CanExecute(recording) && commandChanges > 0,
            "Recording status changes immediately invalidate edit/cancel availability");
        recording.Status = RecordingScheduleStatus.Scheduled;
        interaction.Edits = null;
        model.EditCommand.Execute(recording);
        Check(service.Updates == 0 && recording.PreBufferMinutes == 2, "Canceling the editor leaves the recording unchanged");
        interaction.Edits = new RecordingEdits("  New title  ", 4, 9);
        model.EditCommand.Execute(recording);
        var updated = service.Items.Single();
        Check(service.Updates == 1 && updated.Title == "New title" && updated.PreBufferMinutes == 4 && updated.PostBufferMinutes == 9,
            "Edit applies user-entered title and buffers through the scheduler service");
        Check(recording.Title == "Recording" && recording.PreBufferMinutes == 2 && updated.Id == recording.Id &&
            updated.IsSeriesRecording && updated.SeriesRecordingId == recording.SeriesRecordingId && updated.EpgProgramId == recording.EpgProgramId &&
            updated.CreatedAt == recording.CreatedAt && updated.StreamUrl == recording.StreamUrl && updated.OutputFilePath == recording.OutputFilePath,
            "Editing creates a detached replacement and preserves series, guide, stream, output, and identity metadata");
        var prompts = interaction.EditPrompts;
        model.EditCommand.Execute(recording);
        Check(!model.EditCommand.CanExecute(recording) && interaction.EditPrompts == prompts, "A replaced recording cannot be edited through a stale row");
        interaction.Edits = new RecordingEdits("Bad", -1, 5);
        model.EditCommand.Execute(updated);
        Check(service.Updates == 1 && model.StatusIsError, "Invalid edits are rejected even if a dialog implementation returns them");
        interaction.Edits = new RecordingEdits("Race", 3, 6);
        interaction.DuringEdit = () => updated.Status = RecordingScheduleStatus.Recording;
        model.EditCommand.Execute(updated);
        Check(service.Updates == 1 && model.StatusIsError, "A recording that starts during the modal editor cannot be overwritten");
        interaction.DuringEdit = null;
        updated.Status = RecordingScheduleStatus.Scheduled;
        service.AcceptUpdate = false;
        model.EditCommand.Execute(updated);
        Check(service.Updates == 1 && model.StatusMessage.Contains("no longer"), "A scheduler rejection cannot be reported as a successful edit");
        service.AcceptUpdate = true;
        service.ThrowUpdate = true;
        model.EditCommand.Execute(updated);
        Check(model.StatusIsError && model.StatusMessage.Contains("update failed") && service.Items[0] == updated,
            "Update exceptions leave the original row intact and surface an error");
        service.ThrowUpdate = false;
        interaction.Confirmation = false;
        model.CancelCommand.Execute(updated);
        Check(service.Cancellations == 0, "Declining cancellation does not alter the recording");
        interaction.Confirmation = true;
        interaction.DuringConfirmation = () => updated.Status = RecordingScheduleStatus.Completed;
        model.CancelCommand.Execute(updated);
        Check(service.Cancellations == 0, "A status change during cancellation confirmation is rechecked");
        interaction.DuringConfirmation = null;
        updated.Status = RecordingScheduleStatus.Recording;
        model.CancelCommand.Execute(updated);
        Check(service.Cancellations == 1 && updated.Status == RecordingScheduleStatus.Cancelled, "Cancel command reaches the existing scheduler process-management path");
        updated.FailureReason = "FFmpeg failed for https://provider.invalid/account/password/stream";
        updated.ExitCode = 7;
        model.PropertiesCommand.Execute(updated);
        Check(interaction.LastMessage.Contains("FFmpeg exit code: 7") && interaction.LastMessage.Contains("Failure details:") &&
            !interaction.LastMessage.Contains("https://") && !interaction.LastMessage.Contains("/secret/"),
            "Properties show recording failure details while redacting stream URLs throughout the message");
        interaction.DuringConfirmation = () => service.Items.Add(ManagementRecording("Arrived during prompt"));
        model.DeleteCompletedCommand.Execute(null);
        Check(service.Deletions == 0 && model.StatusIsError, "Cleanup refuses to act on a list that changed during confirmation");
        interaction.DuringConfirmation = null;
        interaction.DuringConfirmation = () => service.Items.Last().Status = RecordingScheduleStatus.Completed;
        model.DeleteCompletedCommand.Execute(null);
        Check(service.Deletions == 0, "Cleanup also rechecks status changes during confirmation");
        service.Items.Last().Status = RecordingScheduleStatus.Scheduled;
        interaction.DuringConfirmation = null;
        model.DeleteCompletedCommand.Execute(null);
        Check(service.Deletions == 1 && service.Items.Count == 1 && service.Items[0].Status == RecordingScheduleStatus.Scheduled,
            "Cleanup removes terminal entries while retaining scheduled recordings");
        model.OpenFolderCommand.Execute(null);
        Check(interaction.OpenedFolder == service.RecordingDirectory, "Output-folder command delegates to desktop interaction");
        interaction.ThrowOpen = true;
        model.OpenFolderCommand.Execute(null);
        Check(model.StatusIsError && interaction.LastMessage.Contains("folder unavailable"), "Folder errors remain visible when invoked from another page");
        interaction.ThrowOpen = false;
        var replacement = ManagementRecording("Other account");
        interaction.DuringEdit = () => { service.Items.Clear(); service.Items.Add(replacement); };
        var prior = service.Items[0];
        model.EditCommand.Execute(prior);
        Check(service.Updates == 1 && service.Items.Single() == replacement && model.StatusIsError,
            "Account/list replacement during editing cannot write the old account's recording");
        var beforeDispose = commandChanges;
        model.Dispose();
        replacement.Status = RecordingScheduleStatus.Recording;
        service.Items.Add(ManagementRecording());
        Check(commandChanges == beforeDispose && !model.EditCommand.CanExecute(replacement), "Disposal detaches collection and recording-status handlers");

        VerifyRecordingEditor();
        VerifyRecordingGrid();
    }

    private static void VerifyRecordingEditor()
    {
        var original = ManagementRecording();
        var editor = new RecordingEditViewModel(original);
        Check(editor.SaveCommand.CanExecute(null) && editor.Title == original.Title, "The editor initializes a valid detached draft");
        editor.Title = "";
        Check(!editor.SaveCommand.CanExecute(null) && editor.ValidationMessage.Contains("title"), "Editor requires a recording title");
        editor.Title = "Edited";
        foreach (var invalid in new[] { "-1", "abc", "1.5", "" })
        {
            editor.PreBuffer = invalid;
            editor.SaveCommand.Execute(null);
            Check(!editor.SaveCommand.CanExecute(null) && editor.Result == null, $"Editor rejects buffer '{invalid}'");
        }
        editor.PreBuffer = int.MaxValue.ToString();
        Check(!editor.SaveCommand.CanExecute(null), "Editor rejects buffers that would overflow recording dates");
        editor.PreBuffer = "0";
        editor.PostBuffer = "12";
        bool? closeResult = null;
        editor.CloseRequested += saved => closeResult = saved;
        editor.SaveCommand.Execute(null);
        Check(closeResult == true && editor.Result == new RecordingEdits("Edited", 0, 12) && original.Title == "Recording",
            "Editor returns validated values without mutating the original recording");
        var cancel = new RecordingEditViewModel(original);
        cancel.CancelCommand.Execute(null);
        Check(cancel.Result == null, "Canceled editing produces no persisted changes");

        var boundModel = new RecordingEditViewModel(original);
        var dialog = new RecordingEditWindow(boundModel);
        Layout(dialog.Content as FrameworkElement ?? throw new InvalidOperationException(), 460, 400);
        var title = (TextBox)dialog.FindName("TitleBox");
        title.SetCurrentValue(TextBox.TextProperty, "Typed draft");
        ((TextBox)dialog.FindName("PreBufferBox")).SetCurrentValue(TextBox.TextProperty, "bad");
        Layout((FrameworkElement)dialog.Content, 460, 400);
        Check(boundModel.Title == "Typed draft" && !((Button)dialog.FindName("SaveButton")).IsEnabled,
            "Compiled edit dialog binds draft values and disables saving invalid input");
        dialog.Close();
    }

    private static void VerifyRecordingGrid()
    {
        var service = new ManagementService();
        for (var i = 0; i < 10000; i++) service.Items.Add(ManagementRecording($"Recording {i}"));
        var interaction = new ManagementInteraction();
        using var management = new RecordingManagementViewModel(service, interaction);
        var form = CreateRecordingFixture().Page.NewRecording;
        using var pageModel = new SchedulerPageViewModel(form, management);
        var view = new SchedulerPageView { DataContext = new { SchedulerPageModel = pageModel } };
        using var source = new HwndSource(new HwndSourceParameters("Scheduled recording grid fixture")
        { Width = 1200, Height = 700, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
        source.RootVisual = view;
        var tabs = (TabControl)view.FindName("SchedulerTabs");
        tabs.SelectedIndex = 1;
        Layout(view, 1200, 700);
        var manager = Descendants(view).OfType<RecordingManagementView>().Single();
        var grid = (DataGrid)manager.FindName("ScheduledGrid");
        var rows = Descendants(grid).OfType<DataGridRow>().ToList();
        Check(manager.DataContext == management && grid.Items.Count == 10000 && rows.Count is > 0 and < 100,
            "The actual scheduler tab hosts a bounded grid that virtualizes 10,000 recordings");
        Check(grid.IsReadOnly, "Grid cells cannot bypass validated, persisted editing");
        var first = service.Items[0];
        var edit = Descendants(rows.Single(row => ReferenceEquals(row.Item, first))).OfType<Button>().Single(button => Equals(button.Content, "Edit"));
        Check(edit.IsEnabled && ReferenceEquals(edit.Command, management.EditCommand) && ReferenceEquals(edit.CommandParameter, first),
            "Recording row actions bind the management commands with the correct record");
        first.Status = RecordingScheduleStatus.Recording;
        Layout(view, 1200, 700);
        Check(!edit.IsEnabled, "A visible edit action disables when its recording starts");
        grid.ScrollIntoView(service.Items[^1]);
        Layout(view, 1200, 700);
        rows = Descendants(grid).OfType<DataGridRow>().ToList();
        Check(rows.Count < 100 && rows.Any(row => ReferenceEquals(row.Item, service.Items[^1])),
            "Scrolling to the final recording preserves bounded row realization");
        var last = service.Items[^1];
        var props = Descendants(rows.Single(row => ReferenceEquals(row.Item, last))).OfType<Button>().Single(button => Equals(button.Content, "Props"));
        props.Command.Execute(props.CommandParameter);
        Check(interaction.LastMessage.Contains("Recording 9999"), "Recycled action buttons target the final recording, not their previous row");
        var oldCommand = props.Command;
        var oldParameter = props.CommandParameter;
        service.Items.Clear();
        service.Items.Add(ManagementRecording("New account"));
        Layout(view, 1200, 700);
        Check(grid.Items.Count == 1 && !oldCommand.CanExecute(oldParameter), "Replacing the account's recordings invalidates stale row actions");
    }

    private sealed class ManagementService : IRecordingManagementService
    {
        public ObservableCollection<ScheduledRecording> Items { get; } = new();
        public ReadOnlyObservableCollection<ScheduledRecording> Recordings { get; }
        public int Updates, Cancellations, Deletions;
        public bool AcceptUpdate = true, ThrowUpdate;
        public string RecordingDirectory => @"C:\fixture-recordings";
        public ManagementService() => Recordings = new(Items);
        public bool Update(ScheduledRecording recording)
        {
            if (ThrowUpdate) throw new IOException("update failed");
            if (!AcceptUpdate) return false;
            var old = Items.Single(item => item.Id == recording.Id);
            Items[Items.IndexOf(old)] = recording;
            Updates++;
            return true;
        }
        public void Cancel(Guid id) { Items.Single(item => item.Id == id).Status = RecordingScheduleStatus.Cancelled; Cancellations++; }
        public void DeleteCompleted()
        {
            foreach (var recording in Items.Where(item => item.Status is RecordingScheduleStatus.Completed or RecordingScheduleStatus.Failed or RecordingScheduleStatus.Cancelled).ToArray())
                Items.Remove(recording);
            Deletions++;
        }
    }
    private sealed class ManagementInteraction : IRecordingManagementInteraction
    {
        public RecordingEdits? Edits;
        public int EditPrompts;
        public bool Confirmation = true, ThrowOpen;
        public Action? DuringEdit, DuringConfirmation;
        public string LastMessage = "", OpenedFolder = "";
        public RecordingEdits? Edit(ScheduledRecording recording) { EditPrompts++; DuringEdit?.Invoke(); return Edits; }
        public bool Confirm(string message, string title) { DuringConfirmation?.Invoke(); return Confirmation; }
        public void ShowMessage(string message, string title, bool isError = false) => LastMessage = message;
        public void OpenFolder(string path)
        {
            if (ThrowOpen) throw new IOException("folder unavailable");
            OpenedFolder = path;
        }
    }
}
