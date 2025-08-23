using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DesktopApp.Models;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;
using System.Threading;

namespace DesktopApp.Views
{
    public partial class DashboardWindow : Window, INotifyPropertyChanged
    {
        private readonly HttpClient _http = new();
        private readonly ObservableCollection<Category> _categories = new();
        public ObservableCollection<Category> Categories => _categories;
        private readonly ObservableCollection<Channel> _channels = new();
        public ObservableCollection<Channel> Channels => _channels;
        private readonly Dictionary<string, BitmapImage> _logoCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _logoSemaphore = new(6);
        private readonly ObservableCollection<EpgEntry> _epgEntries = new();
        public ObservableCollection<EpgEntry> EpgEntries => _epgEntries;
        private string _selectedCategoryName = string.Empty;
        public string SelectedCategoryName { get => _selectedCategoryName; set { if (value != _selectedCategoryName) { _selectedCategoryName = value; OnPropertyChanged(); } } }
        private string _categoriesCountText = string.Empty;
        public string CategoriesCountText { get => _categoriesCountText; set { if (value != _categoriesCountText) { _categoriesCountText = value; OnPropertyChanged(); } } }
        private Channel? _selectedChannel;
        public Channel? SelectedChannel
        {
            get => _selectedChannel;
            set
            {
                if (value != _selectedChannel)
                {
                    _selectedChannel = value;
                    OnPropertyChanged();
                    SelectedChannelName = value?.Name ?? string.Empty;
                    if (value != null)
                        _ = EnsureEpgLoadedAsync(value);
                }
            }
        }
        private string _selectedChannelName = string.Empty;
        public string SelectedChannelName { get => _selectedChannelName; set { if (value != _selectedChannelName) { _selectedChannelName = value; OnPropertyChanged(); } } }
        private string _nowProgramText = string.Empty;
        public string NowProgramText { get => _nowProgramText; set { if (value != _nowProgramText) { _nowProgramText = value; OnPropertyChanged(); } } }
        private bool _logoutRequested;
        private bool _isClosing;
        private readonly CancellationTokenSource _cts = new();

        public DashboardWindow()
        {
            InitializeComponent();
            DataContext = this;
            UserNameText.Text = Session.Username;
            Loaded += async (_, __) => await LoadCategoriesAsync();
        }

        private void Log(string text)
        {
            try
            {
                if (_isClosing || OutputText == null) return;
                OutputText.AppendText(text);
            }
            catch { /* ignore logging errors during shutdown */ }
        }

        private async void LoadCategories_Click(object sender, RoutedEventArgs e) => await LoadCategoriesAsync(); // no longer used, could be removed

        private async Task LoadCategoriesAsync()
        {
            try
            {
                var url = Session.BuildApi("get_live_categories");
                Log($"GET {url}\n");
                var json = await _http.GetStringAsync(url, _cts.Token);
                Log(json + "\n\n");
                List<Category> parsed = new();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            parsed.Add(new Category
                            {
                                Id = el.TryGetProperty("category_id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty,
                                Name = el.TryGetProperty("category_name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                ParentId = el.TryGetProperty("parent_id", out var pEl) ? pEl.GetInt32() : 0,
                                ImageUrl = el.TryGetProperty("category_image", out var imgEl) && imgEl.ValueKind == JsonValueKind.String ? imgEl.GetString() : null
                            });
                        }
                    }
                }
                catch (Exception ex) { Log("PARSE ERROR categories: " + ex.Message + "\n"); }
                _categories.Clear();
                foreach (var c in parsed) _categories.Add(c);
                CategoriesCountText = $"{_categories.Count} categories";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR: " + ex.Message + "\n"); }
        }

        private void SetGuideLoading(bool loading)
        {
            if (GuideLoadingPanel == null || GuideScroll == null) return;
            GuideLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            GuideScroll.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task LoadChannelsForCategoryAsync(Category cat)
        {
            SetGuideLoading(true);
            try
            {
                var url = Session.BuildApi("get_live_streams") + "&category_id=" + Uri.EscapeDataString(cat.Id);
                Log($"GET {url}\n");
                var json = await _http.GetStringAsync(url, _cts.Token);
                Log(json + "\n\n");
                List<Channel> parsed = new();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            parsed.Add(new Channel
                            {
                                Id = el.TryGetProperty("stream_id", out var idEl) && idEl.TryGetInt32(out var sid) ? sid : 0,
                                Name = el.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                Logo = el.TryGetProperty("stream_icon", out var iconEl) ? iconEl.GetString() : null,
                                EpgChannelId = el.TryGetProperty("epg_channel_id", out var epgEl) ? epgEl.GetString() : null
                            });
                        }
                    }
                }
                catch (Exception ex) { Log("PARSE ERROR channels: " + ex.Message + "\n"); }
                _channels.Clear();
                foreach (var c in parsed) _channels.Add(c);
                _ = Task.Run(() => PreloadLogosAsync(parsed), _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR loading channels: " + ex.Message + "\n"); }
            finally { SetGuideLoading(false); }
        }

        private async Task PreloadLogosAsync(IEnumerable<Channel> channels)
        {
            try
            {
                var tasks = channels.Where(c => !string.IsNullOrWhiteSpace(c.Logo)).Select(LoadLogoAsync);
                await Task.WhenAll(tasks);
            }
            catch { }
        }

