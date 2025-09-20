using System.Text;
using System.Text.Json;
using DesktopApp.Models;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public class ChannelService : IChannelService
{
    private readonly ISessionService _sessionService;
    private readonly IHttpService _httpService;
    private readonly ICacheService _cacheService;
    private readonly ILogger<ChannelService> _logger;
    private Action<string>? _rawOutputLogger;

    public ChannelService(
        ISessionService sessionService,
        IHttpService httpService,
        ICacheService cacheService,
        ILogger<ChannelService> logger)
    {
        _sessionService = sessionService;
        _httpService = httpService;
        _cacheService = cacheService;
        _logger = logger;
    }

    public void SetRawOutputLogger(Action<string>? logger)
    {
        _rawOutputLogger = logger;
    }

    public async Task<List<Category>> LoadCategoriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading categories");

            if (_sessionService.Mode == SessionMode.M3u)
            {
                return LoadM3uCategories();
            }

            // Check cache first
            var cacheKey = $"live_categories_{_sessionService.Host}_{_sessionService.Username}";
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

            var url = _sessionService.BuildApi("get_live_categories");
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
                    var category = new Category
                    {
                        Id = item.GetProperty("category_id").GetString() ?? "",
                        Name = item.GetProperty("category_name").GetString() ?? "",
                        ParentId = item.TryGetProperty("parent_id", out var parentId) ? parentId.GetInt32() : 0
                    };
                    categories.Add(category);
                }
            }

            // Cache the results
            await _cacheService.SetDataAsync(cacheKey, categories, TimeSpan.FromHours(2), cancellationToken);

            _logger.LogInformation("✅ API SUCCESS: Loaded {Count} categories from API and cached for future use", categories.Count);
            return categories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading categories");
            throw;
        }
    }

    public async Task<List<Channel>> LoadChannelsForCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading channels for category: {CategoryName}", category.Name);

            if (_sessionService.Mode == SessionMode.M3u)
            {
                return LoadM3uChannelsForCategory(category);
            }

            // Check cache first
            var cacheKey = $"channels_{_sessionService.Host}_{_sessionService.Username}_{category.Id}";
            _logger.LogInformation("🔍 Checking channels cache with key: {CacheKey}", cacheKey);
            var cachedChannels = await _cacheService.GetDataAsync<List<Channel>>(cacheKey, cancellationToken);
            if (cachedChannels != null)
            {
                var cacheHitMsg = $"📱 CACHE HIT: Loaded {cachedChannels.Count} channels from CACHE for category: {category.Name} (no API call needed)";
                _logger.LogInformation(cacheHitMsg);
                _rawOutputLogger?.Invoke(cacheHitMsg + "\n");
                return cachedChannels;
            }
            var cacheMissMsg = $"🌐 API CALL: Channels cache MISS - Loading from API server for category: {category.Name}";
            _logger.LogInformation(cacheMissMsg);
            _rawOutputLogger?.Invoke(cacheMissMsg + "\n");

            var url = _sessionService.BuildApi("get_live_streams", ("category_id", category.Id));
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

            // Cache the results
            await _cacheService.SetDataAsync(cacheKey, channels, TimeSpan.FromMinutes(30), cancellationToken);

            _logger.LogInformation("✅ API SUCCESS: Loaded {Count} channels from API for category {CategoryName} and cached for future use", channels.Count, category.Name);
            return channels;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading channels for category: {CategoryName}", category.Name);
            throw;
        }
    }

    public async Task LoadEpgForChannelAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_sessionService.Mode == SessionMode.M3u)
            {
                LoadM3uEpgForChannel(channel);
                return;
            }

            if (string.IsNullOrEmpty(channel.EpgChannelId))
            {
                return;
            }

            // Check cache first
            var cacheKey = $"epg_{_sessionService.Host}_{_sessionService.Username}_{channel.Id}";
            var cachedEpg = await _cacheService.GetDataAsync<EpgData>(cacheKey, cancellationToken);
            if (cachedEpg != null && cachedEpg.IsStillValid())
            {
                var epgCacheHitMsg = $"📱 CACHE HIT: EPG loaded from CACHE for channel: {channel.Name} (no API call needed)";
                _logger.LogInformation(epgCacheHitMsg);
                _rawOutputLogger?.Invoke(epgCacheHitMsg + "\n");
                channel.NowTitle = cachedEpg.NowTitle;
                channel.NowDescription = cachedEpg.NowDescription;
                channel.NowTimeRange = cachedEpg.NowTimeRange;
                channel.EpgLoaded = true;
                return;
            }

            var epgCacheMissMsg = $"🌐 API CALL: EPG cache MISS - Loading from API server for channel: {channel.Name}";
            _logger.LogInformation(epgCacheMissMsg);
            _rawOutputLogger?.Invoke(epgCacheMissMsg + "\n");

            var url = _sessionService.BuildApi("get_simple_data_table", ("stream_id", channel.Id.ToString()));
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            // Parse EPG data and update channel properties
            var epgData = new EpgData();

            // Handle potential base64 encoded response
            var trimmed = response.AsSpan().TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            {
                _logger.LogWarning("EPG non-JSON response skipped for channel: {ChannelName}", channel.Name);
                channel.EpgLoaded = true;
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("epg_listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                {
                    var nowUtc = DateTime.UtcNow;
                    bool found = false;
                    EpgEntry? fallbackFirstFuture = null;
                    DateTime? latestEndTime = null;

                    foreach (var el in listings.EnumerateArray())
                    {
                        var start = GetUnixTimestamp(el, "start_timestamp");
                        var end = GetUnixTimestamp(el, "stop_timestamp");
                        if (start == DateTime.MinValue || end == DateTime.MinValue) continue;

                        // Track the latest end time across all EPG entries for smart cache expiration
                        if (!latestEndTime.HasValue || end > latestEndTime.Value)
                        {
                            latestEndTime = end;
                        }

                        string titleRaw = TryGetString(el, "title", "name", "programme", "program");
                        string descRaw = TryGetString(el, "description", "desc", "info", "plot", "short_description");
                        string title = DecodeMaybeBase64(titleRaw);
                        string desc = DecodeMaybeBase64(descRaw);

                        bool nowFlag = el.TryGetProperty("now_playing", out var np) &&
                                      (np.ValueKind == JsonValueKind.Number ? np.GetInt32() == 1 :
                                       (np.ValueKind == JsonValueKind.String && np.GetString() == "1"));
                        bool isCurrent = nowFlag || (nowUtc >= start && nowUtc < end);

                        if (isCurrent && !string.IsNullOrWhiteSpace(title))
                        {
                            epgData.NowTitle = title;
                            epgData.NowDescription = desc;
                            epgData.NowTimeRange = $"{start.ToLocalTime():h:mm tt} - {end.ToLocalTime():h:mm tt}";
                            found = true;
                            // Don't break here - we still need to find the latest end time
                        }

                        if (!isCurrent && start > nowUtc && fallbackFirstFuture == null && !string.IsNullOrWhiteSpace(title))
                        {
                            fallbackFirstFuture = new EpgEntry { StartUtc = start, EndUtc = end, Title = title, Description = desc };
                        }
                    }

                    // Set the latest show end time for smart cache expiration
                    epgData.LatestShowEndUtc = latestEndTime;

                    if (!found && fallbackFirstFuture != null)
                    {
                        epgData.NowTitle = fallbackFirstFuture.Title;
                        epgData.NowDescription = fallbackFirstFuture.Description ?? string.Empty;
                        epgData.NowTimeRange = $"{fallbackFirstFuture.StartUtc.ToLocalTime():h:mm tt} - {fallbackFirstFuture.EndUtc.ToLocalTime():h:mm tt}";
                        found = true;
                    }

                    // Update channel with EPG data
                    channel.NowTitle = epgData.NowTitle;
                    channel.NowDescription = epgData.NowDescription;
                    channel.NowTimeRange = epgData.NowTimeRange;
                    channel.EpgLoaded = true;

                    // Cache the results with smart expiration based on EPG content
                    // The EpgData.IsStillValid() method will determine when to expire based on the latest show end time
                    await _cacheService.SetDataAsync(cacheKey, epgData, null, cancellationToken);

                    var expirationInfo = epgData.LatestShowEndUtc.HasValue
                        ? $"until {epgData.LatestShowEndUtc.Value.ToLocalTime():MM/dd HH:mm}"
                        : "for 30 minutes (fallback)";
                    _logger.LogInformation("✅ API SUCCESS: EPG loaded from API for channel {ChannelName} and cached {ExpirationInfo}", channel.Name, expirationInfo);
                    _rawOutputLogger?.Invoke($"📺 EPG cached for {channel.Name} {expirationInfo}\n");
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse EPG JSON for channel: {ChannelName}", channel.Name);
                channel.EpgLoaded = true;
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading EPG for channel: {ChannelName}", channel.Name);
            // Don't throw - EPG failures shouldn't break channel loading
        }
    }

    public async Task LoadEpgForChannelsAsync(IEnumerable<Channel> channels, CancellationToken cancellationToken = default)
    {
        var channelList = channels.ToList();
        _logger.LogInformation("Loading EPG for {Count} channels", channelList.Count);

        // Process in batches to avoid overwhelming the API
        const int batchSize = 10;
        var batches = channelList.Chunk(batchSize);

        foreach (var batch in batches)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var tasks = batch.Select(channel => LoadEpgForChannelAsync(channel, cancellationToken));
            await Task.WhenAll(tasks);

            // Small delay between batches
            await Task.Delay(100, cancellationToken);
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
            categories.Add(new Category { Id = "all", Name = "All Channels" });
        }

        return categories;
    }

    private List<Channel> LoadM3uChannelsForCategory(Category category)
    {
        var channels = new List<Channel>();
        var playlistChannels = category.Id == "all"
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

        var str = tsEl.GetString();
        if (string.IsNullOrEmpty(str) || !long.TryParse(str, out var unix) || unix <= 0)
            return DateTime.MinValue;

        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
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