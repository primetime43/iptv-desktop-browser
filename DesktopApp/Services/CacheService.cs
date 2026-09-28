using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public class CacheService : ICacheService
{
    private readonly IHttpService _httpService;
    private readonly ILogger<CacheService> _logger;
    private readonly ISessionService _sessionService;

    // Image cache
    private readonly ThumbnailMemoryCache _imageCache = new();
    private readonly SemaphoreSlim _imageSemaphore = new(10); // Allow up to 10 concurrent image loads

    // Data cache with expiration
    private readonly ConcurrentDictionary<string, CacheEntry> _dataCache = new();
    private readonly Timer _cleanupTimer;

    // Cache limits
    private const int MaxDataCacheSize = 1000;
    private static readonly TimeSpan DefaultDataExpiration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    public CacheService(IHttpService httpService, ILogger<CacheService> logger, ISessionService? sessionService = null)
    {
        _httpService = httpService;
        _logger = logger;
        _sessionService = sessionService ?? new SessionService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionService>.Instance);

        _logger.LogInformation("🏗️ CacheService instance created");

        // Start cleanup timer
        _cleanupTimer = new Timer(async _ => await CleanupExpiredItemsAsync(), null, CleanupInterval, CleanupInterval);
    }

    public Task<BitmapImage?> GetImageAsync(string url, CancellationToken cancellationToken = default) =>
        GetImageAsync(url, 512, 512, cancellationToken);

    public async Task<BitmapImage?> GetImageAsync(string url, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var size = ThumbnailSize.Create(pixelWidth, pixelHeight);
        var key = $"{ThumbnailImage.SourceKey(_sessionService, url)}_{size.Width}x{size.Height}";
        // Check cache first
        if (_imageCache.TryGetValue(key, out var cachedImage))
        {
            _logger.LogInformation("🎯 Image cache HIT for: {Url}", url);
            return cachedImage;
        }

        // Download and cache image
        await _imageSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-check cache after acquiring semaphore
            if (_imageCache.TryGetValue(key, out cachedImage))
                return cachedImage;

            _logger.LogInformation("🔄 Image cache MISS - Loading from: {Url}", url);
            var imageBytes = await _httpService.GetByteArrayAsync(url, cancellationToken);

            if (imageBytes.Length == 0)
                return null;

            var bitmap = await Task.Run(() => ThumbnailImage.Decode(imageBytes, size), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _imageCache.Store(key, bitmap);
            _logger.LogDebug("Cached image for: {Url}", url);
            return bitmap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load image: {Url}", url);
            return null;
        }
        finally
        {
            _imageSemaphore.Release();
        }
    }

    public void ClearImageCache()
    {
        _imageCache.Clear();
        _logger.LogInformation("Image cache cleared");
    }

    public async Task<BitmapImage?> GetChannelLogoAsync(int channelId, string logoUrl, CancellationToken cancellationToken = default)
    {
        // For the in-memory cache service, fall back to URL-based caching
        return await GetImageAsync(logoUrl, 256, 256, cancellationToken);
    }

    public async Task<T?> GetDataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        if (_dataCache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _dataCache.TryRemove(key, out _);
                _logger.LogDebug("Data cache entry expired: {Key}", key);
                return null;
            }

            if (entry.Data is T data)
            {
                _logger.LogInformation("🎯 Data cache HIT for: {Key}", key);
                entry.AccessCount++;
                entry.LastAccessed = DateTime.UtcNow;
                return data;
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

        // Apply size limit
        if (_dataCache.Count >= MaxDataCacheSize)
        {
            await CleanupLeastRecentlyUsedAsync();
        }

        _dataCache[key] = entry;
        _logger.LogInformation("💾 Cached data for key: {Key} (expires: {ExpiresAt}) [Cache size: {CacheSize}]", key, entry.ExpiresAt, _dataCache.Count);
    }

    public async Task<bool> HasDataAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_dataCache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _dataCache.TryRemove(key, out _);
                return false;
            }
            return true;
        }
        return false;
    }

    public async Task RemoveDataAsync(string key, CancellationToken cancellationToken = default)
    {
        _dataCache.TryRemove(key, out _);
        _logger.LogDebug("Removed data cache entry: {Key}", key);
    }

    public async Task ClearExpiredDataAsync(CancellationToken cancellationToken = default)
    {
        var expiredKeys = _dataCache
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _dataCache.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            _logger.LogDebug("Removed {Count} expired cache entries", expiredKeys.Count);
        }
    }

    public async Task ClearAllDataAsync(CancellationToken cancellationToken = default)
    {
        var allKeys = _dataCache.Keys.ToList();

        foreach (var key in allKeys)
        {
            _dataCache.TryRemove(key, out _);
        }

        _logger.LogDebug("Cleared all {Count} cache entries", allKeys.Count);
    }

    public int ImageCacheCount => _imageCache.Count;
    public int DataCacheCount => _dataCache.Count;

    public long EstimatedMemoryUsage
    {
        get
        {
            // Decoded image pixels plus a rough estimate for data entries.
            return _imageCache.Bytes + (DataCacheCount * 10 * 1024);
        }
    }

    // Cache status (not implemented for basic cache service)
    public event Action<string>? CacheOperationStatusChanged;
    public string CurrentCacheStatus => "Ready";

    private async Task CleanupExpiredItemsAsync()
    {
        try
        {
            await ClearExpiredDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }
    }

    private async Task CleanupLeastRecentlyUsedAsync()
    {
        // Remove 20% of least recently used items
        var itemsToRemove = _dataCache
            .OrderBy(kvp => kvp.Value.LastAccessed)
            .Take(MaxDataCacheSize / 5)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in itemsToRemove)
        {
            _dataCache.TryRemove(key, out _);
        }

        _logger.LogDebug("Removed {Count} LRU cache entries", itemsToRemove.Count);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _imageSemaphore?.Dispose();
    }

    private class CacheEntry
    {
        public object Data { get; set; } = null!;
        public DateTime Created { get; set; }
        public DateTime LastAccessed { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int AccessCount { get; set; }

        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }
}
