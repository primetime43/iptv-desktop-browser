using System.Windows;
using DesktopApp.ViewModels;

namespace DesktopApp.Views.Dashboard;

public partial class SeriesScheduleWindow : Window
{
    public SeriesScheduleWindow(SeriesScheduleViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Loaded += async (_, _) =>
        {
            if (model.ReloadCommand.CanExecute(null)) await model.ReloadCommand.ExecuteAsync(null);
        };
        Closed += (_, _) => model.Dispose();
    }
}
