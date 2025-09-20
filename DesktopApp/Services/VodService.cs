using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using DesktopApp.Models;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public class VodService : IVodService
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

    public async Task<List<VodCategory>> LoadVodCategoriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading VOD categories");

            if (_sessionService.Mode == SessionMode.M3u)
            {
                return _sessionService.VodCategories.ToList();
            }

            // Check cache first
            var cacheKey = $"vod_categories_{_sessionService.Host}_{_sessionService.Username}";
            var cachedCategories = await _cacheService.GetDataAsync<List<VodCategory>>(cacheKey, cancellationToken);
            if (cachedCategories != null)
            {
                _logger.LogInformation("Loaded {Count} VOD categories from cache", cachedCategories.Count);
                return cachedCategories;
            }

            var url = _sessionService.BuildApi("get_vod_categories");
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            var categories = new List<VodCategory>();

            if (IsBase64String(response))
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(response));
                response = decoded;
            }

            var jsonCategories = JsonSerializer.Deserialize<List<JsonElement>>(response);
            if (jsonCategories != null)
            {
                foreach (var item in jsonCategories)
                {
                    var category = new VodCategory
                    {
                        CategoryId = item.GetProperty("category_id").GetString() ?? "",
                        CategoryName = item.GetProperty("category_name").GetString() ?? "",
                        ParentId = item.TryGetProperty("parent_id", out var parentId) ? parentId.GetString() : null
                    };
                    categories.Add(category);
                }
            }

            // Cache the results
            await _cacheService.SetDataAsync(cacheKey, categories, TimeSpan.FromHours(1), cancellationToken);

            _logger.LogInformation("Loaded {Count} VOD categories", categories.Count);
            return categories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading VOD categories");
            throw;
        }
    }

    public async Task<List<VodContent>> LoadVodContentAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading VOD content for category: {CategoryId}", categoryId);

            if (_sessionService.Mode == SessionMode.M3u)
            {
                return _sessionService.VodContent
                    .Where(v => v.CategoryId == categoryId)
                    .ToList();
            }

            // Check cache first
            var cacheKey = $"vod_content_{_sessionService.Host}_{_sessionService.Username}_{categoryId}";
            var cachedContent = await _cacheService.GetDataAsync<List<VodContent>>(cacheKey, cancellationToken);
            if (cachedContent != null)
            {
                _logger.LogInformation("Loaded {Count} VOD items for category {CategoryId} from cache", cachedContent.Count, categoryId);
                return cachedContent;
            }

            var url = _sessionService.BuildApi("get_vod_streams", ("category_id", categoryId));
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            var content = new List<VodContent>();

            if (IsBase64String(response))
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(response));
                response = decoded;
            }

            var jsonContent = JsonSerializer.Deserialize<List<JsonElement>>(response);
            if (jsonContent != null)
            {
                foreach (var item in jsonContent)
                {
                    var vodContent = new VodContent
                    {
                        Id = int.TryParse(item.GetProperty("stream_id").GetString(), out var streamId) ? streamId : 0,
                        Name = item.GetProperty("name").GetString() ?? "",
                        StreamIcon = item.TryGetProperty("stream_icon", out var icon) ? icon.GetString() : null,
                        CategoryId = categoryId,
                        ContainerExtension = item.TryGetProperty("container_extension", out var ext) ? ext.GetString() ?? "mp4" : "mp4"
                    };

                    // Try to get additional metadata
                    if (item.TryGetProperty("rating", out var rating))
                        vodContent.Rating = rating.GetString();
                    if (item.TryGetProperty("plot", out var plot))
                        vodContent.Plot = plot.GetString();
                    if (item.TryGetProperty("cast", out var cast))
                        vodContent.Cast = cast.GetString();
                    if (item.TryGetProperty("director", out var director))
                        vodContent.Director = director.GetString();
                    if (item.TryGetProperty("genre", out var genre))
                        vodContent.Genre = genre.GetString();
                    if (item.TryGetProperty("releasedate", out var releaseDate))
                        vodContent.ReleaseDate = releaseDate.GetString();
                    if (item.TryGetProperty("duration", out var duration))
                        vodContent.Duration = duration.GetString();

                    content.Add(vodContent);
                }
            }

            // Cache the results
            await _cacheService.SetDataAsync(cacheKey, content, TimeSpan.FromMinutes(30), cancellationToken);

            _logger.LogInformation("Loaded {Count} VOD items for category {CategoryId}", content.Count, categoryId);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading VOD content for category: {CategoryId}", categoryId);
            throw;
        }
    }

    public async Task<List<SeriesCategory>> LoadSeriesCategoriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading series categories");

            if (_sessionService.Mode == SessionMode.M3u)
            {
                return _sessionService.SeriesCategories.ToList();
            }

            var url = _sessionService.BuildApi("get_series_categories");
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            var categories = new List<SeriesCategory>();

            if (IsBase64String(response))
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(response));
                response = decoded;
            }

            var jsonCategories = JsonSerializer.Deserialize<List<JsonElement>>(response);
            if (jsonCategories != null)
            {
                foreach (var item in jsonCategories)
                {
                    var category = new SeriesCategory
                    {
                        CategoryId = item.GetProperty("category_id").GetString() ?? "",
                        CategoryName = item.GetProperty("category_name").GetString() ?? "",
                        ParentId = item.TryGetProperty("parent_id", out var parentId) ? parentId.GetString() : null
                    };
                    categories.Add(category);
                }
            }

            _logger.LogInformation("Loaded {Count} series categories", categories.Count);
            return categories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading series categories");
            throw;
        }
    }

    public async Task<List<SeriesContent>> LoadSeriesContentAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading series content for category: {CategoryId}", categoryId);

            if (_sessionService.Mode == SessionMode.M3u)
            {
                return _sessionService.SeriesContent
                    .Where(s => s.CategoryId == categoryId)
                    .ToList();
            }

            var url = _sessionService.BuildApi("get_series", ("category_id", categoryId));
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            var content = new List<SeriesContent>();

            if (IsBase64String(response))
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(response));
                response = decoded;
            }

            var jsonContent = JsonSerializer.Deserialize<List<JsonElement>>(response);
            if (jsonContent != null)
            {
                foreach (var item in jsonContent)
                {
                    var seriesContent = new SeriesContent
                    {
                        Id = int.TryParse(item.GetProperty("series_id").GetString(), out var seriesId) ? seriesId : 0,
                        Name = item.GetProperty("name").GetString() ?? "",
                        StreamIcon = item.TryGetProperty("cover", out var cover) ? cover.GetString() : null,
                        CategoryId = categoryId
                    };

                    // Try to get additional metadata
                    if (item.TryGetProperty("plot", out var plot))
                        seriesContent.Plot = plot.GetString();
                    if (item.TryGetProperty("cast", out var cast))
                        seriesContent.Cast = cast.GetString();
                    if (item.TryGetProperty("director", out var director))
                        seriesContent.Director = director.GetString();
                    if (item.TryGetProperty("genre", out var genre))
                        seriesContent.Genre = genre.GetString();
                    if (item.TryGetProperty("releasedate", out var releaseDate))
                        seriesContent.ReleaseDate = releaseDate.GetString();
                    if (item.TryGetProperty("rating", out var rating))
                        seriesContent.Rating = rating.GetString();

                    content.Add(seriesContent);
                }
            }

            _logger.LogInformation("Loaded {Count} series for category {CategoryId}", content.Count, categoryId);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading series content for category: {CategoryId}", categoryId);
            throw;
        }
    }

    public async Task<List<EpisodeContent>> LoadEpisodesAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading episodes for series: {SeriesId}", seriesId);

            var url = _sessionService.BuildApi("get_series_info", ("series_id", seriesId));
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            var episodes = new List<EpisodeContent>();

            if (IsBase64String(response))
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(response));
                response = decoded;
            }

            var jsonData = JsonSerializer.Deserialize<JsonElement>(response);
            if (jsonData.TryGetProperty("episodes", out var episodesElement))
            {
                foreach (var seasonProperty in episodesElement.EnumerateObject())
                {
                    var seasonNumber = int.TryParse(seasonProperty.Name, out var season) ? season : 1;

                    foreach (var episode in seasonProperty.Value.EnumerateArray())
                    {
                        var episodeContent = new EpisodeContent
                        {
                            Id = int.TryParse(episode.GetProperty("id").GetString(), out var episodeId) ? episodeId : 0,
                            Name = episode.GetProperty("title").GetString() ?? "",
                            SeasonNumber = seasonNumber,
                            SeriesId = int.TryParse(seriesId, out var parsedSeriesId) ? parsedSeriesId : 0,
                            ContainerExtension = episode.TryGetProperty("container_extension", out var ext) ? ext.GetString() ?? "mp4" : "mp4"
                        };

                        if (episode.TryGetProperty("episode_num", out var episodeNum))
                            episodeContent.EpisodeNumber = episodeNum.GetInt32();
                        if (episode.TryGetProperty("info", out var info) && info.TryGetProperty("plot", out var plot))
                            episodeContent.Plot = plot.GetString();
                        if (episode.TryGetProperty("info", out var info2) && info2.TryGetProperty("duration", out var duration))
                            episodeContent.Duration = duration.GetString();

                        episodes.Add(episodeContent);
                    }
                }
            }

            _logger.LogInformation("Loaded {Count} episodes for series {SeriesId}", episodes.Count, seriesId);
            return episodes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading episodes for series: {SeriesId}", seriesId);
            throw;
        }
    }

    public async Task LoadVodPosterAsync(VodContent content, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(content.StreamIcon)) return;

            var bitmap = await _cacheService.GetImageAsync(content.StreamIcon, cancellationToken);
            if (bitmap != null)
            {
                content.PosterImage = bitmap;
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

            var bitmap = await _cacheService.GetImageAsync(content.StreamIcon, cancellationToken);
            if (bitmap != null)
            {
                content.PosterImage = bitmap;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load poster for series: {Name}", content.Name);
            // Don't throw - poster loading failures shouldn't break the application
        }
    }

    private static bool IsBase64String(string base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length % 4 != 0)
            return false;

        try
        {
            Convert.FromBase64String(base64);
            return true;
        }
        catch
        {
            return false;
        }
    }
}