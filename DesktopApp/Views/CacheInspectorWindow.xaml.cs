using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DesktopApp.Services;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Views;

public partial class CacheInspectorWindow : Window
{
    private readonly ICacheService _cacheService;
    private readonly ILogger<CacheInspectorWindow> _logger;
    private readonly ObservableCollection<CacheEntryInfoViewModel> _cacheEntries = new();
    private readonly CollectionViewSource _collectionViewSource = new();

    public CacheInspectorWindow(ICacheService cacheService, ILogger<CacheInspectorWindow> logger)
    {
        _cacheService = cacheService;
        _logger = logger;

        InitializeComponent();
        SetupDataBinding();
        _ = LoadCacheInfoAsync();
    }

    private void SetupDataBinding()
    {
        _collectionViewSource.Source = _cacheEntries;
        _collectionViewSource.Filter += CollectionViewSource_Filter;
        CacheEntriesGrid.ItemsSource = _collectionViewSource.View;
    }

    private void CollectionViewSource_Filter(object sender, FilterEventArgs e)
    {
        if (e.Item is CacheEntryInfoViewModel entry)
        {
            var filterText = FilterTextBox.Text?.ToLower() ?? "";
            var showExpired = ShowExpiredCheckBox.IsChecked == true;

            var matchesFilter = string.IsNullOrEmpty(filterText) ||
                               entry.Key.ToLower().Contains(filterText) ||
                               entry.DataType.ToLower().Contains(filterText);

            var matchesExpired = showExpired || !entry.IsExpired;

            e.Accepted = matchesFilter && matchesExpired;
        }
    }

