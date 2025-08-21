using System.Windows;
using DesktopApp.Models;

namespace DesktopApp.Views
{
    public partial class CredentialManagerWindow : Window
    {
        public CredentialManagerWindow()
        {
            InitializeComponent();
            LoadProfiles();
        }

        private void LoadProfiles()
        {
            ProfilesList.ItemsSource = CredentialStore.GetAll();
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            LoadProfiles();
            StatusText.Text = "Reloaded";
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesList.SelectedItem is CredentialProfile prof)
            {
                CredentialStore.Delete(prof.Server, prof.Username);
                LoadProfiles();
                StatusText.Text = "Deleted";
            }
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Delete ALL saved credentials?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
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
            if (ProfilesList.SelectedItem is CredentialProfile prof)
            {
                if (CredentialStore.TryGet(prof.Server, prof.Username, out var full))
                {
                    ServerBox.Text = full.Server;
                    PortBox.Text = full.Port.ToString();
                    UserBox.Text = full.Username;
                    PassBox.Password = full.Password;
                    SslBox.IsChecked = full.UseSsl;
                    StatusText.Text = "Loaded";
                }
            }
        }

        private void Use_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesList.SelectedItem is CredentialProfile prof && CredentialStore.TryGet(prof.Server, prof.Username, out var full))
            {
                if (Owner is MainWindow mw)
                {
                    mw.ApplyCredentials(full);
                    StatusText.Text = "Applied";
                }
            }
        }
    }
}
