using System.Text;
using System.Text.Json;
using DesktopApp.Configuration;
using DesktopApp.Models;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public partial class ChannelService : IChannelService
{
    private readonly ISessionService _sessionService;
    private readonly IHttpService _httpService;
    private readonly ICacheService _cacheService;
    private readonly ILogger<ChannelService> _logger;
    private readonly ApiSettings _apiSettings;
    private readonly CacheSettings _cacheSettings;
    private readonly BatchProcessingSettings _batchSettings;
    private readonly M3uSettings _m3uSettings;
    private readonly EpgSettings _epgSettings;
    private readonly TimeProvider _clock;
    private Action<string>? _rawOutputLogger;

    public ChannelService(
        ISessionService sessionService,
        IHttpService httpService,
        ICacheService cacheService,
        ILogger<ChannelService> logger,
        ApiSettings apiSettings,
        CacheSettings cacheSettings,
        BatchProcessingSettings batchSettings,
        M3uSettings m3uSettings,
        EpgSettings epgSettings,
        TimeProvider? timeProvider = null)
    {
        _sessionService = sessionService;
        _httpService = httpService;
        _cacheService = cacheService;
        _logger = logger;
        _apiSettings = apiSettings;
        _cacheSettings = cacheSettings;
        _batchSettings = batchSettings;
        _m3uSettings = m3uSettings;
        _epgSettings = epgSettings;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public void SetRawOutputLogger(Action<string>? logger)
    {
        _rawOutputLogger = logger;
    }

    public Task<List<Category>> LoadCategoriesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCategoriesCoreAsync(cancellationToken), cancellationToken);

    public Task<List<Channel>> LoadChannelsForCategoryAsync(Category category, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadChannelsForCategoryCoreAsync(category, cancellationToken), cancellationToken);

    private async Task<List<Category>> LoadCategoriesCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Loading categories");

            if (_sessionService.Mode == SessionMode.M3u)
            {
                return LoadM3uCategories();
            }

            // Check cache first (only if caching is enabled)
            if (_sessionService.CachingEnabled)
            {
                var cacheKey = $"{_cacheSettings.KeyPrefixes.LiveCategories}_{_sessionService.Host}_{_sessionService.Username}";
                _logger.LogInformation("🔍 Checking cache with key: {CacheKey}", cacheKey);
                var cachedCategories = await _cacheService.GetDataAsync<List<Category>>(cacheKey, cancellationToken);
                if (cachedCategories != null)
                {
                    var cacheHitMsg = $"📱 CACHE HIT: Loaded {cachedCategories.Count} categories from CACHE (no API call needed)";
                    _logger.LogInformation(cacheHitMsg);
                    _rawOutputLogger?.Invoke(cacheHitMsg + "\n");
                    return cachedCategories;
                }
                var cacheMissMsg = "🌐 API CALL: Cache MISS - Loading categories from API server";
                _logger.LogInformation(cacheMissMsg);
                _rawOutputLogger?.Invoke(cacheMissMsg + "\n");
            }

            var url = _sessionService.BuildApi(_apiSettings.Actions.GetLiveCategories);
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            var categories = new List<Category>();

            // Handle potential base64 encoded response
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
                    cancellationToken.ThrowIfCancellationRequested();
                    var category = new Category
                    {
                        Id = item.GetProperty("category_id").GetString() ?? "",
                        Name = item.GetProperty("category_name").GetString() ?? "",
                        ParentId = item.TryGetProperty("parent_id", out var parentId) ? parentId.GetInt32() : 0
                    };
                    categories.Add(category);
                }
            }

            // Cache the results (only if caching is enabled)
            if (_sessionService.CachingEnabled)
            {
                var cacheKey = $"{_cacheSettings.KeyPrefixes.LiveCategories}_{_sessionService.Host}_{_sessionService.Username}";
                await _cacheService.SetDataAsync(cacheKey, categories, TimeSpan.FromHours(_cacheSettings.Durations.CategoriesHours), cancellationToken);
            }

            _logger.LogInformation("✅ API SUCCESS: Loaded {Count} categories from API and cached for future use", categories.Count);
            return categories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading categories");
            throw;
        }
    }

    private async Task<List<Channel>> LoadChannelsForCategoryCoreAsync(Category category, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Loading channels for category: {CategoryName}", category.Name);

            if (_sessionService.Mode == SessionMode.M3u)
            {
                return LoadM3uChannelsForCategory(category);
            }

            // Check cache first (only if caching is enabled)
            if (_sessionService.CachingEnabled)
            {
                var cacheKey = $"{_cacheSettings.KeyPrefixes.Channels}_{_sessionService.Host}_{_sessionService.Username}_{category.Id}";
                _logger.LogInformation("🔍 Checking channels cache with key: {CacheKey}", cacheKey);
                var cachedChannels = await _cacheService.GetDataAsync<List<Channel>>(cacheKey, cancellationToken);
                if (cachedChannels != null)
                {
                    // Cache entries may still be bound to another view. Background preparation
                    // must not change their properties or carry an old now-playing snapshot.
                    cachedChannels = cachedChannels.Select(channel => new Channel
                    {
                        Id = channel.Id, Name = channel.Name, Logo = channel.Logo,
                        EpgChannelId = channel.EpgChannelId
                    }).ToList();
                    cancellationToken.ThrowIfCancellationRequested();
                    var cacheHitMsg = $"📱 CACHE HIT: Loaded {cachedChannels.Count} channels from CACHE for category: {category.Name} (no API call needed)";
                    _logger.LogInformation(cacheHitMsg);
                    _rawOutputLogger?.Invoke(cacheHitMsg + "\n");
                    return cachedChannels;
                }
                var cacheMissMsg = $"🌐 API CALL: Channels cache MISS - Loading from API server for category: {category.Name}";
                _logger.LogInformation(cacheMissMsg);
                _rawOutputLogger?.Invoke(cacheMissMsg + "\n");
            }

            var url = _sessionService.BuildApi(_apiSettings.Actions.GetLiveStreams, ("category_id", category.Id));
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            var channels = new List<Channel>();

            // Handle potential base64 encoded response
            if (IsBase64String(response))
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(response));
                response = decoded;
            }

            var jsonChannels = JsonSerializer.Deserialize<List<JsonElement>>(response);
            if (jsonChannels != null)
            {
                foreach (var item in jsonChannels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var channel = new Channel
                    {
                        Id = item.GetProperty("stream_id").GetInt32(),
                        Name = item.GetProperty("name").GetString() ?? "",
                        Logo = item.TryGetProperty("stream_icon", out var logo) ? logo.GetString() : null,
                        EpgChannelId = item.TryGetProperty("epg_channel_id", out var epgId) ? epgId.GetString() : null
                    };
                    channels.Add(channel);
                }
            }

            // Cache the results (only if caching is enabled)
            if (_sessionService.CachingEnabled)
            {
                var cacheKey = $"{_cacheSettings.KeyPrefixes.Channels}_{_sessionService.Host}_{_sessionService.Username}_{category.Id}";
                await _cacheService.SetDataAsync(cacheKey, channels, TimeSpan.FromMinutes(_cacheSettings.Durations.ChannelsMinutes), cancellationToken);
            }

            _logger.LogInformation("✅ API SUCCESS: Loaded {Count} channels from API for category {CategoryName} and cached for future use", channels.Count, category.Name);
            return channels;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading channels for category: {CategoryName}", category.Name);
            throw;
        }
    }

    public async Task LoadEpgForChannelsAsync(IEnumerable<Channel> channels, CancellationToken cancellationToken = default)
    {
        var channelList = channels.ToList();
        _logger.LogInformation("Loading EPG for {Count} channels", channelList.Count);

        // Process in batches to avoid overwhelming the API
        var batches = channelList.Chunk(_batchSettings.EpgBatchSize);

        foreach (var batch in batches)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var tasks = batch.Select(channel => LoadEpgForChannelAsync(channel, cancellationToken));
            await Task.WhenAll(tasks);

            // Small delay between batches
            await Task.Delay(_batchSettings.DelayBetweenBatchesMilliseconds, cancellationToken);
        }
    }

    private List<Category> LoadM3uCategories()
    {
        // Extract categories from M3U playlist
        var categories = new List<Category>();
        var categoryNames = _sessionService.PlaylistChannels
            .Select(c => c.Category)
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct()
            .OrderBy(g => g);

        foreach (var name in categoryNames)
        {
            categories.Add(new Category
            {
                Id = name!,
                Name = name!
            });
        }

        if (!categories.Any())
        {
            categories.Add(new Category { Id = _m3uSettings.DefaultCategories.AllId, Name = _m3uSettings.DefaultCategories.AllName });
        }

        return categories;
    }

    private List<Channel> LoadM3uChannelsForCategory(Category category)
    {
        var channels = new List<Channel>();
        var playlistChannels = category.Id == _m3uSettings.DefaultCategories.AllId
            ? _sessionService.PlaylistChannels
            : _sessionService.PlaylistChannels.Where(c => c.Category == category.Id);

        int id = 1;
        foreach (var playlistChannel in playlistChannels)
        {
            var channel = new Channel
            {
                Id = id++,
                Name = playlistChannel.Name,
                Logo = playlistChannel.Logo,
                EpgChannelId = playlistChannel.TvgId
            };
            channels.Add(channel);
        }

        return channels;
    }

    private void LoadM3uEpgForChannel(Channel channel)
    {
        if (string.IsNullOrEmpty(channel.EpgChannelId)) return;

        if (_sessionService.M3uEpgByChannel.TryGetValue(channel.EpgChannelId, out var epgEntries))
        {
            var now = DateTime.UtcNow;
            var currentProgram = epgEntries.FirstOrDefault(e => e.IsNow(now));
            var nextProgram = epgEntries.FirstOrDefault(e => e.StartUtc > now);

            if (currentProgram != null)
            {
                channel.NowTitle = currentProgram.Title;
                channel.NowDescription = currentProgram.Description;
                channel.NowTimeRange = currentProgram.TimeRangeLocal;
            }

            channel.EpgLoaded = true;
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

    // Helper methods for EPG parsing
    private static DateTime GetUnixTimestamp(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var tsEl))
            return DateTime.MinValue;

        long unix;
        var parsed = tsEl.ValueKind == JsonValueKind.Number
            ? tsEl.TryGetInt64(out unix)
            : long.TryParse(tsEl.ValueKind == JsonValueKind.String ? tsEl.GetString() : null, out unix);
        if (!parsed || unix <= 0)
            return DateTime.MinValue;

        try { return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    private static string TryGetString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p))
            {
                if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? string.Empty;
                if (p.ValueKind == JsonValueKind.Number) return p.ToString();
            }
        }
        return string.Empty;
    }

    private static string DecodeMaybeBase64(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        // Check if string looks like Base64 (proper length and characters)
        if (raw.Length % 4 != 0 || !raw.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '='))
            return raw;

        try
        {
            var bytes = Convert.FromBase64String(raw);
            var txt = System.Text.Encoding.UTF8.GetString(bytes);

            // If decoded text contains unexpected control characters, return original
            if (txt.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                return raw;

            return txt;
        }
        catch
        {
            return raw;
        }
    }
}
