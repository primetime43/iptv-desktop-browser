using System.Windows;
using System.Collections.Generic;
using System.Linq;
using DesktopApp.Models;

namespace DesktopApp.Views
{
    // Unified profile interface for displaying both Xtream and M3U profiles
    public interface IProfileEntry
    {
        string Type { get; }
        string Display { get; }
        object UnderlyingProfile { get; }
    }

    public class XtreamProfileEntry : IProfileEntry
    {
        public CredentialProfile Profile { get; set; }
        public string Type => Profile?.Type ?? "Xtream";
        public string Display => Profile?.Display ?? string.Empty;
        public object UnderlyingProfile => Profile!;

        public XtreamProfileEntry(CredentialProfile profile) { Profile = profile; }
    }

    public class M3uProfileEntry : IProfileEntry
    {
        public M3uProfile Profile { get; set; }
        public string Type => Profile?.Type ?? "M3U";
        public string Display => $"[M3U] {Profile?.Display ?? string.Empty}";
        public object UnderlyingProfile => Profile!;

        public M3uProfileEntry(M3uProfile profile) { Profile = profile; }
    }

    public partial class CredentialManagerWindow : Window
    {
        public CredentialManagerWindow()
        {
            InitializeComponent();
            LoadProfiles();
        }

        private void LoadProfiles()
        {
            var allProfiles = new List<IProfileEntry>();

            // Add Xtream profiles
            foreach (var xtream in CredentialStore.GetAll())
            {
                allProfiles.Add(new XtreamProfileEntry(xtream));
            }

            // Add M3U profiles
            foreach (var m3u in CredentialStore.GetAllM3u())
            {
                allProfiles.Add(new M3uProfileEntry(m3u));
            }

            ProfilesList.ItemsSource = allProfiles;
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            LoadProfiles();
            StatusText.Text = "Reloaded";
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesList.SelectedItem is XtreamProfileEntry xtream)
            {
                CredentialStore.Delete(xtream.Profile.Server, xtream.Profile.Username);
                LoadProfiles();
                StatusText.Text = "Deleted";
            }
            else if (ProfilesList.SelectedItem is M3uProfileEntry m3u)
            {
                CredentialStore.DeleteM3u(m3u.Profile.PlaylistUrl);
                LoadProfiles();
                StatusText.Text = "Deleted";
            }
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Delete ALL saved profiles (Xtream + M3U)?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                CredentialStore.DeleteAll();
                LoadProfiles();
                StatusText.Text = "All deleted";
            }
        }

        private void SaveUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortBox.Text.Trim(), out var port)) { StatusText.Text = "Invalid port"; return; }
            if (string.IsNullOrWhiteSpace(ServerBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text) || string.IsNullOrWhiteSpace(PassBox.Password)) { StatusText.Text = "Missing fields"; return; }
            CredentialStore.SaveOrUpdate(ServerBox.Text.Trim(), port, SslBox.IsChecked == true, UserBox.Text.Trim(), PassBox.Password);
            LoadProfiles();
            StatusText.Text = "Saved";
        }

        private void ProfilesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ProfilesList.SelectedItem is XtreamProfileEntry xtream)
            {
                if (CredentialStore.TryGet(xtream.Profile.Server, xtream.Profile.Username, out var full))
                {
                    ServerBox.Text = full.Server;
                    PortBox.Text = full.Port.ToString();
                    UserBox.Text = full.Username;
                    PassBox.Password = full.Password;
                    PassTextBox.Text = full.Password; // sync to text box
                    SslBox.IsChecked = full.UseSsl;
                    StatusText.Text = "Loaded Xtream profile";
                }
            }
            else if (ProfilesList.SelectedItem is M3uProfileEntry m3u)
            {
                // Clear Xtream fields for M3U profiles
                ServerBox.Text = m3u.Profile.PlaylistUrl;
                PortBox.Text = string.Empty;
                UserBox.Text = string.Empty;
                PassBox.Password = string.Empty;
                PassTextBox.Text = string.Empty;
                SslBox.IsChecked = false;
                StatusText.Text = "Loaded M3U profile";
            }
        }

        private void Use_Click(object sender, RoutedEventArgs e)
        {
            if (Owner is MainWindow mw)
            {
                if (ProfilesList.SelectedItem is XtreamProfileEntry xtream && CredentialStore.TryGet(xtream.Profile.Server, xtream.Profile.Username, out var full))
                {
                    mw.ApplyCredentials(full);
                    StatusText.Text = "Applied Xtream profile";
                }
                else if (ProfilesList.SelectedItem is M3uProfileEntry m3u)
                {
                    mw.ApplyM3uProfile(m3u.Profile);
                    StatusText.Text = "Applied M3U profile";
                }
            }
        }

        private bool _isPasswordVisible = false;
        private bool _isUpdatingPassword = false;

        private void TogglePassButton_Click(object sender, RoutedEventArgs e)
        {
            _isPasswordVisible = !_isPasswordVisible;
            if (_isPasswordVisible)
            {
                // Show password as plain text
                PassTextBox.Text = PassBox.Password;
                PassTextBox.Visibility = Visibility.Visible;
                PassBox.Visibility = Visibility.Collapsed;
                TogglePassIcon.Text = "🙈"; // closed eye
            }
            else
            {
                // Hide password
                PassBox.Password = PassTextBox.Text;
                PassBox.Visibility = Visibility.Visible;
                PassTextBox.Visibility = Visibility.Collapsed;
                TogglePassIcon.Text = "👁"; // open eye
            }
        }

        private void PassBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingPassword) return;
            _isUpdatingPassword = true;
            PassTextBox.Text = PassBox.Password;
            _isUpdatingPassword = false;
        }

        private void PassTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isUpdatingPassword) return;
            _isUpdatingPassword = true;
            PassBox.Password = PassTextBox.Text;
            _isUpdatingPassword = false;
        }
    }
}
