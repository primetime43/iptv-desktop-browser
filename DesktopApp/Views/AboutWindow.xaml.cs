using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.Reflection;

namespace DesktopApp.Views
{
    public partial class AboutWindow : Window
    {
        private const string RepoOwner = "primetime43";
        private const string RepoName = "iptv-desktop-browser";
        private const string LatestApi = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
        private readonly HttpClient _http = new();
        private string? _latestTag;
        private string? _latestUrl;
        private readonly string _currentVersion;

        public AboutWindow(string currentVersion, string? latestTag, string? latestUrl)
        {
            InitializeComponent();
            _currentVersion = NormalizeDisplayVersion(currentVersion);
            _latestTag = latestTag;
            _latestUrl = latestUrl;
            VersionText.Text = $"Version: {_currentVersion}";
            if (!string.IsNullOrWhiteSpace(_latestTag))
            {
                UpdateStatusText.Text = $"Latest: {_latestTag}";
                ReleaseLink.Visibility = Visibility.Visible;
                if (IsNewer(_latestTag, _currentVersion))
                {
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    UpdateStatusText.Text += " (update available)";
                }
            }
            else
            {
                _ = PerformUpdateCheckAsync();
            }
        }

        private string NormalizeDisplayVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "1.0.0";
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v[1..];
            var parts = v.Split('.');
            if (parts.Length >= 3)
            {
                if (parts.Length > 3 && parts[3] == "0") v = string.Join('.', parts[0], parts[1], parts[2]);
                else if (parts.Length == 4 && parts[3] == "0") v = string.Join('.', parts[0], parts[1], parts[2]);
            }
            return v;
        }

        private async Task PerformUpdateCheckAsync()
        {
            UpdateStatusText.Text = "Checking for updates...";
            CheckUpdateButton.IsEnabled = false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
                req.Headers.UserAgent.ParseAdd("iptv-desktop-browser");
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    UpdateStatusText.Text = "Update check failed.";
                    return;
                }
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                _latestTag = doc.RootElement.GetProperty("tag_name").GetString();
                _latestUrl = doc.RootElement.TryGetProperty("html_url", out var uEl) ? uEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(_latestTag))
                {
                    UpdateStatusText.Text = "No release tag found.";
                    return;
                }
                ReleaseLink.Visibility = Visibility.Visible;
                UpdateStatusText.Text = $"Latest: {_latestTag}";
                if (IsNewer(_latestTag, _currentVersion))
                {
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    UpdateStatusText.Text += " (update available)";
                }
                else
                {
                    UpdateStatusText.Text += " (you are up to date)";
                }
            }
            catch
            {
                UpdateStatusText.Text = "Update check failed.";
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
            }
        }

        private bool IsNewer(string latestTag, string current)
        {
            (int, int, int, string) Parse(string v)
            {
                if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v[1..];
                var rest = string.Empty;
                var dash = v.IndexOf('-');
                if (dash > 0) { rest = v[dash..]; v = v[..dash]; }
                var parts = v.Split('.');
                int a=0,b=0,c=0; if (parts.Length>0) int.TryParse(parts[0], out a); if (parts.Length>1) int.TryParse(parts[1], out b); if (parts.Length>2) int.TryParse(parts[2], out c);
                return (a,b,c,rest);
            }
            var L = Parse(latestTag); var C = Parse(current);
            if (L.Item1!=C.Item1) return L.Item1>C.Item1;
            if (L.Item2!=C.Item2) return L.Item2>C.Item2;
            if (L.Item3!=C.Item3) return L.Item3>C.Item3;
            if (string.IsNullOrEmpty(C.Item4) && !string.IsNullOrEmpty(L.Item4)) return false;
            if (!string.IsNullOrEmpty(C.Item4) && string.IsNullOrEmpty(L.Item4)) return true;
            return false;
        }

        private void RepoLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        { try { Process.Start(new ProcessStartInfo { FileName = $"https://github.com/{RepoOwner}/{RepoName}", UseShellExecute = true }); } catch { } }
        private void ReleaseLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        { if (string.IsNullOrWhiteSpace(_latestUrl)) return; try { Process.Start(new ProcessStartInfo { FileName = _latestUrl, UseShellExecute = true }); } catch { } }
        private void OpenReleaseButton_Click(object sender, RoutedEventArgs e) { ReleaseLink_Click(sender, null!); }
        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e) { await PerformUpdateCheckAsync(); }
    }
}