        private async Task LoadLogoAsync(Channel channel)
        {
            if (_cts.IsCancellationRequested) return;
            var url = channel.Logo;
            if (string.IsNullOrWhiteSpace(url)) return;
            if (_logoCache.TryGetValue(url, out var cached)) { channel.LogoImage = cached; return; }
            try
            {
                await _logoSemaphore.WaitAsync(_cts.Token);
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                if (!resp.IsSuccessStatusCode) return;
                await using var ms = new MemoryStream();
                await resp.Content.CopyToAsync(ms, _cts.Token);
                if (_cts.IsCancellationRequested) return;
                ms.Position = 0;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        _logoCache[url] = bmp;
                        channel.LogoImage = bmp;
                    }
                    catch { }
                });
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { if (_logoSemaphore.CurrentCount < 6) _logoSemaphore.Release(); }
        }

        private async Task EnsureEpgLoadedAsync(Channel ch)
        {
            if (ch.EpgLoaded || ch.EpgLoading || _cts.IsCancellationRequested) return;
            await LoadEpgCurrentOnlyAsync(ch);
        }

        private async Task LoadEpgCurrentOnlyAsync(Channel ch)
        {
            ch.EpgLoading = true;
            try
            {
                if (string.IsNullOrWhiteSpace(ch.EpgChannelId) || _cts.IsCancellationRequested) { ch.EpgLoaded = true; ch.EpgLoading = false; return; }
                var url = Session.BuildApi("get_simple_data_table") + "&stream_id=" + ch.Id;
                Log($"GET {url} (current only)\n");
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                var json = await resp.Content.ReadAsStringAsync(_cts.Token);
                Log("(length=" + json.Length + ")\n\n");
                if (_cts.IsCancellationRequested) return;
                var nowUtc = DateTime.UtcNow;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("epg_listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in listings.EnumerateArray())
                        {
                            bool nowFlag = el.TryGetProperty("now_playing", out var np) && (np.ValueKind == JsonValueKind.Number ? np.GetInt32() == 1 : (np.ValueKind == JsonValueKind.String && np.GetString() == "1"));
                            DateTime start = DateTime.MinValue, end = DateTime.MinValue;
                            if (!nowFlag)
                            {
                                start = GetUnix(el, "start_timestamp");
                                end = GetUnix(el, "stop_timestamp");
                                if (start == DateTime.MinValue || end == DateTime.MinValue) continue;
                                if (!(nowUtc >= start && nowUtc < end)) continue;
                            }
                            string titleRaw = el.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? string.Empty : string.Empty;
                            string descRaw = el.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? string.Empty : string.Empty;
                            string title = DecodeMaybeBase64(titleRaw);
                            string desc = DecodeMaybeBase64(descRaw);
                            if (!nowFlag)
                            {
                                if (start == DateTime.MinValue) start = nowUtc;
                                if (end == DateTime.MinValue) end = start.AddMinutes(1);
                            }
                            ch.NowTitle = title;
                            ch.NowDescription = desc;
                            if (start == DateTime.MinValue || end == DateTime.MinValue) { start = nowUtc; end = nowUtc.AddMinutes(1); }
                            ch.NowTimeRange = $"{start.ToLocalTime():HH:mm} - {end.ToLocalTime():HH:mm}"; // simplified
                            NowProgramText = $"Now: {ch.NowTitle} ({ch.NowTimeRange})";
                            break;
                        }
                    }
                }
                catch (Exception ex) { Log("PARSE ERROR (current epg): " + ex.Message + "\n"); }
                ch.EpgLoaded = true;
            }
            catch (OperationCanceledException) { ch.EpgLoaded = true; }
            catch (Exception ex) { Log("ERROR loading epg: " + ex.Message + "\n"); ch.EpgLoaded = true; }
            finally { ch.EpgLoading = false; }
        }

        private static DateTime GetUnix(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var tsEl))
            {
                var str = tsEl.GetString();
                if (long.TryParse(str, out var unix) && unix > 0)
                    return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }
            return DateTime.MinValue;
        }

        private static string DecodeMaybeBase64(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (raw.Length % 4 == 0 && raw.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '='))
            {
                try
                {
                    var bytes = Convert.FromBase64String(raw);
                    var txt = System.Text.Encoding.UTF8.GetString(bytes);
                    if (txt.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) return raw;
                    return txt;
                }
                catch { }
            }
            return raw;
        }

        private async void LoadStreams_Click(object sender, RoutedEventArgs e) => await RunApiCall("get_live_streams");

        private async Task RunApiCall(string action)
        {
            try
            {
                var url = Session.BuildApi(action);
                Log($"GET {url}\n");
                var json = await _http.GetStringAsync(url, _cts.Token);
                if (json.Length > 50_000) json = json[..50_000] + "...<truncated>";
                Log(json + "\n\n");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ERROR: " + ex.Message + "\n"); }
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            _logoutRequested = true;
            _cts.Cancel();
            Session.Username = Session.Password = string.Empty;
            if (Owner is MainWindow mw)
            {
                Application.Current.MainWindow = mw;
                mw.Show();
            }
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            _cts.Cancel();
            base.OnClosed(e);
            _cts.Dispose();
            if (!_logoutRequested)
            {
                if (Owner is MainWindow mw)
                {
                    try { mw.Close(); } catch { }
                }
                Application.Current.Shutdown();
            }
        }

        private async void CategoryTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Category cat)
            {
                SelectedCategoryName = cat.Name;
                await LoadChannelsForCategoryAsync(cat);
                // Switch to Guide tab automatically
                try { MainTabs.SelectedIndex = 1; } catch { }
            }
        }

        private async void ChannelTile_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Channel ch)
                await EnsureEpgLoadedAsync(ch);
        }

        private void ChannelTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Channel ch)
                SelectedChannel = ch;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