    private async Task LoadCacheInfoAsync()
    {
        try
        {
            RefreshButton.IsEnabled = false;
            RefreshButton.Content = "🔄 Loading...";

            if (_cacheService is PersistentCacheService persistentCache)
            {
                var cacheInfo = await persistentCache.GetCacheInfoAsync();

                // Update statistics
                MemoryImageCountText.Text = $"Images in memory: {cacheInfo.MemoryImageCount:N0}";
                MemoryDataCountText.Text = $"Data entries in memory: {cacheInfo.MemoryDataCount:N0}";
                DiskImageCountText.Text = $"Images on disk: {cacheInfo.DiskImageCount:N0}";
                DiskDataCountText.Text = $"Data files on disk: {cacheInfo.DiskDataCount:N0}";
                TotalSizeText.Text = $"Total size: {FormatBytes(cacheInfo.TotalSizeBytes)}";
                CacheDirectoryText.Text = $"Location: {cacheInfo.CacheDirectory}";

                // Update entries
                _cacheEntries.Clear();
                foreach (var entry in cacheInfo.Entries)
                {
                    _cacheEntries.Add(new CacheEntryInfoViewModel(entry));
                }

                _collectionViewSource.View.Refresh();
                _logger.LogInformation("Cache info loaded: {ImageCount} images, {DataCount} data entries, {TotalSize} bytes",
                    cacheInfo.DiskImageCount, cacheInfo.DiskDataCount, cacheInfo.TotalSizeBytes);
            }
            else
            {
                // Fallback for basic cache service
                MemoryImageCountText.Text = $"Images in memory: {_cacheService.ImageCacheCount:N0}";
                MemoryDataCountText.Text = $"Data entries in memory: {_cacheService.DataCacheCount:N0}";
                DiskImageCountText.Text = "Images on disk: N/A";
                DiskDataCountText.Text = "Data files on disk: N/A";
                TotalSizeText.Text = $"Estimated memory: {FormatBytes(_cacheService.EstimatedMemoryUsage)}";
                CacheDirectoryText.Text = "Location: In-memory only";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load cache info");
            MessageBox.Show($"Failed to load cache information: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "🔄 Refresh";
        }
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _collectionViewSource.View.Refresh();
    }

    private void ShowExpiredCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _collectionViewSource.View.Refresh();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadCacheInfoAsync();
    }

    private async void ClearExpiredButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = MessageBox.Show("Clear all expired cache entries?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                ClearExpiredButton.IsEnabled = false;
                ClearExpiredButton.Content = "🧹 Clearing...";

                await _cacheService.ClearExpiredDataAsync();
                await LoadCacheInfoAsync();

                MessageBox.Show("Expired cache entries cleared successfully!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear expired cache");
            MessageBox.Show($"Failed to clear expired cache: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ClearExpiredButton.IsEnabled = true;
            ClearExpiredButton.Content = "🧹 Clear Expired";
        }
    }

    private void ClearImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = MessageBox.Show("Clear all cached images? This will remove all images from memory and disk.",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _cacheService.ClearImageCache();
                _ = LoadCacheInfoAsync();

                MessageBox.Show("Image cache cleared successfully!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear image cache");
            MessageBox.Show($"Failed to clear image cache: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedItems = CacheEntriesGrid.SelectedItems.Cast<CacheEntryInfoViewModel>().ToList();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("Please select one or more cache entries to delete.", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Delete {selectedItems.Count} selected cache entries?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                DeleteSelectedButton.IsEnabled = false;
                DeleteSelectedButton.Content = "🗑️ Deleting...";

                _logger.LogInformation("Deleting {Count} cache entries using service: {ServiceType}",
                    selectedItems.Count, _cacheService.GetType().Name);

                foreach (var item in selectedItems)
                {
                    _logger.LogInformation("Deleting cache entry: {Key}", item.Key);
                    await _cacheService.RemoveDataAsync(item.Key);

                    // For PersistentCacheService, also manually verify file deletion
                    if (_cacheService is PersistentCacheService)
                    {
                        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        var cacheDirectory = Path.Combine(appDataPath, "IPTV-Desktop-Browser", "Cache");
                        var safeFileName = GetSafeFileName(item.Key);
                        var dataFile = Path.Combine(cacheDirectory, $"{safeFileName}.json");

                        if (File.Exists(dataFile))
                        {
                            try
                            {
                                File.Delete(dataFile);
                                _logger.LogInformation("Manually deleted cache file: {File}", dataFile);
                            }
                            catch (Exception fileEx)
                            {
                                _logger.LogWarning(fileEx, "Failed to manually delete cache file: {File}", dataFile);
                            }
                        }
                    }
                }

                await LoadCacheInfoAsync();

                MessageBox.Show($"{selectedItems.Count} cache entries deleted successfully!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete selected cache entries");
            MessageBox.Show($"Failed to delete cache entries: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DeleteSelectedButton.IsEnabled = true;
            DeleteSelectedButton.Content = "🗑️ Delete Selected";
        }
    }

    private void OpenCacheFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_cacheService is PersistentCacheService)
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var cacheDirectory = Path.Combine(appDataPath, "IPTV-Desktop-Browser", "Cache");

                if (Directory.Exists(cacheDirectory))
                {
                    Process.Start("explorer.exe", cacheDirectory);
                }
                else
                {
                    MessageBox.Show("Cache directory does not exist yet.", "Directory Not Found",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                MessageBox.Show("Cache directory is only available for persistent cache service.", "Not Available",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open cache folder");
            MessageBox.Show($"Failed to open cache folder: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return string.Format("{0:n1} {1}", number, suffixes[counter]);
    }

    private static string GetSafeFileName(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var result = string.Join("_", input.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return result.Length > 200 ? result.Substring(0, 200) : result;
    }
}

public class CacheEntryInfoViewModel : INotifyPropertyChanged
{
    public CacheEntryInfoViewModel(CacheEntryInfo entry)
    {
        Key = entry.Key;
        DataType = entry.DataType;
        Created = entry.Created;
        ExpiresAt = entry.ExpiresAt;
        AccessCount = entry.AccessCount;
        IsExpired = entry.IsExpired;
        SizeBytes = entry.SizeBytes;
    }

    public string Key { get; set; }
    public string DataType { get; set; }
    public DateTime Created { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int AccessCount { get; set; }
    public bool IsExpired { get; set; }
    public long SizeBytes { get; set; }

    public string SizeBytesFormatted
    {
        get
        {
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024.0:F1} KB";
            return $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}