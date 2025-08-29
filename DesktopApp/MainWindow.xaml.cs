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
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Xml;

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

        private SessionMode CurrentMode => ModeXtreamRadio.IsChecked == true ? SessionMode.Xtream : SessionMode.M3u;

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (XtreamPanel == null || M3uPanel == null) return;
            if (CurrentMode == SessionMode.Xtream)
            {
                XtreamPanel.Visibility = Visibility.Visible;
                M3uPanel.Visibility = Visibility.Collapsed;
                ModeHeaderText.Text = "Xtream Login";
                HelpHeader.Text = "What is Xtream Codes?";
                HelpText.Text = "Enter the portal URL (or IP), your account username & password provided by your IPTV provider. SSL will prefix https:// and default port 443 if selected.";
            }
            else
            {
                XtreamPanel.Visibility = Visibility.Collapsed;
                M3uPanel.Visibility = Visibility.Visible;
                ModeHeaderText.Text = "M3U Playlist";
                HelpHeader.Text = "What is M3U?";
                HelpText.Text = "Provide a remote URL or local file path to an .m3u / .m3u8 playlist. Optionally supply XMLTV URL for EPG.";
            }
            StatusText.Text = string.Empty;
            DiagnosticsText.Clear();
            DiagnosticsExpander.Visibility = Visibility.Collapsed;
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
            if (CurrentMode != SessionMode.Xtream)
            {
                SetStatus("Switch to Xtream mode to login with credentials.", BrushInfo);
                return;
            }
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
            if (ssl && (portText == "80" || port == 80)) { port = 443; }
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
            bool authed = false; string? lastError = null; UserInfo? successUserInfo = null;
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
                    foreach (var h in resp.Headers) diag.AppendLine($"  H: {h.Key}: {string.Join(",", h.Value)}");
                    foreach (var h in resp.Content.Headers) diag.AppendLine($"  CH: {h.Key}: {string.Join(",", h.Value)}");
                    var body = await resp.Content.ReadAsStringAsync();
                    var snippet = body.Length > 4000 ? body[..4000] + "...<truncated>" : body;
                    diag.AppendLine("---- BODY START ----\n" + snippet + "\n---- BODY END ----");
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
                                        break;
                                    }
                                    else
                                    {
                                        lastError = ui.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : ui.TryGetProperty("status", out var st2) && st2.ValueKind == JsonValueKind.String ? st2.GetString() : "Auth failed";
                                    }
                                }
                                else lastError = "Missing user_info in JSON";
                            }
                            catch (JsonException jx) { lastError = "Invalid JSON: " + jx.Message; }
                        }
                        else if (body.Contains("#EXTM3U")) { lastError = "M3U playlist returned (player_api not available)."; }
                        else { lastError = "Unexpected body format."; }
                    }
                    else
                    {
                        lastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                        if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null) lastError += $" -> Location: {resp.Headers.Location}";
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
                { lastError = "Timeout"; diag.AppendLine("TIMEOUT after 12s"); }
                catch (Exception ex)
                { lastError = ex.Message; diag.AppendLine("EXCEPTION: " + ex); }
                finally { resp?.Dispose(); diag.AppendLine(); }
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
            Session.Mode = SessionMode.Xtream;
            Session.Host = host; Session.Port = port; Session.UseSsl = ssl; Session.Username = username!; Session.Password = password!; Session.UserInfo = successUserInfo;
            if (RememberCheckBox.IsChecked == true) CredentialStore.SaveOrUpdate(serverRaw!, port, ssl, username!, password!);
            var dash = new DashboardWindow { Owner = this }; dash.Show(); this.Hide(); LoginButton.IsEnabled = true;
        }

        private async void LoadM3uButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentMode != SessionMode.M3u) return;
            var playlistPath = M3uUrlTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(playlistPath)) { SetStatus("Enter a playlist URL or file path.", BrushError); return; }
            var xmlPath = XmltvUrlTextBox.Text?.Trim();
            SetStatus("Loading playlist...", BrushInfo);
            LoadM3uButton.IsEnabled = false;
            try
            {
                string playlistContent;
                if (File.Exists(playlistPath)) playlistContent = await File.ReadAllTextAsync(playlistPath);
                else
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                    playlistContent = await _http.GetStringAsync(playlistPath, cts.Token);
                }
                var entries = ParseM3u(playlistContent).ToList();
                if (entries.Count == 0) { SetStatus("No channels found.", BrushError); return; }
                Session.Mode = SessionMode.M3u; Session.ResetM3u(); Session.PlaylistChannels.AddRange(entries);
                SetStatus($"Loaded {entries.Count} channels. Parsing EPG...", BrushSuccess);
                if (!string.IsNullOrWhiteSpace(xmlPath))
                {
                    _ = Task.Run(async () => await LoadXmltvAsync(xmlPath));
                }
                var dash = new DashboardWindow { Owner = this }; dash.Show(); this.Hide();
            }
            catch (Exception ex) { SetStatus("Failed: " + ex.Message, BrushError); }
            finally { LoadM3uButton.IsEnabled = true; }
        }

        private async Task LoadXmltvAsync(string source)
        {
            try
            {
                string xml;
                if (File.Exists(source)) xml = await File.ReadAllTextAsync(source);
                else
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(40));
                    xml = await _http.GetStringAsync(source, cts.Token);
                }
                ParseXmltv(xml);
                Session.RaiseM3uEpgUpdated();
            }
            catch (Exception ex)
            {
                // Could log to diagnostics file in future
            }
        }

        private void ParseXmltv(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreProcessingInstructions = true };
                using var reader = XmlReader.Create(new StringReader(xml), settings);
                var byChannel = Session.M3uEpgByChannel;
                byChannel.Clear();
                EpgEntry? BuildEntry(string chId, string start, string stop, string title, string desc)
                {
                    if (!TryParseXmltvDate(start, out var s) || !TryParseXmltvDate(stop, out var e)) return null;
                    return new EpgEntry { StartUtc = s, EndUtc = e, Title = title, Description = desc };
                }
                string currentElement = string.Empty;
                string channelId = string.Empty; string start = string.Empty; string stop = string.Empty; string title = string.Empty; string desc = string.Empty;
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        currentElement = reader.Name;
                        if (reader.Name == "programme")
                        {
                            channelId = reader.GetAttribute("channel") ?? string.Empty;
                            start = reader.GetAttribute("start") ?? string.Empty;
                            stop = reader.GetAttribute("stop") ?? string.Empty;
                            title = desc = string.Empty;
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.Text)
                    {
                        if (currentElement == "title") title += reader.Value;
                        else if (currentElement == "desc") desc += reader.Value;
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "programme")
                    {
                        var entry = BuildEntry(channelId, start, stop, title.Trim(), desc.Trim());
                        if (entry != null)
                        {
                            if (!byChannel.TryGetValue(channelId, out var list)) { list = new List<EpgEntry>(); byChannel[channelId] = list; }
                            list.Add(entry);
                        }
                    }
                }
                foreach (var kv in byChannel) kv.Value.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            }
            catch { }
        }

        private bool TryParseXmltvDate(string raw, out DateTime utc)
        {
            // Formats like 20240101083000 +0000 or 20240101083000 +0100 / 20240101083000
            utc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(raw) || raw.Length < 14) return false;
            try
            {
                var dtPart = raw.Substring(0, 14); // yyyyMMddHHmmss
                if (!DateTime.TryParseExact(dtPart, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var baseLocal)) return false;
                TimeSpan offset = TimeSpan.Zero;
                var spaceIdx = raw.IndexOf(' ');
                if (spaceIdx > 0 && raw.Length >= spaceIdx + 6)
                {
                    var offStr = raw.Substring(spaceIdx + 1, 5); // +HHMM
                    if ((offStr[0] == '+' || offStr[0] == '-') && int.TryParse(offStr.Substring(1, 2), out var hh) && int.TryParse(offStr.Substring(3, 2), out var mm))
                    {
                        offset = new TimeSpan(hh, mm, 0);
                        if (offStr[0] == '-') offset = -offset;
                    }
                }
                utc = DateTime.SpecifyKind(baseLocal - offset, DateTimeKind.Utc);
                return true;
            }
            catch { return false; }
        }

        private IEnumerable<PlaylistEntry> ParseM3u(string content)
        {
            var list = new List<PlaylistEntry>();
            if (string.IsNullOrWhiteSpace(content)) return list;
            var lines = content.Replace('\r', '\n').Split('\n');
            if (lines.Length == 0 || !lines[0].Contains("#EXTM3U")) return list;
            int id = 1;
            string? pendingExtInf = null;
            foreach (var raw in lines.Skip(1))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) { pendingExtInf = line; continue; }
                if (line.StartsWith("#")) continue; // other tags
                // line now should be a URL
                string url = line;
                string name = url;
                string logo = string.Empty;
                string cat = string.Empty;
                string tvgId = string.Empty; string tvgName = string.Empty;
                if (pendingExtInf != null)
                {
                    // Example: #EXTINF:-1 tvg-id="" tvg-name="" group-title="News" tvg-logo="http://...",Channel Name
                    var parts = pendingExtInf.Split(',', 2);
                    if (parts.Length == 2) name = parts[1].Trim();
                    var attrs = parts[0];
                    string Extract(string key)
                    {
                        var idx = attrs.IndexOf(key + "=\"", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            idx += key.Length + 2;
                            var end = attrs.IndexOf('"', idx);
                            if (end > idx) return attrs[idx..end];
                        }
                        return string.Empty;
                    }
                    tvgId = Extract("tvg-id");
                    tvgName = Extract("tvg-name");
                    logo = Extract("tvg-logo");
                    cat = Extract("group-title");
                }
                list.Add(new PlaylistEntry { Id = id++, Name = name, StreamUrl = url, Logo = string.IsNullOrWhiteSpace(logo) ? null : logo, Category = cat ?? string.Empty, TvgId = tvgId, TvgName = tvgName });
                pendingExtInf = null;
            }
            return list;
        }

        private void SetStatus(string text, Brush brush)
        { StatusText.Text = text; StatusText.Foreground = brush; }

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
            { try { var dt = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; sb.Append($" | Expires UTC: {dt:yyyy-MM-dd HH:mm}"); } catch { } }
            sb.Append($" | {elapsedMs} ms");
            return sb.ToString();
        }

        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(DiagnosticsText.Text); } catch { } }
        private void ClearDiagnostics_Click(object sender, RoutedEventArgs e) { DiagnosticsText.Clear(); }
        private void ClearDiagnosticsIfHidden() { if (DiagnosticsExpander.Visibility != Visibility.Visible) DiagnosticsText.Clear(); }
    }
}