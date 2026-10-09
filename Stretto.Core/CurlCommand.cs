namespace Stretto.Core;

/// <summary>
/// Builds a curl command reproducing a download request, for debugging an expired link
/// outside the app. Pure string building, so it is unit tested rather than eyeballed.
/// </summary>
public static class CurlCommand
{
    public static string Build(Uri url, IReadOnlyDictionary<string, string>? headers = null, string? cookie = null,
        string? username = null, string? password = null)
    {
        var parts = new List<string>
        {
            "curl", "--fail", "--location", "--remote-name", Quote(url.ToString()),
        };

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
                parts.Add($"--header {Quote($"{name}: {value}")}");
        }

        if (!string.IsNullOrEmpty(cookie))
            parts.Add($"--header {Quote($"Cookie: {cookie}")}");

        if (!string.IsNullOrEmpty(username))
        {
            parts.Add("--anyauth");
            parts.Add($"--user {Quote($"{username}:{password ?? ""}")}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Double quotes suit both shells curl is pasted into. Backslashes and quotes inside
    /// are escaped for curl's own argument parsing.
    /// </summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
