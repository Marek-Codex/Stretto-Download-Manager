namespace Stretto.Core;

/// <summary>
/// Builds a curl command reproducing a download request, for debugging an expired link
/// outside the app, written for PowerShell (where Windows users paste it). Pure string
/// building, so it is unit tested rather than eyeballed.
/// </summary>
public static class CurlCommand
{
    public static string Build(Uri url, IReadOnlyDictionary<string, string>? headers = null, string? cookie = null,
        string? username = null, string? password = null)
    {
        var parts = new List<string>
        {
            // curl.exe, not curl: Windows PowerShell 5 aliases the bare name to Invoke-WebRequest.
            "curl.exe", "--fail", "--location", "--remote-name", Quote(url.ToString()),
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
    /// PowerShell single quotes: the one form where nothing inside is interpreted, so a
    /// <c>$(…)</c> planted in a URL or cookie stays text. Double quotes would run it on paste.
    /// An apostrophe is written twice, which is PowerShell's only escape inside them.
    /// </summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
