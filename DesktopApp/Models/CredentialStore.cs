using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace DesktopApp.Models;

public sealed class CredentialProfile
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseSsl { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty; // plain only when returned to caller, never persisted
    public string Display => $"{Username}@{Server}:{Port}{(UseSsl ? "+ssl" : string.Empty)}";
    public string Type => "Xtream";
}

public sealed class M3uProfile
{
    public string PlaylistUrl { get; set; } = string.Empty;
    public string? XmltvUrl { get; set; }
    public DateTime SavedUtc { get; set; }
    public string Display => GetDisplayName();
    public string Type => "M3U";

    private string GetDisplayName()
    {
        if (string.IsNullOrWhiteSpace(PlaylistUrl)) return "Unknown Playlist";

        // Extract filename from URL or path
        try
        {
            if (Uri.TryCreate(PlaylistUrl, UriKind.Absolute, out var uri))
            {
                var segments = uri.Segments;
                if (segments.Length > 0)
                {
                    var lastSegment = segments[^1].Trim('/');
                    if (!string.IsNullOrEmpty(lastSegment))
                        return lastSegment;
                }
                return uri.Host;
            }
            else
            {
                // Local file path
                return System.IO.Path.GetFileName(PlaylistUrl);
            }
        }
        catch
        {
            return PlaylistUrl.Length > 50 ? PlaylistUrl[..50] + "..." : PlaylistUrl;
        }
    }
}

