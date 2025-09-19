using System.Text;
using System.Text.Json;
using DesktopApp.Models;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public class ChannelService : IChannelService
{
    private readonly ISessionService _sessionService;
    private readonly IHttpService _httpService;
    private readonly ILogger<ChannelService> _logger;

    public ChannelService(
        ISessionService sessionService,
        IHttpService httpService,
        ILogger<ChannelService> logger)
    {
        _sessionService = sessionService;
        _httpService = httpService;
        _logger = logger;
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

            _logger.LogInformation("Loaded {Count} categories", categories.Count);
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

            _logger.LogInformation("Loaded {Count} channels for category {CategoryName}", channels.Count, category.Name);
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

            var url = _sessionService.BuildApi("get_simple_data_table", ("stream_id", channel.Id.ToString()));
            var response = await _httpService.GetStringAsync(url, cancellationToken);

            // Parse EPG data and update channel properties
            // Implementation would continue here...

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
}