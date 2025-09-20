using System.Windows.Media.Imaging;

namespace DesktopApp.Services;

public interface ICacheService
{
    // Image caching
    Task<BitmapImage?> GetImageAsync(string url, CancellationToken cancellationToken = default);
    Task<BitmapImage?> GetChannelLogoAsync(int channelId, string logoUrl, CancellationToken cancellationToken = default);
    void ClearImageCache();

    // Data caching with expiration
    Task<T?> GetDataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;
    Task SetDataAsync<T>(string key, T data, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class;
    Task<bool> HasDataAsync(string key, CancellationToken cancellationToken = default);
    Task RemoveDataAsync(string key, CancellationToken cancellationToken = default);
    Task ClearExpiredDataAsync(CancellationToken cancellationToken = default);

    // Cache statistics
    int ImageCacheCount { get; }
    int DataCacheCount { get; }
    long EstimatedMemoryUsage { get; }

    // Cache status
    event Action<string>? CacheOperationStatusChanged;
    string CurrentCacheStatus { get; }
}