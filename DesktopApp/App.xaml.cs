using System.Configuration;
using System.Data;
using System.Windows;
using DesktopApp.Models;

namespace DesktopApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            SettingsStore.LoadIntoSession();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SettingsStore.SaveFromSession();
            base.OnExit(e);
        }
    }
}
