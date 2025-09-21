using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using DesktopApp.Models;

namespace DesktopApp.Services;

public class PersistentCacheService : ICacheService
{
    private readonly IHttpService _httpService;
    private readonly ILogger<PersistentCacheService> _logger;

    // Cache operation status
    public event Action<string>? CacheOperationStatusChanged;
    public string CurrentCacheStatus { get; private set; } = "Ready";

    // In-memory cache for quick access
    private readonly ConcurrentDictionary<string, BitmapImage> _imageCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _dataCache = new();
    private readonly SemaphoreSlim _imageSemaphore = new(10);

    // File-based storage
    private readonly string _cacheDirectory;
    private readonly string _imageDirectory;
    private readonly string _dataDirectory;
    private readonly string _indexFile;

    // Cache settings
    private const int MaxImageCacheSize = 500;
    private const int MaxDataCacheSize = 1000;
    private const long MaxCacheSizeBytes = 100 * 1024 * 1024; // 100MB
    private static readonly TimeSpan DefaultDataExpiration = TimeSpan.FromMinutes(30);

    public PersistentCacheService(IHttpService httpService, ILogger<PersistentCacheService> logger)
    {
        _httpService = httpService;
        _logger = logger;

        // Setup cache directories
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheDirectory = Path.Combine(appDataPath, "IPTV-Desktop-Browser", "Cache");
        _imageDirectory = Path.Combine(_cacheDirectory, "Images");
        _dataDirectory = Path.Combine(_cacheDirectory, "Data");
        _indexFile = Path.Combine(_cacheDirectory, "cache_index.json");

        // Create directories
        Directory.CreateDirectory(_imageDirectory);
        Directory.CreateDirectory(_dataDirectory);

        _logger.LogInformation("🏗️ PersistentCacheService created - Cache dir: {CacheDir}", _cacheDirectory);

        // Load existing cache
        _ = Task.Run(LoadCacheFromDiskAsync);

        // Start cleanup timer
        var cleanupTimer = new Timer(async _ => await CleanupExpiredItemsAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
    }

    public async Task<BitmapImage?> GetImageAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var cacheKey = GetSafeFileName(url);

        // Check in-memory cache first
        if (_imageCache.TryGetValue(cacheKey, out var cachedImage))
        {
            _logger.LogInformation("🎯 Image cache HIT (memory): {Url}", url);
            return cachedImage;
        }

        // Check file cache (try to load synchronously for immediate display)
        var imageFile = Path.Combine(_imageDirectory, $"{cacheKey}.jpg");
        if (File.Exists(imageFile))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imageFile);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                _imageCache[cacheKey] = bitmap;
                _logger.LogInformation("🎯 Image cache HIT (disk): {Url}", url);
                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached image from disk: {File}", imageFile);
                // Delete corrupted file in background
                _ = Task.Run(() =>
                {
                    try { File.Delete(imageFile); } catch { }
                });
            }
        }

        // Download and cache
        await _imageSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring semaphore
            if (_imageCache.TryGetValue(cacheKey, out cachedImage))
                return cachedImage;

            _logger.LogInformation("🔄 Image cache MISS - Downloading: {Url}", url);
            var imageBytes = await _httpService.GetByteArrayAsync(url, cancellationToken);

            if (imageBytes.Length == 0)
                return null;

            // Save to disk
            await File.WriteAllBytesAsync(imageFile, imageBytes, cancellationToken);

            // Load into memory
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(imageBytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            // Apply size limits
            if (_imageCache.Count >= MaxImageCacheSize)
            {
                var keysToRemove = _imageCache.Keys.Take(MaxImageCacheSize / 10).ToList();
                foreach (var key in keysToRemove)
                {
                    _imageCache.TryRemove(key, out _);
                }
            }

            _imageCache[cacheKey] = bmp;
            _logger.LogInformation("💾 Image cached to disk and memory: {Url}", url);
            return bmp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download and cache image: {Url}", url);
            return null;
        }
        finally
        {
            _imageSemaphore.Release();
        }
    }

    public async Task<BitmapImage?> GetChannelLogoAsync(int channelId, string logoUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logoUrl))
            return null;

        // Use channel_id as the primary cache key for better cache matching
        var channelCacheKey = $"channel_logo_{channelId}";
        var urlCacheKey = GetSafeFileName(logoUrl);

        // Check in-memory cache first using channel_id
        if (_imageCache.TryGetValue(channelCacheKey, out var cachedImage))
        {
            _logger.LogInformation("📱 CACHE HIT: Channel logo loaded from MEMORY cache: Channel {ChannelId} (no download needed)", channelId);
            return cachedImage;
        }

        // Check file cache using channel_id first
        var channelImageFile = Path.Combine(_imageDirectory, $"{channelCacheKey}.jpg");
        if (File.Exists(channelImageFile))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(channelImageFile);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                _imageCache[channelCacheKey] = bitmap;
                _logger.LogInformation("📱 CACHE HIT: Channel logo loaded from DISK cache: Channel {ChannelId} (no download needed)", channelId);
                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached channel logo for channel {ChannelId}", channelId);
            }
        }

        // Check if we have the logo cached by URL (for migration from old cache)
        var urlImageFile = Path.Combine(_imageDirectory, $"{urlCacheKey}.jpg");
        if (File.Exists(urlImageFile))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(urlImageFile);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                // Cache using channel_id for future requests
                _imageCache[channelCacheKey] = bitmap;

                // Copy file to channel-based name for future use
                _ = Task.Run(async () =>
                {
                    try
                    {
                        File.Copy(urlImageFile, channelImageFile, true);
                        _logger.LogInformation("📁 Migrated logo cache from URL to channel_id: {ChannelId}", channelId);
                    }
                    catch { }
                });

                _logger.LogInformation("📱 CACHE HIT: Channel logo loaded from URL cache (migrating): Channel {ChannelId} (no download needed)", channelId);
                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate logo cache for channel {ChannelId}", channelId);
            }
        }

        // No cache hit - download the image
        _logger.LogInformation("🌐 DOWNLOAD: Channel logo cache MISS - Downloading from server: Channel {ChannelId} from {Url}", channelId, logoUrl);
        SetCacheStatus($"📥 Downloading logo for channel {channelId}...");

        // Use semaphore to limit concurrent downloads
        await _imageSemaphore.WaitAsync(cancellationToken);
        try
        {
            var imageData = await _httpService.GetByteArrayAsync(logoUrl, cancellationToken);

            // Create BitmapImage from downloaded data
            var downloadedBitmap = new BitmapImage();
            downloadedBitmap.BeginInit();
            downloadedBitmap.StreamSource = new MemoryStream(imageData);
            downloadedBitmap.CacheOption = BitmapCacheOption.OnLoad;
            downloadedBitmap.EndInit();
            downloadedBitmap.Freeze();

            // Cache in memory using channel_id
            _imageCache[channelCacheKey] = downloadedBitmap;

            // Save to disk using channel_id
            _ = Task.Run(async () =>
            {
                try
                {
                    await File.WriteAllBytesAsync(channelImageFile, imageData, cancellationToken);
                    _logger.LogInformation("💾 Channel logo cached to disk: Channel {ChannelId}", channelId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cache channel logo to disk: Channel {ChannelId}", channelId);
                }
            });

            _logger.LogInformation("✅ DOWNLOAD SUCCESS: Channel logo downloaded and cached: Channel {ChannelId}", channelId);
            SetCacheStatus("Ready");
            return downloadedBitmap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download and cache channel logo: Channel {ChannelId} from {Url}", channelId, logoUrl);
            SetCacheStatus("Ready");
            return null;
        }
        finally
        {
            _imageSemaphore.Release();
        }
    }

    public async Task<T?> GetDataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        // Check in-memory cache first (fast, non-blocking)
        if (_dataCache.TryGetValue(key, out var entry))
        {
            // For EpgData, skip cache service expiration check and let EpgData.IsStillValid() handle it
            // For VOD data, use the cache service expiration which is properly calculated
            var isEpgData = typeof(T) == typeof(EpgData);

            if (!isEpgData && entry.IsExpired)
            {
                _dataCache.TryRemove(key, out _);
                // Remove file asynchronously in background
                _ = Task.Run(async () => await RemoveDataFileAsync(key));
            }
            else if (entry.Data is T data)
            {
                _logger.LogInformation("📱 CACHE HIT: Data loaded from MEMORY cache: {Key} (no API call needed)", key);
                entry.AccessCount++;
                entry.LastAccessed = DateTime.UtcNow;
                return data;
            }
        }

        // For EpgData and VOD data types, do synchronous disk loading to ensure cache is actually used
        var isEpgDataType = typeof(T) == typeof(EpgData);
        var isVodDataType = typeof(T) == typeof(List<VodCategory>) ||
                           typeof(T) == typeof(List<VodContent>) ||
                           typeof(T) == typeof(List<SeriesCategory>) ||
                           typeof(T) == typeof(List<SeriesContent>) ||
                           typeof(T) == typeof(List<EpisodeContent>);

        if (isEpgDataType || isVodDataType)
        {
            SetCacheStatus($"🔄 Loading {key} from disk...");
            var diskData = await LoadFromDiskCacheAsync<T>(key, cancellationToken);
            SetCacheStatus("Ready");
            if (diskData != null)
            {
                var dataType = isEpgDataType ? "EPG" : "VOD";
                _logger.LogInformation("🎯 {DataType} data loaded from disk to memory cache: {Key}", dataType, key);
                return diskData;
            }
            var missType = isEpgDataType ? "EPG" : "VOD";
            _logger.LogInformation("🔄 CACHE MISS: {MissType} data not found on disk: {Key}", missType, key);
            return null;
        }

        // For non-EPG data, use background disk cache check (non-blocking) for performance
        _ = Task.Run(async () =>
        {
            SetCacheStatus($"🔄 Loading {key} from disk...");
            var diskData = await LoadFromDiskCacheAsync<T>(key, cancellationToken);
            if (diskData != null)
            {
                _logger.LogInformation("🎯 Data loaded from disk to memory cache in background: {Key}", key);
                SetCacheStatus("Ready");
            }
            else
            {
                SetCacheStatus("Ready");
            }
        });

        _logger.LogInformation("🔄 CACHE MISS: Data not found in memory cache, checking disk in background: {Key}", key);
        return null;
    }

    private async Task<T?> LoadFromDiskCacheAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        var dataFile = Path.Combine(_dataDirectory, $"{GetSafeFileName(key)}.json");
        if (File.Exists(dataFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(dataFile, cancellationToken);
                var fileEntry = JsonSerializer.Deserialize<PersistentCacheEntry>(json);

                // For EpgData, skip cache service expiration check and let EpgData.IsStillValid() handle it
                var isEpgData = typeof(T) == typeof(EpgData);
                if (fileEntry != null && (isEpgData || !fileEntry.IsExpired))
                {
                    var deserializedData = JsonSerializer.Deserialize<T>(fileEntry.DataJson);
                    if (deserializedData != null)
                    {
                        // Load back into memory cache so subsequent calls will hit memory
                        var memoryEntry = new CacheEntry
                        {
                            Data = deserializedData,
                            Created = fileEntry.Created,
                            LastAccessed = DateTime.UtcNow,
                            ExpiresAt = fileEntry.ExpiresAt,
                            AccessCount = fileEntry.AccessCount + 1
                        };
                        _dataCache[key] = memoryEntry;

                        _logger.LogInformation("🎯 Data loaded from disk to memory cache: {Key}", key);
                        return deserializedData;
                    }
                }
                else
                {
                    // Expired or invalid - delete file
                    File.Delete(dataFile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached data from disk: {File}", dataFile);
                try { File.Delete(dataFile); } catch { }
            }
        }
        return null;
    }

    public async Task SetDataAsync<T>(string key, T data, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class
    {
        var expirationTime = expiration ?? DefaultDataExpiration;
        var entry = new CacheEntry
        {
            Data = data,
            Created = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(expirationTime),
            AccessCount = 1
        };

        // Apply size limit to memory cache (non-blocking)
        if (_dataCache.Count >= MaxDataCacheSize)
        {
            _ = Task.Run(async () => await CleanupLeastRecentlyUsedAsync());
        }

        // Store in memory immediately (fast, non-blocking)
        _dataCache[key] = entry;

        // Store to disk asynchronously in background (non-blocking)
        _ = Task.Run(async () => await SaveToDiskAsync(key, data, entry, cancellationToken));

        _logger.LogInformation("💾 CACHE STORED: Data cached to memory for key: {Key} (expires: {ExpiresAt}) [Cache size: {MemCount}]",
            key, entry.ExpiresAt, _dataCache.Count);
    }

    private async Task SaveToDiskAsync<T>(string key, T data, CacheEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            // Create a more specific DataType identifier for generic lists
            string dataType = typeof(T).Name;
            if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>))
            {
                var genericArg = typeof(T).GetGenericArguments().FirstOrDefault();
                if (genericArg != null)
                {
                    dataType = $"List<{genericArg.Name}>";
                }
            }

            var persistentEntry = new PersistentCacheEntry
            {
                Key = key,
                DataJson = JsonSerializer.Serialize(data),
                Created = entry.Created,
                ExpiresAt = entry.ExpiresAt,
                AccessCount = entry.AccessCount,
                DataType = dataType
            };

            var json = JsonSerializer.Serialize(persistentEntry, new JsonSerializerOptions { WriteIndented = true });
            var dataFile = Path.Combine(_dataDirectory, $"{GetSafeFileName(key)}.json");
            await File.WriteAllTextAsync(dataFile, json, cancellationToken);

            _logger.LogInformation("💾 Data persisted to disk: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save data cache to disk: {Key}", key);
        }
    }

    public async Task<bool> HasDataAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_dataCache.TryGetValue(key, out var entry) && !entry.IsExpired)
            return true;

        var dataFile = Path.Combine(_dataDirectory, $"{GetSafeFileName(key)}.json");
        if (File.Exists(dataFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(dataFile, cancellationToken);
                var fileEntry = JsonSerializer.Deserialize<PersistentCacheEntry>(json);
                return fileEntry != null && !fileEntry.IsExpired;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public async Task RemoveDataAsync(string key, CancellationToken cancellationToken = default)
    {
        _dataCache.TryRemove(key, out _);
        await RemoveDataFileAsync(key);
        _logger.LogInformation("🗑️ Removed cache entry: {Key}", key);
    }

    public async Task ClearExpiredDataAsync(CancellationToken cancellationToken = default)
    {
        var removedCount = 0;

        // Clean memory cache
        var expiredKeys = _dataCache
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _dataCache.TryRemove(key, out _);
            await RemoveDataFileAsync(key);
            removedCount++;
        }

        // Clean disk cache
        var dataFiles = Directory.GetFiles(_dataDirectory, "*.json");
        foreach (var file in dataFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var entry = JsonSerializer.Deserialize<PersistentCacheEntry>(json);
                if (entry != null && entry.IsExpired)
                {
                    File.Delete(file);
                    removedCount++;
                }
            }
            catch
            {
                // If we can't read it, delete it
                File.Delete(file);
                removedCount++;
            }
        }

        if (removedCount > 0)
        {
            _logger.LogInformation("🧹 Cleaned up {Count} expired cache entries", removedCount);
        }
    }

    public void ClearImageCache()
    {
        _imageCache.Clear();
        try
        {
            foreach (var file in Directory.GetFiles(_imageDirectory))
            {
                File.Delete(file);
            }
            _logger.LogInformation("🧹 Image cache cleared");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear image cache directory");
        }
    }

    public int ImageCacheCount => _imageCache.Count;
    public int DataCacheCount => _dataCache.Count;

    public long EstimatedMemoryUsage
    {
        get
        {
            return (ImageCacheCount * 100 * 1024) + (DataCacheCount * 10 * 1024);
        }
    }

    private async Task LoadCacheFromDiskAsync()
    {
        try
        {
            var dataFiles = Directory.GetFiles(_dataDirectory, "*.json");
            var loadedCount = 0;
            var preloadedCount = 0;

            // Sort by creation time to prioritize recent entries
            var fileInfos = dataFiles.Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .Take(50) // Only preload the most recent 50 entries
                .ToList();

            foreach (var fileInfo in fileInfos)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(fileInfo.FullName);
                    var entry = JsonSerializer.Deserialize<PersistentCacheEntry>(json);

                    if (entry != null && !entry.IsExpired)
                    {
                        loadedCount++;

                        // Pre-load recent, non-expired entries into memory for immediate access
                        try
                        {
                            // Use the stored DataType to determine how to deserialize
                            Type? dataType = entry.DataType switch
                            {
                                "List<Category>" => typeof(List<Category>),
                                "List<Channel>" => typeof(List<Channel>),
                                "List<VodContent>" => typeof(List<VodContent>),
                                "List<VodCategory>" => typeof(List<VodCategory>),
                                "List<SeriesContent>" => typeof(List<SeriesContent>),
                                "List<SeriesCategory>" => typeof(List<SeriesCategory>),
                                "EpgData" => typeof(EpgData),
                                // Legacy support for old format
                                "List`1" when entry.Key.Contains("categories") => typeof(List<Category>),
                                "List`1" when entry.Key.Contains("channels") => typeof(List<Channel>),
                                "List`1" when entry.Key.Contains("vod") => typeof(List<VodContent>),
                                _ => null
                            };

                            if (dataType != null)
                            {
                                var deserializedData = JsonSerializer.Deserialize(entry.DataJson, dataType);
                                if (deserializedData != null)
                                {
                                    var memoryEntry = new CacheEntry
                                    {
                                        Data = deserializedData,
                                        Created = entry.Created,
                                        LastAccessed = DateTime.UtcNow,
                                        ExpiresAt = entry.ExpiresAt,
                                        AccessCount = entry.AccessCount
                                    };
                                    _dataCache[entry.Key] = memoryEntry;
                                    preloadedCount++;
                                }
                            }
                        }
                        catch
                        {
                            // If we can't determine the type, that's OK - it will load on demand
                        }
                    }
                    else
                    {
                        // Delete expired entries
                        File.Delete(fileInfo.FullName);
                    }
                }
                catch
                {
                    // Delete corrupted entries
                    try { File.Delete(fileInfo.FullName); } catch { }
                }
            }

            _logger.LogInformation("📂 Loaded {Count} cache entries from disk ({PreloadedCount} preloaded to memory)",
                loadedCount, preloadedCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cache from disk");
        }
    }

    private async Task CleanupExpiredItemsAsync()
    {
        try
        {
            await ClearExpiredDataAsync();

            // Check cache size and cleanup if needed
            var cacheSize = GetCacheSizeBytes();
            if (cacheSize > MaxCacheSizeBytes)
            {
                _logger.LogInformation("🧹 Cache size ({Size:N0} bytes) exceeds limit, cleaning up...", cacheSize);
                await CleanupOldestItemsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }
    }

    private async Task CleanupLeastRecentlyUsedAsync()
    {
        var itemsToRemove = _dataCache
            .OrderBy(kvp => kvp.Value.LastAccessed)
            .Take(MaxDataCacheSize / 5)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in itemsToRemove)
        {
            _dataCache.TryRemove(key, out _);
        }

        _logger.LogInformation("🧹 Removed {Count} LRU memory cache entries", itemsToRemove.Count);
    }

    private async Task CleanupOldestItemsAsync()
    {
        var dataFiles = Directory.GetFiles(_dataDirectory, "*.json")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.CreationTime)
            .Take(50) // Remove oldest 50 files
            .ToList();

        foreach (var file in dataFiles)
        {
            try
            {
                File.Delete(file.FullName);
            }
            catch { }
        }

        _logger.LogInformation("🧹 Removed {Count} oldest cache files", dataFiles.Count);
    }

    private async Task RemoveDataFileAsync(string key)
    {
        try
        {
            var dataFile = Path.Combine(_dataDirectory, $"{GetSafeFileName(key)}.json");
            if (File.Exists(dataFile))
            {
                File.Delete(dataFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove cache file for key: {Key}", key);
        }
    }

    private long GetCacheSizeBytes()
    {
        try
        {
            var imageSize = Directory.GetFiles(_imageDirectory).Sum(f => new FileInfo(f).Length);
            var dataSize = Directory.GetFiles(_dataDirectory).Sum(f => new FileInfo(f).Length);
            return imageSize + dataSize;
        }
        catch
        {
            return 0;
        }
    }

    private int GetDiskCacheCount()
    {
        try
        {
            return Directory.GetFiles(_dataDirectory, "*.json").Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetSafeFileName(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeFileName = string.Join("_", input.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

        // Limit length and add hash for uniqueness
        if (safeFileName.Length > 100)
        {
            var hash = input.GetHashCode().ToString("X");
            safeFileName = safeFileName.Substring(0, 90) + "_" + hash;
        }

        return safeFileName;
    }

    public async Task<CacheInfo> GetCacheInfoAsync()
    {
        return await Task.Run(() =>
        {
            var imageFiles = Directory.GetFiles(_imageDirectory).Length;
            var dataFiles = Directory.GetFiles(_dataDirectory).Length;
            var totalSize = GetCacheSizeBytes();

            // For performance: Only provide basic stats, detailed entries loaded on demand
            return new CacheInfo
            {
                MemoryImageCount = ImageCacheCount,
                MemoryDataCount = DataCacheCount,
                DiskImageCount = imageFiles,
                DiskDataCount = dataFiles,
                TotalSizeBytes = totalSize,
                CacheDirectory = _cacheDirectory,
                Entries = new List<CacheEntryInfo>() // Empty - loaded on demand
            };
        });
    }

    public async Task<List<CacheEntryInfo>> GetCacheEntriesAsync(int maxEntries = 1000)
    {
        return await Task.Run(() =>
        {
            var cacheEntries = new List<CacheEntryInfo>();
            var files = Directory.GetFiles(_dataDirectory, "*.json");

            // Process files in parallel for better performance
            var results = new ConcurrentBag<CacheEntryInfo>();

            Parallel.ForEach(files.Take(maxEntries), file =>
            {
                try
                {
                    // Fast metadata extraction without full deserialization
                    var fileInfo = new FileInfo(file);
                    var key = Path.GetFileNameWithoutExtension(file);

                    // Read only first part of file to extract metadata quickly
                    var json = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("Key", out var keyProp) &&
                        root.TryGetProperty("DataType", out var typeProp) &&
                        root.TryGetProperty("Created", out var createdProp) &&
                        root.TryGetProperty("ExpiresAt", out var expiresProp) &&
                        root.TryGetProperty("AccessCount", out var accessProp))
                    {
                        var entry = new CacheEntryInfo
                        {
                            Key = keyProp.GetString() ?? key,
                            DataType = typeProp.GetString() ?? "Unknown",
                            Created = createdProp.GetDateTime(),
                            ExpiresAt = expiresProp.GetDateTime(),
                            AccessCount = accessProp.GetInt32(),
                            IsExpired = DateTime.UtcNow > expiresProp.GetDateTime(),
                            SizeBytes = fileInfo.Length
                        };
                        results.Add(entry);
                    }
                }
                catch
                {
                    // Skip corrupted files
                }
            });

            return results.OrderByDescending(e => e.Created).ToList();
        });
    }


    private void SetCacheStatus(string status)
    {
        CurrentCacheStatus = status;
        CacheOperationStatusChanged?.Invoke(status);
    }

    // Cache models
    private class CacheEntry
    {
        public object Data { get; set; } = null!;
        public DateTime Created { get; set; }
        public DateTime LastAccessed { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int AccessCount { get; set; }
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }

    private class PersistentCacheEntry
    {
        public string Key { get; set; } = string.Empty;
        public string DataJson { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public DateTime Created { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int AccessCount { get; set; }
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }
}

public class CacheInfo
{
    public int MemoryImageCount { get; set; }
    public int MemoryDataCount { get; set; }
    public int DiskImageCount { get; set; }
    public int DiskDataCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public string CacheDirectory { get; set; } = string.Empty;
    public List<CacheEntryInfo> Entries { get; set; } = new();
}

public class CacheEntryInfo
{
    public string Key { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int AccessCount { get; set; }
    public bool IsExpired { get; set; }
    public long SizeBytes { get; set; }
}