using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DesktopApp.Models;

namespace DesktopApp.Views;

public partial class VodWindow : Window
{
    public VodWindow()
    {
        InitializeComponent();
    }

    public VodWindow(object dataContext) : this()
    {
        DataContext = dataContext;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Set DataContext from owner if not already set
        if (DataContext == null && Owner is DashboardWindow dash)
        {
            DataContext = dash.DataContext;
        }
    }

    private void VodContent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not VodContent vod)
            return;

        if (DataContext is DashboardWindow dashboard)
        {
            // Check for double-click to launch player
            var now = DateTime.UtcNow;
            const int doubleClickMs = 400;

            if (dashboard.SelectedVodContent == vod && (now - _lastVodClickTime).TotalMilliseconds <= doubleClickMs)
            {
                // Double-click: launch player
                if (Owner is DashboardWindow dash)
                {
                    dash.GetType().GetMethod("TryLaunchVodInPlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                        .Invoke(dash, new object[] { vod });
                }
                _lastVodClickTime = DateTime.MinValue;
            }
            else
            {
                // Single-click: select VOD content
                dashboard.SelectedVodContent = vod;
                _lastVodClickTime = now;
            }
        }
    }

    private DateTime _lastVodClickTime;

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardWindow dashboard && dashboard.SelectedVodContent != null)
        {
            if (Owner is DashboardWindow dash)
            {
                dash.GetType().GetMethod("TryLaunchVodInPlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                    .Invoke(dash, new object[] { dashboard.SelectedVodContent });
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
