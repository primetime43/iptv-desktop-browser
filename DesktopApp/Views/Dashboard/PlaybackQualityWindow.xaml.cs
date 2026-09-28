using System.Windows;
using DesktopApp.ViewModels;

namespace DesktopApp.Views.Dashboard;

public partial class PlaybackQualityWindow : Window
{
    public PlaybackQualityWindow(PlaybackQualityViewModel model)
    {
        InitializeComponent(); DataContext = model;
        model.CloseRequested += CloseDialog;
        Loaded += async (_, _) =>
        {
            if (model.ReloadCommand.CanExecute(null)) await model.ReloadCommand.ExecuteAsync(null);
        };
        Closed += (_, _) => { model.CloseRequested -= CloseDialog; model.Dispose(); };
    }
    private void CloseDialog(bool result) => DialogResult = result;
}
