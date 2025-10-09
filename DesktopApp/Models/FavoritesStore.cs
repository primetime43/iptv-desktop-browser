using System.IO;
using System.Text.Json;

namespace DesktopApp.Models;

public class FavoriteChannel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Logo { get; set; }
    public string? EpgChannelId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class FavoritesStore
{
    private readonly string _filePath;

    public FavoritesStore()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDataDir = Path.Combine(appDataPath, "IPTV-Desktop-Browser");
        Directory.CreateDirectory(appDataDir);
        _filePath = Path.Combine(appDataDir, "favorites.json");
    }

    public Dictionary<string, List<FavoriteChannel>> GetAll()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, List<FavoriteChannel>>();

        try
        {
            var json = File.ReadAllText(_filePath);

            // Try to deserialize as the new format first
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<FavoriteChannel>>>(json) ?? new Dictionary<string, List<FavoriteChannel>>();
            }
            catch
            {
                // If that fails, try to migrate from old format (List<int>)
                var oldFormat = JsonSerializer.Deserialize<Dictionary<string, List<int>>>(json);
                if (oldFormat != null)
                {
                    var newFormat = new Dictionary<string, List<FavoriteChannel>>();
                    foreach (var kvp in oldFormat)
                    {
                        newFormat[kvp.Key] = kvp.Value.Select(id => new FavoriteChannel
                        {
                            Id = id,
                            Name = $"Channel {id}",
                            AddedAt = DateTime.UtcNow
                        }).ToList();
                    }
                    // Save in new format
                    Save(newFormat);
                    return newFormat;
                }
                return new Dictionary<string, List<FavoriteChannel>>();
            }
        }
        catch
        {
            return new Dictionary<string, List<FavoriteChannel>>();
        }
    }

    public List<FavoriteChannel> GetForCurrentSession(string sessionKey)
    {
        var allFavorites = GetAll();
        return allFavorites.TryGetValue(sessionKey, out var favorites) ? favorites : new List<FavoriteChannel>();
    }

    public bool IsFavorite(string sessionKey, int channelId)
    {
        var favorites = GetForCurrentSession(sessionKey);
        return favorites.Any(f => f.Id == channelId);
    }

    public void AddFavorite(string sessionKey, Channel channel)
    {
        var allFavorites = GetAll();
        if (!allFavorites.TryGetValue(sessionKey, out var favorites))
        {
            favorites = new List<FavoriteChannel>();
            allFavorites[sessionKey] = favorites;
        }

        if (!favorites.Any(f => f.Id == channel.Id))
        {
            favorites.Add(new FavoriteChannel
            {
                Id = channel.Id,
                Name = channel.Name,
                Logo = channel.Logo,
                EpgChannelId = channel.EpgChannelId,
                AddedAt = DateTime.UtcNow
            });
            Save(allFavorites);
        }
    }

    public void RemoveFavorite(string sessionKey, int channelId)
    {
        var allFavorites = GetAll();
        if (allFavorites.TryGetValue(sessionKey, out var favorites))
        {
            var favoriteToRemove = favorites.FirstOrDefault(f => f.Id == channelId);
            if (favoriteToRemove != null)
            {
                favorites.Remove(favoriteToRemove);
                Save(allFavorites);
            }
        }
    }

    private void Save(Dictionary<string, List<FavoriteChannel>> favorites)
    {
        try
        {
            var json = JsonSerializer.Serialize(favorites, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Silently ignore save errors
        }
    }

    /// <summary>
    /// Exports favorites for the current session to a JSON file.
    /// </summary>
    /// <param name="sessionKey">The session key identifying the favorites to export</param>
    /// <param name="exportPath">The file path to export to</param>
    /// <returns>True if export succeeded, false otherwise</returns>
    public bool ExportFavorites(string sessionKey, string exportPath)
    {
        try
        {
            var favorites = GetForCurrentSession(sessionKey);
            var json = JsonSerializer.Serialize(favorites, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(exportPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Imports favorites from a JSON file and merges them with the current session's favorites.
    /// Existing favorites with the same ID will not be overwritten.
    /// </summary>
    /// <param name="sessionKey">The session key to import favorites into</param>
    /// <param name="importPath">The file path to import from</param>
    /// <returns>Number of new favorites imported, or -1 on error</returns>
    public int ImportFavorites(string sessionKey, string importPath)
    {
        try
        {
            if (!File.Exists(importPath))
                return -1;

            var json = File.ReadAllText(importPath);
            var importedFavorites = JsonSerializer.Deserialize<List<FavoriteChannel>>(json);

            if (importedFavorites == null || importedFavorites.Count == 0)
                return 0;

            var allFavorites = GetAll();
            if (!allFavorites.TryGetValue(sessionKey, out var existingFavorites))
            {
                existingFavorites = new List<FavoriteChannel>();
                allFavorites[sessionKey] = existingFavorites;
            }

            int importedCount = 0;
            foreach (var favorite in importedFavorites)
            {
                // Only add if not already in favorites (by ID)
                if (!existingFavorites.Any(f => f.Id == favorite.Id))
                {
                    existingFavorites.Add(favorite);
                    importedCount++;
                }
            }

            if (importedCount > 0)
            {
                Save(allFavorites);
            }

            return importedCount;
        }
        catch
        {
            return -1;
        }
    }
}