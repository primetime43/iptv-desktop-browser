using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopApp.Models;

namespace DesktopApp.Services;

// This is discovery history, independent of the optional download cache. Only
// opaque keys and dates are persisted; never account names, URLs or credentials.
public sealed class ChannelHistoryStore(string? directory = null, TimeProvider? clock = null)
{
    private static readonly object Gate = new();
    private readonly string _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IPTV-Desktop-Browser", "ChannelHistory");
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string Key(params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    // A scope is a complete category or the complete index. Its first successful
    // snapshot establishes a baseline. Shared identities prevent moved/reappearing
    // channels from being called new. Favorites only read history.
    public void Apply(string account, string? scope, List<Channel> channels,
        Func<Channel, string> identity, CancellationToken token)
    {
        var keyed = channels.Select(c => (Channel: c, Key: Key(identity(c)))).ToArray();
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(_directory, Key(account) + ".json");
            var history = Read(path);
            var now = _clock.GetUtcNow();
            var scopeKey = scope == null ? null : Key(scope);
            var baseline = scopeKey != null && (history.Scopes.Contains(scopeKey) || history.Scopes.Contains(Key("all")));
            var changed = false;
            foreach (var (channel, key) in keyed)
            {
                token.ThrowIfCancellationRequested();
                history.Channels.TryGetValue(key, out var entry);
                if (entry == null && scope != null)
                {
                    entry = new Entry { DiscoveredUtc = baseline ? now : null };
                    history.Channels[key] = entry;
                    changed = true;
                }
                if (channel.AddedUtc > now || channel.AddedUtc <= DateTimeOffset.UnixEpoch) channel.AddedUtc = null;
                if (entry != null)
                {
                    if (channel.AddedUtc is { } date && date != entry.AddedUtc)
                    {
                        entry.AddedUtc = date;
                        changed = true;
                    }
                    channel.AddedUtc ??= entry.AddedUtc;
                    channel.DiscoveredUtc = entry.DiscoveredUtc;
                }
                channel.RefreshRecentlyAdded(now);
            }
            if (scopeKey != null) changed |= history.Scopes.Add(scopeKey);
            token.ThrowIfCancellationRequested();
            if (changed) Save(path, history);
        }
    }

    private static History Read(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<History>(File.ReadAllText(path)) is { Channels: not null, Scopes: not null } history)
                return history;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new History();
    }

    private void Save(string path, History history)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(history));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unavailable history must not prevent browsing or provider-date badges.
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public sealed class History
    {
        public HashSet<string> Scopes { get; set; } = new();
        public Dictionary<string, Entry> Channels { get; set; } = new();
    }
    public sealed class Entry
    {
        public DateTimeOffset? AddedUtc { get; set; }
        public DateTimeOffset? DiscoveredUtc { get; set; }
    }
}
