using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Stretto.App.Services;

/// <summary>What a check found.</summary>
public sealed record UpdateInfo(Version Latest, string Tag, string PageUrl, bool IsNewer);

/// <summary>
/// Asks GitHub Releases whether a newer Stretto exists. It only reports: nothing is
/// downloaded or installed, so a check can never break a working install.
///
/// One unauthenticated request against the public API, which allows 60 an hour per IP;
/// the automatic check runs at most once a day.
/// </summary>
public static class Updates
{
    /// <summary>
    /// "owner/repo" of the public releases. Empty until there is one, which turns the whole
    /// feature into a quiet no-op rather than a check against someone else's project.
    /// </summary>
    public const string ReleasesRepo = "Marek-Codex/Stretto-Download-Manager";

    public static bool ManagedByPackageSource => StartupRegistration.IsPackaged;
    public static bool IsConfigured => ReleasesRepo.Length > 0 && !ManagedByPackageSource;

    /// <summary>The running version, from the project's Version property.</summary>
    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public static async Task<UpdateInfo> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("No release channel is set up for this build.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ReleasesRepo}/releases/latest");
        // GitHub refuses API requests that carry no user agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Stretto", CurrentText));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        var page = json.RootElement.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";

        if (!TryParseTag(tag, out var latest))
            throw new InvalidDataException($"The latest release is tagged \"{tag}\", which is not a version number.");

        return new UpdateInfo(latest, tag, page, latest > Current);
    }

    /// <summary>Accepts "v1.2.3", "1.2" and "1.2.3-beta", comparing only the numbers.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0) text = text[..end];

        if (Version.TryParse(text.Contains('.') ? text : text + ".0", out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }
}
