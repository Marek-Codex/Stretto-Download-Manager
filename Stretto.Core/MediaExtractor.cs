using System.Text.RegularExpressions;

namespace Stretto.Core;

public enum MediaKind
{
    /// <summary>A plain file the segmented engine can pull directly.</summary>
    Direct,
    /// <summary>An HLS playlist: many small segments that have to be stitched.</summary>
    Hls,
    /// <summary>An MPEG-DASH manifest, same problem as HLS.</summary>
    Dash,
    /// <summary>A page on a video site, which yt-dlp resolves and fetches.</summary>
    Site,
}

public sealed record MediaCandidate
{
    public required Uri Url { get; init; }
    public required string Label { get; init; }
    public required MediaKind Kind { get; init; }

    /// <summary>Where on the page it was found, for telling near-identical URLs apart.</summary>
    public string? Source { get; init; }

    /// <summary>The yt-dlp format to ask for, when one was picked. Null means its choice.</summary>
    public string? FormatId { get; init; }
    public int? HlsProgramIndex { get; init; }

    /// <summary>The title of the page it was seen on. The file name is chosen from it at
    /// download time, so a title that arrives late is still used.</summary>
    public string? PageTitle { get; init; }
    public string? Cookie { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>
    /// The rest of the browser's request headers, when this candidate came from the
    /// extension watching a real request rather than from a page we parsed. Null for
    /// anything MediaExtractor found by reading HTML, where there was no request to copy.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    public bool NeedsMuxing => Kind is MediaKind.Hls or MediaKind.Dash;
}

/// <summary>
/// Finds the media on a page. Regex over the HTML rather than a parser dependency: we are
/// looking for URLs in attributes and in inline script, not walking a document, and half the
/// interesting URLs on a modern video page are inside JavaScript where a DOM parser would not
/// help anyway.
/// </summary>
public static partial class MediaExtractor
{
    private static readonly string[] DirectExtensions =
        [".mp4", ".webm", ".mkv", ".mov", ".m4v", ".avi", ".flv", ".ogv", ".mp3", ".m4a", ".ogg", ".wav", ".flac"];

    [GeneratedRegex(@"<(?:video|source|audio)\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MediaTag();

    [GeneratedRegex(@"<meta\b[^>]*?(?:property|name)\s*=\s*[""']og:video(?::url|:secure_url)?[""'][^>]*?\bcontent\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OpenGraphVideo();

    [GeneratedRegex(@"<a\b[^>]*?\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex Anchor();

    /// <summary>Catches playlist and media URLs sitting inside inline script or JSON blobs.</summary>
    [GeneratedRegex(@"[""']([^""'\s\\]+?\.(?:m3u8|m3u|mpd|mp4|webm|m4a)(?:\?[^""'\s\\]*)?)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInScript();

    public static async Task<IReadOnlyList<MediaCandidate>> ExtractAsync(
        HttpClient http, Uri pageUrl, string? cookie = null, CancellationToken ct = default)
    {
        // The address may already point straight at the media, in which case there is no
        // page to scrape.
        if (ClassifyByExtension(pageUrl) is { } direct)
            return [new MediaCandidate { Url = pageUrl, Label = NameFor(pageUrl), Kind = direct, Source = "address", Cookie = cookie }];

        var (html, finalUrl, mime) = await FetchAsync(http, pageUrl, cookie, ct).ConfigureAwait(false);
        var streamKind = mime?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true ? MediaKind.Hls
            : mime?.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) == true ? MediaKind.Dash : (MediaKind?)null;
        if (streamKind is { } kind)
            return [new MediaCandidate { Url = finalUrl, Label = NameFor(finalUrl), Kind = kind, Source = "content type", Cookie = cookie }];
        return [.. Extract(html, finalUrl).Select(c => c with { Cookie = cookie, Referrer = finalUrl.ToString() })];
    }

    /// <summary>Split out from the fetch so it can be tested without a network.</summary>
    public static IReadOnlyList<MediaCandidate> Extract(string html, Uri pageUrl)
    {
        html = System.Net.WebUtility.HtmlDecode(html).Replace(@"\/", "/");
        var found = new List<MediaCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string raw, string source)
        {
            if (!TryResolve(pageUrl, raw, out var url))
                return;

            var kind = ClassifyByExtension(url);
            if (kind is null || !seen.Add(url.ToString()))
                return;

            found.Add(new MediaCandidate
            {
                Url = url,
                Label = "",       // assigned below, once the ordering is known
                Kind = kind.Value,
                Source = source,
            });
        }

        foreach (Match m in MediaTag().Matches(html)) Consider(m.Groups[1].Value, "video tag");
        foreach (Match m in OpenGraphVideo().Matches(html)) Consider(m.Groups[1].Value, "og:video");
        foreach (Match m in UrlInScript().Matches(html)) Consider(m.Groups[1].Value, "script");
        foreach (Match m in Anchor().Matches(html)) Consider(m.Groups[1].Value, "link");

        // XDM numbers its findings "HTML Video (1)", "HTML Video (2)"; do the same, but say
        // what kind of thing each one is so a stream is not mistaken for a plain file.
        var counters = new Dictionary<MediaKind, int>();

        return
        [
            .. found.Select(c =>
            {
                counters[c.Kind] = counters.GetValueOrDefault(c.Kind) + 1;
                var prefix = c.Kind switch
                {
                    MediaKind.Hls => "HLS stream",
                    MediaKind.Dash => "DASH stream",
                    _ => "HTML Video",
                };

                return c with { Label = $"{prefix} ({counters[c.Kind]})" };
            })
        ];
    }

    private static async Task<(string Text, Uri Url, string? Mime)> FetchAsync(HttpClient http, Uri pageUrl, string? cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);

        // Some sites serve a stripped page without a browser-shaped User-Agent.
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");

        if (!string.IsNullOrEmpty(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false),
            response.RequestMessage?.RequestUri ?? pageUrl, response.Content.Headers.ContentType?.MediaType);
    }

    private static bool TryResolve(Uri pageUrl, string raw, out Uri url)
    {
        url = pageUrl;
        raw = raw.Trim();

        if (raw.Length == 0 || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return false;

        // Protocol-relative URLs are common in embed markup.
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = pageUrl.Scheme + ":" + raw;

        if (!Uri.TryCreate(pageUrl, raw, out var resolved))
            return false;

        url = resolved;
        return url.Scheme is "http" or "https";
    }

    private static MediaKind? ClassifyByExtension(Uri url)
    {
        var path = url.AbsolutePath;

        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)) return MediaKind.Hls;
        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) return MediaKind.Dash;

        return DirectExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            ? MediaKind.Direct
            : null;
    }

    private static string NameFor(Uri url)
    {
        var name = Path.GetFileName(url.AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? "video" : name;
    }
}
