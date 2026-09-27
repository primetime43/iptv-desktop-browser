using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopApp.Security;

public static class DiagnosticRedactor
{
    // Hide the entire URL: playlist providers can put credentials in arbitrary paths,
    // query parameters, user-info, or fragments (including JSON-escaped URLs).
    private static readonly Regex Url = new(
        @"\b[a-z][a-z0-9+.-]*:(?:/|\\/){2}[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JsonSecret = new(
        @"(""(?:password|passwd|pwd|username|user|token|access_token|refresh_token|api_key|apikey|authorization|cookie|set-cookie)""\s*:\s*)(?:""(?:\\.|[^""\\])*""?|[^,}\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LabeledSecret = new(
        @"\b(password|passwd|pwd|username|token|access_token|refresh_token|api_key|apikey|authorization|cookie|set-cookie)(\s*[:=]\s*)[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Redact(string text, params string?[] secrets)
    {
        text = Url.Replace(text, "[redacted URL]");
        text = JsonSecret.Replace(text, "$1\"[redacted]\"");
        text = LabeledSecret.Replace(text, "$1$2[redacted]");
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s!.Length))
        {
            text = text.Replace(JsonSerializer.Serialize(secret)[1..^1], "[redacted]", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(secret!), "[redacted]", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(secret!, "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }
}
