using System.Text.Json;
using DesktopApp.Models;
using Microsoft.Extensions.Logging;

namespace DesktopApp.Services;

public partial class ChannelService
{
    public async Task LoadEpgForChannelAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_sessionService.Mode == SessionMode.M3u)
            {
                LoadM3uEpgForChannel(channel);
                return;
            }

            var nowUtc = _clock.GetUtcNow().UtcDateTime;
            channel.RefreshCurrentProgram(nowUtc);
            if (string.IsNullOrEmpty(channel.EpgChannelId))
            {
                channel.NextEpgRefreshUtc = null;
                return;
            }

            // Retain the loaded guide in memory even when disk caching is disabled.
            if (channel.EpgSchedule?.IsStillValid(nowUtc) == true)
            {
                channel.NextEpgRefreshUtc = channel.EpgSchedule.ExpiresUtc;
                return;
            }

            channel.NextEpgRefreshUtc = nowUtc.AddMinutes(1);
            var cacheKey = $"{_cacheSettings.KeyPrefixes.Epg}_{_sessionService.Host}_{_sessionService.Username}_{channel.Id}";
            if (_sessionService.CachingEnabled)
            {
                var cached = await _cacheService.GetDataAsync<EpgData>(cacheKey, cancellationToken);
                if (cached?.IsStillValid(_clock.GetUtcNow().UtcDateTime) == true)
                {
                    ApplyEpg(channel, cached);
                    _logger.LogInformation("EPG schedule loaded from cache for {ChannelName}", channel.Name);
                    return;
                }
            }

            var url = _sessionService.BuildApi(_apiSettings.Actions.GetSimpleDataTable, ("stream_id", channel.Id.ToString()));
            var response = await _httpService.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("epg_listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
                throw new JsonException("EPG response does not contain a program schedule.");

            var programs = new List<EpgEntry>();
            foreach (var item in listings.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var start = GetUnixTimestamp(item, "start_timestamp");
                var end = GetUnixTimestamp(item, "stop_timestamp");
                if (start == DateTime.MinValue || end <= start) continue;
                var title = DecodeMaybeBase64(TryGetString(item, "title", "name", "programme", "program"));
                if (string.IsNullOrWhiteSpace(title)) continue;
                programs.Add(new EpgEntry
                {
                    StartUtc = start, EndUtc = end, Title = title,
                    Description = DecodeMaybeBase64(TryGetString(item, "description", "desc", "info", "plot", "short_description"))
                });
            }

            nowUtc = _clock.GetUtcNow().UtcDateTime;
            var data = new EpgData
            {
                Programs = programs.OrderBy(p => p.StartUtc).ToList(),
                CachedAt = nowUtc,
                ExpiresUtc = GetScheduleExpiration(programs, nowUtc)
            };
            ApplyEpg(channel, data);
            if (_sessionService.CachingEnabled)
                await _cacheService.SetDataAsync(cacheKey, data, data.ExpiresUtc - nowUtc, cancellationToken);

            _logger.LogInformation("EPG schedule loaded for {ChannelName}; expires at {Expiration}", channel.Name, data.ExpiresUtc);
            _rawOutputLogger?.Invoke($"EPG schedule loaded for {channel.Name}, valid until {data.ExpiresUtc.ToLocalTime():MM/dd HH:mm}\n");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Expired labels were cleared before the request; a provider outage must
            // not leave yesterday's show on screen or trigger requests every UI tick.
            channel.RefreshCurrentProgram(_clock.GetUtcNow().UtcDateTime);
            channel.NextEpgRefreshUtc = _clock.GetUtcNow().UtcDateTime.AddMinutes(1);
            _logger.LogWarning(ex, "Unable to load EPG for {ChannelName}", channel.Name);
        }
    }

    private void ApplyEpg(Channel channel, EpgData data)
    {
        channel.EpgSchedule = data;
        channel.NextEpgRefreshUtc = data.ExpiresUtc;
        channel.RefreshCurrentProgram(_clock.GetUtcNow().UtcDateTime);
    }

    private DateTime GetScheduleExpiration(List<EpgEntry> programs, DateTime nowUtc)
    {
        var minimum = TimeSpan.FromMinutes(Math.Max(1, _epgSettings.MinCacheValidityMinutes));
        var maximum = TimeSpan.FromHours(Math.Max(1, _epgSettings.MaxCacheValidityHours));
        var latestEnd = programs.Where(p => p.EndUtc > nowUtc).Select(p => (DateTime?)p.EndUtc).Max();
        var desired = latestEnd.HasValue
            ? latestEnd.Value.AddMinutes(-Math.Max(0, _epgSettings.SmartCacheExpirationMinutesBeforeShowEnd)) - nowUtc
            : minimum;
        var lifetime = desired < minimum ? minimum : desired;
        if (lifetime > maximum) lifetime = maximum;
        var expires = nowUtc + lifetime;
        // A minimum TTL must never outlive the final available program.
        return latestEnd.HasValue && latestEnd.Value < expires ? latestEnd.Value : expires;
    }
}
