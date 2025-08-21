using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopApp.Models;

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

    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "credentials.json");

    public static void Save(string server, int port, bool useSsl, string username, string password)
    {
        try
        {
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
            var model = new PersistedModel
            {
                Server = server,
                Port = port,
                UseSsl = useSsl,
                Username = username,
                PasswordProtected = Convert.ToBase64String(protectedBytes),
                SavedUtc = DateTime.UtcNow
            };
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* swallow - persistence is best effort */ }
    }

    public static bool TryLoad(out string server, out int port, out bool useSsl, out string username, out string password)
    {
        server = username = password = string.Empty; port = 0; useSsl = false;
        try
        {
            if (!File.Exists(FilePath)) return false;
            var json = File.ReadAllText(FilePath);
            var model = JsonSerializer.Deserialize<PersistedModel>(json);
            if (model == null || string.IsNullOrEmpty(model.Server) || string.IsNullOrEmpty(model.Username) || string.IsNullOrEmpty(model.PasswordProtected)) return false;
            var protectedBytes = Convert.FromBase64String(model.PasswordProtected);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            password = Encoding.UTF8.GetString(plainBytes);
            server = model.Server;
            port = model.Port;
            useSsl = model.UseSsl;
            username = model.Username;
            return true;
        }
        catch { return false; }
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