public static class CredentialStore
{
    private sealed class PersistedModel
    {
        public string? Server { get; set; }
        public int Port { get; set; }
        public bool UseSsl { get; set; }
        public string? Username { get; set; }
        public string? PasswordProtected { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    private sealed class M3uPersistedModel
    {
        public string? PlaylistUrl { get; set; }
        public string? XmltvUrl { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    private sealed class PersistedCollection
    {
        public List<PersistedModel> Profiles { get; set; } = new();
        public List<M3uPersistedModel> M3uPlaylists { get; set; } = new();
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IPTV-Desktop-Browser",
        "credentials.json");

    // Backwards compatibility: legacy single-object file
    private static bool TryMigrateLegacyFormat(string jsonText, out PersistedCollection migrated)
    {
        migrated = new PersistedCollection();
        try
        {
            var legacy = JsonSerializer.Deserialize<PersistedModel>(jsonText);
            if (legacy?.Server != null && legacy.Username != null && legacy.PasswordProtected != null)
            {
                migrated.Profiles.Add(legacy);
                SaveCollection(migrated); // overwrite file with new format
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static PersistedCollection LoadCollection()
    {
        try
        {
            if (!File.Exists(FilePath)) return new PersistedCollection();
            var json = File.ReadAllText(FilePath);
            // Detect array or object format
            if (json.TrimStart().StartsWith("{"))
            {
                try
                {
                    var col = JsonSerializer.Deserialize<PersistedCollection>(json);
                    if (col?.Profiles != null) return col;
                }
                catch
                {
                    // maybe legacy single object
                    if (TryMigrateLegacyFormat(json, out var migrated)) return migrated;
                }
            }
            else if (json.TrimStart().StartsWith("["))
            {
                var list = JsonSerializer.Deserialize<List<PersistedModel>>(json) ?? new();
                return new PersistedCollection { Profiles = list };
            }
        }
        catch { }
        return new PersistedCollection();
    }

    private static void SaveCollection(PersistedCollection collection)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(collection, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public static IReadOnlyList<CredentialProfile> GetAll()
    {
        var col = LoadCollection();
        return col.Profiles
            .OrderBy(p => p.Server)
            .ThenBy(p => p.Username)
            .Select(p => new CredentialProfile { Server = p.Server ?? string.Empty, Port = p.Port, UseSsl = p.UseSsl, Username = p.Username ?? string.Empty })
            .ToList();
    }

    public static bool TryGet(string server, string username, out CredentialProfile profile)
    {
        profile = new CredentialProfile();
        var col = LoadCollection();
        var match = col.Profiles.FirstOrDefault(p => string.Equals(p.Server, server, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
        if (match == null || match.PasswordProtected == null) return false;
        try
        {
            var protectedBytes = Convert.FromBase64String(match.PasswordProtected);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            profile = new CredentialProfile
            {
                Server = match.Server ?? string.Empty,
                Port = match.Port,
                UseSsl = match.UseSsl,
                Username = match.Username ?? string.Empty,
                Password = Encoding.UTF8.GetString(plainBytes)
            };
            return true;
        }
        catch { return false; }
    }

    public static void SaveOrUpdate(string server, int port, bool useSsl, string username, string password)
    {
        try
        {
            var col = LoadCollection();
            var existing = col.Profiles.FirstOrDefault(p => string.Equals(p.Server, server, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
            if (existing == null)
            {
                existing = new PersistedModel { Server = server, Username = username };
                col.Profiles.Add(existing);
            }
            existing.Port = port;
            existing.UseSsl = useSsl;
            existing.PasswordProtected = Convert.ToBase64String(protectedBytes);
            existing.SavedUtc = DateTime.UtcNow;
            SaveCollection(col);
        }
        catch { }
    }

    public static void Delete(string server, string username)
    {
        try
        {
            var col = LoadCollection();
            col.Profiles.RemoveAll(p => string.Equals(p.Server, server, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
            SaveCollection(col);
        }
        catch { }
    }

    public static void DeleteAll()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    // ===== M3U Playlist Management =====
    public static IReadOnlyList<M3uProfile> GetAllM3u()
    {
        var col = LoadCollection();
        return col.M3uPlaylists
            .OrderByDescending(p => p.SavedUtc)
            .Select(p => new M3uProfile
            {
                PlaylistUrl = p.PlaylistUrl ?? string.Empty,
                XmltvUrl = p.XmltvUrl,
                SavedUtc = p.SavedUtc
            })
            .ToList();
    }

    public static bool TryGetM3u(string playlistUrl, out M3uProfile profile)
    {
        profile = new M3uProfile();
        var col = LoadCollection();
        var match = col.M3uPlaylists.FirstOrDefault(p => string.Equals(p.PlaylistUrl, playlistUrl, StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;

        profile = new M3uProfile
        {
            PlaylistUrl = match.PlaylistUrl ?? string.Empty,
            XmltvUrl = match.XmltvUrl,
            SavedUtc = match.SavedUtc
        };
        return true;
    }

    public static void SaveOrUpdateM3u(string playlistUrl, string? xmltvUrl)
    {
        try
        {
            var col = LoadCollection();
            var existing = col.M3uPlaylists.FirstOrDefault(p => string.Equals(p.PlaylistUrl, playlistUrl, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new M3uPersistedModel { PlaylistUrl = playlistUrl };
                col.M3uPlaylists.Add(existing);
            }
            existing.XmltvUrl = xmltvUrl;
            existing.SavedUtc = DateTime.UtcNow;
            SaveCollection(col);
        }
        catch { }
    }

    public static void DeleteM3u(string playlistUrl)
    {
        try
        {
            var col = LoadCollection();
            col.M3uPlaylists.RemoveAll(p => string.Equals(p.PlaylistUrl, playlistUrl, StringComparison.OrdinalIgnoreCase));
            SaveCollection(col);
        }
        catch { }
    }

    // Backwards compatible single-profile helpers (still used by MainWindow existing flow)
    public static void Save(string server, int port, bool useSsl, string username, string password) => SaveOrUpdate(server, port, useSsl, username, password);
    public static bool TryLoad(out string server, out int port, out bool useSsl, out string username, out string password)
    {
        server = username = password = string.Empty; port = 0; useSsl = false;
        var first = GetAll().FirstOrDefault();
        if (first == null) return false;
        if (TryGet(first.Server, first.Username, out var prof))
        {
            server = prof.Server; port = prof.Port; useSsl = prof.UseSsl; username = prof.Username; password = prof.Password; return true;
        }
        return false;
    }

    public static bool TryLoadM3u(out string playlistUrl, out string? xmltvUrl)
    {
        playlistUrl = string.Empty; xmltvUrl = null;
        var first = GetAllM3u().FirstOrDefault();
        if (first == null) return false;
        playlistUrl = first.PlaylistUrl;
        xmltvUrl = first.XmltvUrl;
        return true;
    }
}
