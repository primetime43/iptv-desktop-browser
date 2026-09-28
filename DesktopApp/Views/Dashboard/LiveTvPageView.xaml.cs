using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopApp.Views.Dashboard;

// Interaction forwarding is retained while page behavior migrates into its view model.
public partial class LiveTvPageView : UserControl
{
    public LiveTvPageView() => InitializeComponent();
    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.CategoryCombo_SelectionChanged(sender, e);
    private void ChannelsGridView_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ChannelsGridView_Click(sender, e);
    private void ChannelsListView_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ChannelsListView_Click(sender, e);
    private void TileSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.TileSize_SelectionChanged(sender, e);
    private void ChannelTile_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ChannelTile_PreviewMouseLeftButtonDown(sender, e);
    private void ChannelTile_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ChannelTile_MouseEnter(sender, e);
    private void ChannelFavoriteButton_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ChannelFavoriteButton_Click(sender, e);
    private void ChannelRecordButton_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as DashboardWindow)?.ChannelRecordButton_Click(sender, e);
}