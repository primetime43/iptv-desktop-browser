using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public class HttpService : IHttpService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public HttpService(HttpClient httpClient, ILogger<HttpService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Making HTTP GET request to: {Url}", url);
            var response = await _httpClient.GetStringAsync(url, cancellationToken);
            _logger.LogDebug("Successfully received response from: {Url}", url);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("HTTP request to {Url} was cancelled", url);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error making HTTP GET request to: {Url}", url);
            throw;
        }
    }

    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Making HTTP GET JSON request to: {Url}", url);
            var jsonString = await GetStringAsync(url, cancellationToken);
            var result = JsonSerializer.Deserialize<T>(jsonString, _jsonOptions);
            _logger.LogDebug("Successfully deserialized JSON response from: {Url}", url);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("HTTP JSON request to {Url} was cancelled", url);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error making HTTP GET JSON request to: {Url}", url);
            throw;
        }
    }

    public async Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Making HTTP GET byte array request to: {Url}", url);
            var response = await _httpClient.GetByteArrayAsync(url, cancellationToken);
            _logger.LogDebug("Successfully received byte array response from: {Url}", url);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("HTTP byte array request to {Url} was cancelled", url);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error making HTTP GET byte array request to: {Url}", url);
            throw;
        }
    }
}