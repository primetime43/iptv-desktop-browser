using System.Security.Cryptography;
using System.Text.Json;
using DesktopApp.Models;
using DesktopApp.Security;
using Serilog;

var directory = Path.Combine(Path.GetTempPath(), "iptv-security-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var passed = 0;
try
{
    foreach (var input in new[]
    {
        "GET https://example.test/player_api.php?username=alice&password=secret123",
        "FFMPEG Input #0 from 'http://example.test/live/alice/secret123/1.ts':",
        "rtsp://alice:secret123@example.test/channel",
        "https://example.test/arbitrary/secret123?custom_auth=secret123#secret123",
        "{\"url\":\"https:\\/\\/example.test/live/alice/secret123/1.ts\"}",
        "{\"password\":\"secret123\",\"username\":\"alice\",\"status\":\"Active\"}",
        "{\"password\":\"escaped\\\"secret123\"}",
        "{\"password\":\"secret123...<truncated>",
        "Authorization: Bearer secret123\r\nCookie: session=secret123",
        "{\"access_token\":\"secret123\",\"api_key\":\"secret123\"}"
    })
    {
        var output = DiagnosticRedactor.Redact(input);
        Check(!output.Contains("secret123") && !output.Contains("alice"), "Diagnostic secret redaction");
    }
    var special = "p&ss\"\\word";
    var variants = string.Join(" ", special, Uri.EscapeDataString(special), JsonSerializer.Serialize(special));
    var redacted = DiagnosticRedactor.Redact(variants, special);
    Check(!redacted.Contains(special) && !redacted.Contains(Uri.EscapeDataString(special)) &&
        !redacted.Contains(JsonSerializer.Serialize(special)[1..^1]), "Known credential encodings");
    Check(DiagnosticRedactor.Redact("Response: HTTP 401 Unauthorized (125 ms)") ==
        "Response: HTTP 401 Unauthorized (125 ms)", "Useful diagnostics retained");

    // Exercise the real Serilog sink, including structured properties and exceptions.
    var logPath = Path.Combine(directory, "test.log");
    Session.Password = "secret123";
    using (var logger = new LoggerConfiguration().WriteTo.File(new RedactingLogFormatter(), logPath).CreateLogger())
    {
        logger.Error(new InvalidOperationException("Request https://example.test/live/alice/secret123/1.ts failed"),
            "GET {Url}; server said {Detail}", "https://example.test/?password=secret123", "secret123");
    }
    var log = File.ReadAllText(logPath);
    Check(!log.Contains("secret123") && !log.Contains("alice") && log.Contains("InvalidOperationException"),
        "File logs redact structured values and exceptions");

    var url = "https://example.test/live/alice/secret123/42.ts?token=another-secret";
    var schedules = new Dictionary<string, List<ScheduledRecording>>
    {
        ["account-one"] = [new() { Title = "Morning news", StreamUrl = url }],
        ["account-two"] = [new() { Title = "Evening news", StreamUrl = url + "2" }]
    };
    var series = new Dictionary<string, List<SeriesRecording>>
    {
        ["account-one"] = [new() { SeriesName = "Weekly show", StreamUrl = url }]
    };
    foreach (var payload in new[]
    {
        JsonSerializer.Serialize(schedules), JsonSerializer.Serialize(series),
        JsonSerializer.Serialize(schedules["account-one"]), JsonSerializer.Serialize(series["account-one"])
    })
    {
        var path = Path.Combine(directory, Guid.NewGuid() + ".json");
        // Simulate an existing plaintext file, including the legacy array layout.
        File.WriteAllText(path, payload);
        Check(ProtectedRecordingFile.ReadAllText(path) == payload, "Legacy migration preserves every account and field");
        var protectedText = File.ReadAllText(path);
        Check(!protectedText.Contains("secret123") && !protectedText.Contains("StreamUrl") &&
            !protectedText.Contains("another-secret"), "Migrated file contains no plaintext URLs");
        Check(ProtectedRecordingFile.ReadAllText(path) == payload, "DPAPI round trip preserves playback URL");
        ProtectedRecordingFile.WriteAllText(path, payload);
        Check(ProtectedRecordingFile.ReadAllText(path) == payload, "Atomic encrypted replacement");
    }

    var newPath = Path.Combine(directory, "new.json");
    var newPayload = JsonSerializer.Serialize(schedules);
    ProtectedRecordingFile.WriteAllText(newPath, newPayload);
    Check(ProtectedRecordingFile.ReadAllText(newPath) == newPayload && !File.ReadAllText(newPath).Contains("secret123"),
        "New recording file is protected");

    foreach (var damaged in new[]
    {
        "{\"Format\":\"iptv-recordings-dpapi-v1\",\"ProtectedData\":\"AQIDBA==\"}",
        "{\"Format\":\"unknown-version\",\"ProtectedData\":\"AQIDBA==\"}",
        "{invalid-json"
    })
    {
        var path = Path.Combine(directory, Guid.NewGuid() + ".json");
        File.WriteAllText(path, damaged);
        var failed = false;
        try { ProtectedRecordingFile.ReadAllText(path); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or JsonException) { failed = true; }
        Check(failed && File.ReadAllText(path) == damaged, "Unreadable files fail without overwriting data");
    }
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "No migration temporary files left behind");
    Console.WriteLine($"Passed {passed} security regression checks.");
}
finally
{
    Session.Password = string.Empty;
    // Only remove files created by this test run, never user application data.
    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    passed++;
}
