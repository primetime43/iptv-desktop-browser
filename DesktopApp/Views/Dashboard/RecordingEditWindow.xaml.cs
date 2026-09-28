using System.Windows;
using DesktopApp.ViewModels;

namespace DesktopApp.Views.Dashboard;

public partial class RecordingEditWindow : Window
{
    public RecordingEditWindow(RecordingEditViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.CloseRequested += OnCloseRequested;
        Closed += (_, _) => model.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(bool saved) => DialogResult = saved;
}
