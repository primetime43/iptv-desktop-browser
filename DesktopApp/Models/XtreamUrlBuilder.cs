namespace DesktopApp.Models;

public static class XtreamUrlBuilder
{
    // Accept raw values and encode each query component exactly once.
    public static string BuildApi(string baseUrl, string endpoint, string username, string password,
        params (string key, string value)[] parameters)
    {
        var query = new List<(string key, string value)>
        {
            ("username", username), ("password", password)
        };
        query.AddRange(parameters);
        return $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}?" +
            string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.key)}={Uri.EscapeDataString(p.value)}"));
    }

    public static string BuildStream(string baseUrl, string streamPath, string username, string password,
        string streamId, string extension) =>
        $"{baseUrl.TrimEnd('/')}/{streamPath.Trim('/')}/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{Uri.EscapeDataString(streamId)}.{Uri.EscapeDataString(extension)}";
}
