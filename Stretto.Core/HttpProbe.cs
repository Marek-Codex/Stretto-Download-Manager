using System.Net;
using System.Net.Http.Headers;

namespace Stretto.Core;

/// <summary>
/// Asks the server what we are dealing with before committing to a strategy.
/// Uses a one-byte ranged GET rather than HEAD: plenty of servers and CDNs either
/// reject HEAD or answer it with headers that disagree with the real GET.
/// </summary>
public static class HttpProbe
{
    public static async Task<RemoteFileInfo> ProbeAsync(
        HttpClient http, DownloadRequest request, CancellationToken ct)
    {
        using var probe = BuildRequest(request);
        probe.Headers.Range = new RangeHeaderValue(0, 0);

        using var lease = request.HostBudget is { } budget
            ? await budget.AcquireAsync(request.Url, ct).ConfigureAwait(false) : null;

        using var response = await http
            .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        request.HostBudget?.Observe(response, request.Url);
        RetryPolicy.EnsureSuccess(response);

        var finalUrl = response.RequestMessage?.RequestUri ?? request.Url;
        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;

        // A 206 reports the true size in Content-Range; a 200 means the server ignored
        // the Range header entirely and is about to stream the whole body at us.
        long? total = supportsRanges
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

        return new RemoteFileInfo
        {
            FinalUrl = finalUrl,
            TotalLength = total,
            SupportsRanges = supportsRanges && total is > 0,
            SuggestedFileName = request.FileName ?? ResolveFileName(response, finalUrl),
            ContentType = response.Content.Headers.ContentType?.MediaType,
            // Weak tags permit semantically equivalent but byte-different representations.
            // That is fine for a web cache. It is not permission to stitch their ranges.
            Validator = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString()
                        : response.Content.Headers.LastModified?.ToString("R"),
            LastModified = response.Content.Headers.LastModified,
        };
    }

    public static HttpRequestMessage BuildRequest(DownloadRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, request.Url);

        foreach (var (name, value) in request.Headers)
            message.Headers.TryAddWithoutValidation(name, value);

        if (!string.IsNullOrEmpty(request.Cookie))
            message.Headers.TryAddWithoutValidation("Cookie", request.Cookie);

        return message;
    }

    private static string ResolveFileName(HttpResponseMessage response, Uri finalUrl)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var fromHeader = disposition?.FileNameStar ?? disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fromHeader))
            return Sanitize(fromHeader.Trim('"'));

        var fromPath = Path.GetFileName(Uri.UnescapeDataString(finalUrl.AbsolutePath));
        return string.IsNullOrWhiteSpace(fromPath) ? "download.bin" : Sanitize(fromPath);
    }

    private static string Sanitize(string name)
    {
        var cleaned = string.Concat(name.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(cleaned) ? "download.bin" : cleaned;
    }
}
