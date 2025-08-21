using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DesktopApp.Models;

namespace DesktopApp.Views
{
    public partial class DashboardWindow : Window
    {
        private readonly HttpClient _http = new();

        public DashboardWindow()
        {
            InitializeComponent();
            UserNameText.Text = Session.Username;
        }

        private async void LoadCategories_Click(object sender, RoutedEventArgs e)
        {
            await RunApiCall("get_live_categories");
        }

        private async void LoadStreams_Click(object sender, RoutedEventArgs e)
        {
            await RunApiCall("get_live_streams");
        }

        private async Task RunApiCall(string action)
        {
            try
            {
                var url = Session.BuildApi(action);
                OutputText.AppendText($"GET {url}\n");
                var json = await _http.GetStringAsync(url);
                // Truncate huge payload
                if (json.Length > 50_000) json = json.Substring(0, 50_000) + "...<truncated>";
                OutputText.AppendText(json + "\n\n");
            }
            catch (Exception ex)
            {
                OutputText.AppendText("ERROR: " + ex.Message + "\n");
            }
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            Session.Username = Session.Password = string.Empty;
            Close();
            Application.Current.MainWindow?.Show();
        }
    }
}
