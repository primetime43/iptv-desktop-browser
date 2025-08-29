using System.Windows;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Windows.Media;
using DesktopApp.Models;
using DesktopApp.Views;

namespace DesktopApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false // so we can display redirects if present
        });

        private static readonly SolidColorBrush BrushInfo = new(Color.FromRgb(0x6C, 0x82, 0x98));
        private static readonly SolidColorBrush BrushSuccess = new(Color.FromRgb(0x4C, 0xD1, 0x64));
        private static readonly SolidColorBrush BrushError = new(Color.FromRgb(0xE5, 0x60, 0x60));

        public MainWindow()
        {
            InitializeComponent();
            TryLoadStoredCredentials();
        }

        public void ApplyCredentials(CredentialProfile profile)
        {
            ServerTextBox.Text = profile.Server;
            PortTextBox.Text = profile.Port.ToString();
            SslCheckBox.IsChecked = profile.UseSsl;
            UsernameTextBox.Text = profile.Username;
            PasswordBoxInput.Password = profile.Password; // password is filled from TryGet
            RememberCheckBox.IsChecked = true;
            SetStatus($"Loaded {profile.Username}@{profile.Server}", BrushInfo);
        }

        private void OpenCredentialManager_Click(object sender, RoutedEventArgs e)
        {
            var mgr = new CredentialManagerWindow { Owner = this };
            mgr.ShowDialog();
        }

        private void TryLoadStoredCredentials()
        {
            if (CredentialStore.TryLoad(out var server, out var port, out var useSsl, out var user, out var pass))
            {
                ServerTextBox.Text = server;
                PortTextBox.Text = port == 0 ? string.Empty : port.ToString();
                SslCheckBox.IsChecked = useSsl;
                UsernameTextBox.Text = user;
                PasswordBoxInput.Password = pass;
                RememberCheckBox.IsChecked = true;
                SetStatus("Loaded saved credentials.", BrushInfo);
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            ClearDiagnosticsIfHidden();

            var serverRaw = ServerTextBox.Text?.Trim();
            var username = UsernameTextBox.Text?.Trim();
            var password = PasswordBoxInput.Password?.Trim();
            var portText = PortTextBox.Text?.Trim();
            var ssl = SslCheckBox.IsChecked == true;

            SetStatus("Validating...", BrushInfo);
            LoginButton.IsEnabled = false;

            if (string.IsNullOrWhiteSpace(serverRaw) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                SetStatus("Missing required fields.", BrushError);
                LoginButton.IsEnabled = true;
                return;
            }

            if (!int.TryParse(portText, out var port))
            {
                SetStatus("Port must be numeric.", BrushError);
                LoginButton.IsEnabled = true;
                return;
            }

            if (ssl && (portText == "80" || port == 80))
            {
                port = 443; // default SSL fallback
            }

            string host = NormalizeHost(serverRaw);
            var scheme = ssl ? "https" : "http";
            var baseUrl = $"{scheme}://{host}:{port}";

            var candidateUrls = new[]
            {
                $"{baseUrl}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}",
                (port == 80 || port == 443) ? $"{scheme}://{host}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}" : null,
                $"{baseUrl}/panel_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}",
                $"{baseUrl}/get.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&type=m3u"
            };

            var sw = Stopwatch.StartNew();
            var diag = new StringBuilder();
            diag.AppendLine($"Timestamp: {DateTime.UtcNow:O}");
            diag.AppendLine($"Host: {host}");
            diag.AppendLine($"Scheme: {scheme}  Port: {port}");
            diag.AppendLine($"Username: {username}\n");

            bool authed = false;
            string? lastError = null;
            UserInfo? successUserInfo = null;

            foreach (var url in candidateUrls)
            {
                if (url is null) continue;
                diag.AppendLine($"REQUEST => GET {url}");
                HttpResponseMessage? resp = null;
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12));
                    resp = await _http.GetAsync(url, cts.Token);
                    var elapsed = sw.ElapsedMilliseconds;
                    diag.AppendLine($"Response: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}  ({elapsed} ms)");
                    foreach (var h in resp.Headers)
                        diag.AppendLine($"  H: {h.Key}: {string.Join(",", h.Value)}");
                    foreach (var h in resp.Content.Headers)
                        diag.AppendLine($"  CH: {h.Key}: {string.Join(",", h.Value)}");
                    var body = await resp.Content.ReadAsStringAsync();
                    var snippet = body.Length > 4000 ? body[..4000] + "...<truncated>" : body;
                    diag.AppendLine("---- BODY START ----");
                    diag.AppendLine(snippet);
                    diag.AppendLine("---- BODY END ----");

                    if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
                    {
                        if (body.TrimStart().StartsWith("{"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(body);
                                if (doc.RootElement.TryGetProperty("user_info", out var ui))
                                {
                                    bool isAuth = false;
                                    if (ui.TryGetProperty("auth", out var authEl))
                                    {
                                        isAuth = authEl.ValueKind switch
                                        {
                                            JsonValueKind.True => true,
                                            JsonValueKind.Number => authEl.TryGetInt32(out var iv) && iv == 1,
                                            JsonValueKind.String => authEl.GetString() is string s && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)),
                                            _ => false
                                        };
                                    }
                                    if (isAuth)
                                    {
                                        authed = true;
                                        successUserInfo = new UserInfo
                                        {
                                            status = ui.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null,
                                            exp_date = ui.TryGetProperty("exp_date", out var exp) && exp.ValueKind == JsonValueKind.String ? exp.GetString() : null,
                                            is_trial = ui.TryGetProperty("is_trial", out var tr) && tr.ValueKind == JsonValueKind.String ? tr.GetString() : (ui.TryGetProperty("is_trial", out var tr2) && tr2.ValueKind == JsonValueKind.Number ? tr2.GetRawText() : null),
                                            username = ui.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String ? un.GetString() : username,
                                            max_connections = ui.TryGetProperty("max_connections", out var mc) && (mc.ValueKind == JsonValueKind.String || mc.ValueKind == JsonValueKind.Number) ? mc.ToString() : null,
                                            active_cons = ui.TryGetProperty("active_cons", out var ac) && (ac.ValueKind == JsonValueKind.String || ac.ValueKind == JsonValueKind.Number) ? ac.ToString() : null,
                                            password = ui.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String ? pw.GetString() : null,
                                            created_at = ui.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() : null,
                                            message = ui.TryGetProperty("message", out var msgp) && msgp.ValueKind == JsonValueKind.String ? msgp.GetString() : null
                                        };
                                        SetStatus(BuildSuccessStatus(successUserInfo, sw.ElapsedMilliseconds), BrushSuccess);
                                        break; // stop trying more URLs
                                    }
                                    else
                                    {
                                        lastError = ui.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() :
                                                    ui.TryGetProperty("status", out var st2) && st2.ValueKind == JsonValueKind.String ? st2.GetString() :
                                                    "Auth failed";
                                    }
                                }
                                else
                                {
                                    lastError = "Missing user_info in JSON";
                                }
                            }
                            catch (JsonException jx)
                            {
                                lastError = "Invalid JSON: " + jx.Message;
                            }
                        }
                        else if (body.Contains("#EXTM3U"))
                        {
                            lastError = "M3U playlist returned (player_api not available).";
                        }
                        else
                        {
                            lastError = "Unexpected body format.";
                        }
                    }
                    else
                    {
                        lastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                        if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
                            lastError += $" -> Location: {resp.Headers.Location}";
                    }
                }
                catch (HttpRequestException hre) when (hre.InnerException is SocketException se)
                {
                    lastError = se.SocketErrorCode switch
                    {
                        SocketError.HostNotFound => "DNS lookup failed (host not found)",
                        SocketError.NetworkUnreachable => "Network unreachable",
                        SocketError.TimedOut => "Connection timed out",
                        _ => $"Socket error: {se.SocketErrorCode}"
                    };
                    diag.AppendLine("NETWORK ERROR: " + lastError);
                }
                catch (TaskCanceledException)
                {
                    lastError = "Timeout";
                    diag.AppendLine("TIMEOUT after 12s");
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    diag.AppendLine("EXCEPTION: " + ex);
                }
                finally
                {
                    resp?.Dispose();
                    diag.AppendLine();
                }
            }

            sw.Stop();
            DiagnosticsText.Text = diag.ToString();
            DiagnosticsExpander.Visibility = Visibility.Visible;
            if (!authed) DiagnosticsExpander.IsExpanded = true;

            if (!authed)
            {
                SetStatus((lastError is null ? "Login failed (unknown)." : lastError) + $" ({sw.ElapsedMilliseconds} ms)", BrushError);
                LoginButton.IsEnabled = true;
                return;
            }

            // Persist session state
            Session.Host = host;
            Session.Port = port;
            Session.UseSsl = ssl;
            Session.Username = username!;
            Session.Password = password!;
            Session.UserInfo = successUserInfo;

            if (RememberCheckBox.IsChecked == true)
                CredentialStore.SaveOrUpdate(serverRaw!, port, ssl, username!, password!);

            // Launch dashboard directly; settings accessible from dashboard
            var dash = new DashboardWindow { Owner = this };
            dash.Show();
            this.Hide();

            LoginButton.IsEnabled = true;
        }

        private void SetStatus(string text, Brush brush)
        {
            StatusText.Text = text;
            StatusText.Foreground = brush;
        }

        private string NormalizeHost(string serverRaw)
        {
            string host = serverRaw;
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host[7..];
            if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host[8..];
            host = host.TrimEnd('/');
            return host;
        }

        private string BuildSuccessStatus(UserInfo userInfo, long elapsedMs)
        {
            var sb = new StringBuilder();
            sb.Append("Login OK");
            if (!string.IsNullOrEmpty(userInfo.status)) sb.Append($" | Status: {userInfo.status}");
            if (!string.IsNullOrEmpty(userInfo.is_trial)) sb.Append(userInfo.is_trial == "1" || userInfo.is_trial.Equals("true", StringComparison.OrdinalIgnoreCase) ? " | trial" : " | paid");
            if (!string.IsNullOrEmpty(userInfo.exp_date) && long.TryParse(userInfo.exp_date, out var unix) && unix > 0)
            {
                try { var dt = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; sb.Append($" | Expires UTC: {dt:yyyy-MM-dd HH:mm}"); } catch { }
            }
            sb.Append($" | {elapsedMs} ms");
            return sb.ToString();
        }

        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(DiagnosticsText.Text); } catch { }
        }

        private void ClearDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsText.Clear();
        }

        private void ClearDiagnosticsIfHidden()
        {
            if (DiagnosticsExpander.Visibility != Visibility.Visible)
                DiagnosticsText.Clear();
        }
    }
}