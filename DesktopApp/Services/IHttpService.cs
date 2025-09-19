namespace DesktopApp.Services;

public interface IHttpService
{
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default);
    Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default);
    Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken = default);
}