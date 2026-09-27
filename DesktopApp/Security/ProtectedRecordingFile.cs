using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopApp.Security;

/// <summary>Protects schedules, including all accounts' authenticated stream URLs.</summary>
public static class ProtectedRecordingFile
{
    private const string Format = "iptv-recordings-dpapi-v1";
    private static readonly object Sync = new();

    public static string ReadAllText(string path)
    {
        lock (Sync)
        {
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Format", out var format))
            {
                if (format.GetString() != Format)
                    throw new InvalidDataException("Unsupported protected recording file format.");
                var data = Convert.FromBase64String(document.RootElement.GetProperty("ProtectedData").GetString()!);
                var plain = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }

            // Migrate both legacy arrays and per-account dictionaries before returning.
            // This covers inactive accounts too and leaves no plaintext backup.
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new InvalidDataException("Invalid recording file.");
            WriteAllText(path, text);
            return text;
        }
    }

    public static void WriteAllText(string path, string text)
    {
        lock (Sync)
        {
            var plain = Encoding.UTF8.GetBytes(text);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var envelope = JsonSerializer.Serialize(new
            {
                Format,
                ProtectedData = Convert.ToBase64String(encrypted)
            });
            // The temporary file contains only ciphertext; replacement is atomic.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, envelope);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
