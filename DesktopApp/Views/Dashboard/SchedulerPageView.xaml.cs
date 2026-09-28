using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopApp.Views.Dashboard;

// Interaction forwarding is retained while page behavior migrates into its view model.
public partial class SchedulerPageView : UserControl
{
    public SchedulerPageView() => InitializeComponent();
    private void AddSeriesRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.AddSeriesRecording_Click(sender, e);
    private void ViewSeriesSchedule_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ViewSeriesSchedule_Click(sender, e);
    private void EditSeriesRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.EditSeriesRecording_Click(sender, e);
    private void DeleteSeriesRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.DeleteSeriesRecording_Click(sender, e);
}
