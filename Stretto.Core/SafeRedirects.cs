using System.Net;
using System.Net.Http.Headers;

namespace Stretto.Core;

/// <summary>
/// Follows redirects for the download client, dropping the browser's session on the way out
/// of the site it belongs to.
///
/// The built-in redirect handling strips Authorization and nothing else, so a Cookie header
/// copied from the browser used to ride along to whatever host a redirect named: a CDN, a
/// link shortener's next hop, or someone else's server entirely. Browsers scope cookies to
/// their domain; this keeps them roughly as tidy without knowing each cookie's attributes.
/// </summary>
public sealed class SafeRedirects : DelegatingHandler
{
    private const int MaxRedirects = 10;

    public SafeRedirects(HttpMessageHandler inner) : base(inner) { }

    /// <summary>
    /// Captured headers do not tell us a cookie's Domain or host-only flag. Keep that
    /// context on the exact origin rather than guessing which neighbouring host owns it.
    /// </summary>
    public static bool SameOrigin(Uri from, Uri to) =>
        string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(from.IdnHost, to.IdnHost, StringComparison.OrdinalIgnoreCase)
        && from.Port == to.Port;

    /// <summary>Removes what identifies the user to the original site.</summary>
    public static void StripSession(HttpRequestHeaders headers)
    {
        // Unknown captured headers can contain credentials too. Only transport and
        // representation headers needed for an ordinary ranged GET cross the boundary.
        foreach (var name in headers.Select(h => h.Key).Where(name => !CrossOriginHeaders.Contains(name)).ToList())
            headers.Remove(name);
    }

    private static readonly HashSet<string> CrossOriginHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Encoding", "Accept-Language", "User-Agent", "Range",
        "If-Match", "If-Unmodified-Since", "If-Range", "If-None-Match", "If-Modified-Since",
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var origin = request.RequestUri!;
        var current = request;
        var stripped = false;

        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, ct).ConfigureAwait(false);

            if (hop >= MaxRedirects || !IsRedirect(response.StatusCode)
                || response.Headers.Location is not { } location)
                return response;

            var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);

            // Like the built-in handler: never follow https down to http. The 3xx goes back
            // to the caller, which reports it as a failure rather than downloading in clear.
            if (target.Scheme is not ("http" or "https")
                || (current.RequestUri!.Scheme == "https" && target.Scheme == "http"))
                return response;

            response.Dispose();

            var next = new HttpRequestMessage(HttpMethod.Get, target) { Version = current.Version };
            foreach (var (name, values) in current.Headers)
                next.Headers.TryAddWithoutValidation(name, values);

            // Once off the site, stay stripped: a hop back does not restore the session.
            stripped |= !SameOrigin(origin, target);
            if (stripped)
                StripSession(next.Headers);

            if (!ReferenceEquals(current, request))
                current.Dispose();
            current = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
