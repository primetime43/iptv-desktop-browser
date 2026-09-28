using System.Windows;
using DesktopApp.ViewModels;

namespace DesktopApp.Views.Dashboard;

public partial class SeriesRecordingWindow : Window
{
    public SeriesRecordingWindow(SeriesRecordingDialogViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.CloseRequested += CloseDialog;
        model.PreviewRequested += Preview;
        Closed += (_, _) =>
        {
            model.CloseRequested -= CloseDialog;
            model.PreviewRequested -= Preview;
            model.Dispose();
        };
    }
    private void CloseDialog(bool saved) => DialogResult = saved;
    private void Preview(SeriesScheduleViewModel model)
    {
        using (model) new SeriesScheduleWindow(model) { Owner = this }.ShowDialog();
    }
}
