using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopApp.Views.Dashboard;

// Interaction forwarding is retained while page behavior migrates into its view model.
public partial class SchedulerPageView : UserControl
{
    public SchedulerPageView() => InitializeComponent();
    private void RecordingType_Changed(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.RecordingType_Changed(sender, e);
    private void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ChannelCombo_SelectionChanged(sender, e);
    private void ProgramCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ProgramCombo_SelectionChanged(sender, e);
    private void ProgramCombo_PreviewMouseMove(object sender, MouseEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ProgramCombo_PreviewMouseMove(sender, e);
    private void CustomChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.CustomChannelCombo_SelectionChanged(sender, e);
    private void CustomTime_Changed(object sender, EventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.CustomTime_Changed(sender, e);
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.BrowseOutput_Click(sender, e);
    private void ScheduleRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ScheduleRecording_Click(sender, e);
    private void RefreshScheduled_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.RefreshScheduled_Click(sender, e);
    private void DeleteCompleted_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.DeleteCompleted_Click(sender, e);
    private void OpenRecordingFolder_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.OpenRecordingFolder_Click(sender, e);
    private void PropertiesRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.PropertiesRecording_Click(sender, e);
    private void EditRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.EditRecording_Click(sender, e);
    private void CancelRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.CancelRecording_Click(sender, e);
    private void AddSeriesRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.AddSeriesRecording_Click(sender, e);
    private void ViewSeriesSchedule_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ViewSeriesSchedule_Click(sender, e);
    private void EditSeriesRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.EditSeriesRecording_Click(sender, e);
    private void DeleteSeriesRecording_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.DeleteSeriesRecording_Click(sender, e);
    private void ProgramComboItem_MouseEnter(object sender, MouseEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ProgramComboItem_MouseEnter(sender, e);
    private void ProgramComboItem_MouseMove(object sender, MouseEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ProgramComboItem_MouseMove(sender, e);
    private void ProgramComboItem_MouseLeave(object sender, MouseEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ProgramComboItem_MouseLeave(sender, e);
}