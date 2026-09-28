using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopApp.Models;
using DesktopApp.Services;

namespace DesktopApp.ViewModels;

public partial class RecordingEditViewModel : ObservableObject
{
    private readonly DateTime _startTime;
    private readonly DateTime _endTime;
    public RecordingEditViewModel(ScheduledRecording recording)
    {
        _startTime = recording.StartTime;
        _endTime = recording.EndTime;
        _title = recording.Title;
        _preBuffer = recording.PreBufferMinutes.ToString();
        _postBuffer = recording.PostBufferMinutes.ToString();
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ValidationMessage)), NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ValidationMessage)), NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _preBuffer;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ValidationMessage)), NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _postBuffer;

    public RecordingEdits? Result { get; private set; }
    public event Action<bool>? CloseRequested;
    public string ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title)) return "Enter a recording title.";
            if (!int.TryParse(PreBuffer, out var pre) || pre < 0 || !int.TryParse(PostBuffer, out var post) || post < 0)
                return "Buffer times must be non-negative whole minutes.";
            try { _ = _startTime.AddMinutes(-pre); _ = _endTime.AddMinutes(post); }
            catch (ArgumentOutOfRangeException) { return "Buffer times are too large for this recording."; }
            return string.Empty;
        }
    }
    private bool CanSave() => ValidationMessage.Length == 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!CanSave()) return;
        Result = new RecordingEdits(Title.Trim(), int.Parse(PreBuffer), int.Parse(PostBuffer));
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
