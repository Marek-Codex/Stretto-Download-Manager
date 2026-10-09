using System.Globalization;
using System.Text.RegularExpressions;

namespace Stretto.Core;

/// <summary>One rendition listed in an HLS master playlist.</summary>
public sealed record HlsVariant
{
    public required Uri Url { get; init; }

    /// <summary>Vertical resolution, when the playlist says. This is the "1080p".</summary>
    public int? Height { get; init; }

    /// <summary>Declared bits per second, which is the only quality hint some playlists give.</summary>
    public long? Bandwidth { get; init; }

    public string? Codecs { get; init; }

    /// <summary>What to show in a list of formats.</summary>
    public string Label
    {
        get
        {
            var quality = Height is { } height ? $"{height}p" : "Unknown quality";

            return Bandwidth is { } bits and > 0
                ? $"{quality} · {bits / 1_000_000d:0.#} Mbps"
                : quality;
        }
    }
}

/// <summary>
/// Reads an HLS master playlist.
///
/// This is what a site's "other formats" really are: one master playlist pointing at a
/// rendition per quality. Left alone, ffmpeg picks one itself, which is usually the highest
/// bitrate and not always what someone wants on a metered connection.
///
/// Only the master level is parsed. The segment lists inside each rendition are ffmpeg's
/// business, and re-implementing them here would be rewriting the part that already works.
/// </summary>
public static partial class HlsPlaylist
{
    /// <summary>
    /// Pulls the renditions out of a master playlist. An empty list means this was a media
    /// playlist, which has no alternatives to offer and should be used as it is.
    /// </summary>
    public static IReadOnlyList<HlsVariant> ParseMaster(string text, Uri baseUrl)
    {
        var variants = new List<HlsVariant>();

        // Lines are one per rendition: the EXT-X-STREAM-INF attributes, then a URI on the
        // next line that is not a comment.
        var lines = text.ReplaceLineEndings().Split(Environment.NewLine);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                continue;

            var uri = NextUri(lines, i);
            if (uri is null || !Uri.TryCreate(baseUrl, uri, out var resolved))
                continue;

            var attributes = line["#EXT-X-STREAM-INF:".Length..];

            variants.Add(new HlsVariant
            {
                Url = resolved,
                Height = ParseHeight(Attribute(attributes, "RESOLUTION")),
                Bandwidth = long.TryParse(Attribute(attributes, "BANDWIDTH"), out var bandwidth) ? bandwidth : null,
                Codecs = Attribute(attributes, "CODECS")?.Trim('"'),
            });
        }

        // Best first, which is what someone scanning the list expects to see at the top.
        return [.. variants
            .OrderByDescending(v => v.Height ?? 0)
            .ThenByDescending(v => v.Bandwidth ?? 0)];
    }

    /// <summary>Fetches a playlist and reads its renditions. Empty when it has none.</summary>
    public static async Task<IReadOnlyList<HlsVariant>> FetchVariantsAsync(
        HttpClient http,
        Uri playlist,
        string? cookie = null,
        string? referrer = null,
        CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, playlist);

            if (!string.IsNullOrEmpty(cookie))
                request.Headers.TryAddWithoutValidation("Cookie", cookie);

            if (!string.IsNullOrEmpty(referrer))
                request.Headers.TryAddWithoutValidation("Referer", referrer);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return [];

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Cheap guard against handing a megabyte of something else to the parser.
            if (!text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                return [];

            return ParseMaster(text, response.RequestMessage?.RequestUri ?? playlist);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // No variants is a perfectly good answer: the caller falls back to the playlist
            // as given, which is what happened before any of this existed.
            return [];
        }
    }

    /// <summary>The first line after <paramref name="index"/> that is not blank or a comment.</summary>
    private static string? NextUri(string[] lines, int index)
    {
        for (var i = index + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            return line;
        }

        return null;
    }

    /// <summary>RESOLUTION is WIDTHxHEIGHT; only the height is spoken about.</summary>
    private static int? ParseHeight(string? resolution)
    {
        if (resolution is null)
            return null;

        var cross = resolution.IndexOf('x', StringComparison.OrdinalIgnoreCase);

        return cross > 0 && int.TryParse(resolution[(cross + 1)..], NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out var height)
            ? height
            : null;
    }

    /// <summary>
    /// Reads one attribute out of the comma separated list. Quoted values can contain
    /// commas, which is why this is a regex and not a Split.
    /// </summary>
    private static string? Attribute(string attributes, string name)
    {
        var match = Regex.Match(attributes,
            $@"(?:^|,)\s*{Regex.Escape(name)}\s*=\s*(""[^""]*""|[^,]*)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
