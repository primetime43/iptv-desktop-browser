using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopApp.Models;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public partial class VodService : IVodService
{
    private readonly ISessionService _sessionService;
    private readonly IHttpService _httpService;
    private readonly ICacheService _cacheService;
    private readonly ILogger<VodService> _logger;

    public VodService(
        ISessionService sessionService,
        IHttpService httpService,
        ICacheService cacheService,
        ILogger<VodService> logger)
    {
        _sessionService = sessionService;
        _httpService = httpService;
        _cacheService = cacheService;
        _logger = logger;
    }

    public Task<List<VodCategory>> LoadVodCategoriesAsync(CancellationToken cancellationToken = default) =>
        _sessionService.Mode == SessionMode.M3u
            ? Task.FromResult(_sessionService.VodCategories.ToList())
            : LoadCachedAsync("get_vod_categories", [], TimeSpan.FromHours(1), ParseVodCategories, cancellationToken);

    public Task<List<SeriesCategory>> LoadSeriesCategoriesAsync(CancellationToken cancellationToken = default) =>
        _sessionService.Mode == SessionMode.M3u
            ? Task.FromResult(_sessionService.SeriesCategories.ToList())
            : LoadCachedAsync("get_series_categories", [], TimeSpan.FromHours(1), ParseSeriesCategories, cancellationToken);

    public Task<List<VodContent>> LoadVodContentAsync(string categoryId, CancellationToken cancellationToken = default) =>
        _sessionService.Mode == SessionMode.M3u
            ? Task.FromResult(_sessionService.VodContent.Where(v => v.CategoryId == categoryId).ToList())
            : LoadCachedAsync("get_vod_streams", [("category_id", categoryId)], TimeSpan.FromMinutes(30),
                root => ParseVodContent(root, categoryId), cancellationToken);

    public Task<List<SeriesContent>> LoadSeriesContentAsync(string categoryId, CancellationToken cancellationToken = default) =>
        _sessionService.Mode == SessionMode.M3u
            ? Task.FromResult(_sessionService.SeriesContent.Where(s => s.CategoryId == categoryId).ToList())
            : LoadCachedAsync("get_series", [("category_id", categoryId)], TimeSpan.FromMinutes(30),
                root => ParseSeriesContent(root, categoryId), cancellationToken);

    public async Task LoadVodDetailsAsync(VodContent content, CancellationToken cancellationToken = default)
    {
        var details = await LoadCachedAsync("get_vod_info", [("vod_id", content.Id.ToString())], TimeSpan.FromHours(2),
            root => ParseVodDetails(root, content.Id), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        content.ApplyDetails(details);
    }

    public async Task LoadSeriesDetailsAsync(SeriesContent content, CancellationToken cancellationToken = default)
    {
        var details = await LoadSeriesInfoAsync(content.Id.ToString(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        content.ApplyDetails(details);
    }

    public async Task<List<EpisodeContent>> LoadEpisodesAsync(string seriesId, CancellationToken cancellationToken = default) =>
        (await LoadSeriesInfoAsync(seriesId, cancellationToken)).Seasons.SelectMany(s => s.Episodes).ToList();

    private Task<SeriesContent> LoadSeriesInfoAsync(string seriesId, CancellationToken cancellationToken) =>
        LoadCachedAsync("get_series_info", [("series_id", seriesId)], TimeSpan.FromHours(2),
            root => ParseSeriesDetails(root, int.TryParse(seriesId, out var id) ? id : 0), cancellationToken);

    private async Task<T> LoadCachedAsync<T>(string action, (string key, string value)[] parameters,
        TimeSpan lifetime, Func<JsonElement, T> parse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture the complete request before awaiting: servers, ports, accounts and categories
        // must never share cached data. Hashing also keeps credentials out of cache keys/logs.
        var url = _sessionService.BuildApi(action, parameters);
        var cacheKey = $"vod_v2_{action}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))}";
        var useCache = _sessionService.CachingEnabled;
        try
        {
            var response = useCache ? await _cacheService.GetDataAsync<string>(cacheKey, cancellationToken).ConfigureAwait(false) : null;
            cancellationToken.ThrowIfCancellationRequested();
            var fromCache = response != null;
            response ??= await _httpService.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Cache immutable provider data, not live WPF models with posters, selection and
            // loading flags. Recreate models off the UI thread even on a memory-cache hit.
            var parsed = await Task.Run(() =>
            {
                var json = response.Trim();
                if (!json.StartsWith('[') && !json.StartsWith('{') && !json.StartsWith("null", StringComparison.Ordinal))
                {
                    try { json = Encoding.UTF8.GetString(Convert.FromBase64String(json)); }
                    catch (FormatException) { /* Let the JSON parser report the invalid response. */ }
                }
                using var doc = JsonDocument.Parse(json);
                return parse(doc.RootElement);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Invalid/error responses are rejected by the parser and never cached as empty catalogs.
            if (useCache && !fromCache)
                await _cacheService.SetDataAsync(cacheKey, response, lifetime, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Loaded {Action} from {Source}", action, fromCache ? "cache" : "provider");
            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading {Action}", action);
            throw;
        }
    }

    public async Task LoadVodPosterAsync(VodContent content, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(content.StreamIcon)) return;

            if (_sessionService.CachingEnabled)
            {
                var bitmap = await _cacheService.GetImageAsync(content.StreamIcon, cancellationToken);
                if (bitmap != null)
                {
                    content.PosterImage = bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load poster for VOD content: {Name}", content.Name);
            // Don't throw - poster loading failures shouldn't break the application
        }
    }

    public async Task LoadSeriesPosterAsync(SeriesContent content, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(content.StreamIcon)) return;

            if (_sessionService.CachingEnabled)
            {
                var bitmap = await _cacheService.GetImageAsync(content.StreamIcon, cancellationToken);
                if (bitmap != null)
                {
                    content.PosterImage = bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load poster for series: {Name}", content.Name);
            // Don't throw - poster loading failures shouldn't break the application
        }
    }

}
