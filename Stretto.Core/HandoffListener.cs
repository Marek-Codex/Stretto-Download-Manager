using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Stretto.Core;

/// <summary>Media the extension has watched pass by on a page, for the media grabber.</summary>
public sealed record MediaReport
{
    public required string Url { get; init; }
    public string? Name { get; init; }

    /// <summary>The title of the page it was playing on, when the extension found one. The
    /// app turns this into a file name when the video is downloaded; see FileNaming.</summary>
    public string? PageTitle { get; init; }
    public string? Mime { get; init; }
    public long? Size { get; init; }
    public string? Cookie { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>stream, video or audio, as classified by the extension.</summary>
    public string? Kind { get; init; }
}

/// <summary>
/// Many links at once, from the extension's "all links" and "all images" commands. Sent as
/// one request so the app can show one list to confirm, not a window per link.
/// Cookies are keyed by origin, because a page's images often live on another host.
/// </summary>
public sealed record BatchHandoff
{
    public required List<string> Urls { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public Dictionary<string, string>? Cookies { get; init; }

    /// <summary>
    /// The cookies the browser sent for this link's origin. Older extensions keyed them by
    /// bare host after reading the https:// jar, so those only ever go to an https:// link:
    /// a Secure cookie in clear text over http:// is precisely what Secure exists to prevent.
    /// </summary>
    public string? CookieFor(string url)
    {
        if (Cookies is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (Cookies.TryGetValue(uri.GetLeftPart(UriPartial.Authority), out var exact))
            return exact;

        return uri.Scheme == Uri.UriSchemeHttps ? Cookies.GetValueOrDefault(uri.Authority) : null;
    }
}

/// <summary>A download the browser extension has handed over.</summary>
public sealed record HandoffRequest
{
    public required string Url { get; init; }
    public string? FileName { get; init; }

    /// <summary>The title of the page the download came from, when the extension found one.
    /// The app chooses between this and <see cref="FileName"/>; see FileNaming.</summary>
    public string? PageTitle { get; init; }

    /// <summary>Set by the app, not the extension: the name was chosen from the page's title.</summary>
    public bool NamedFromTitle { get; init; }

    /// <summary>How the extension found the title (which tab, which version), for the log.
    /// Never shown, and never part of the name.</summary>
    public string? NameNote { get; init; }
    public string? Cookie { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public long? Size { get; init; }

    /// <summary>
    /// The Content-Type the browser saw. Worth having before the probe runs, because the
    /// case that needs it is the one where the address says nothing: an API-style download
    /// endpoint with no extension in it, where the type is known only from the header.
    /// Null when the browser did not see one.
    /// </summary>
    public string? Mime { get; init; }

    /// <summary>
    /// What the browser sent on the request this download came from, already stripped of
    /// the headers that must not be replayed. Without these the engine has Cookie, Referer
    /// and User-Agent and nothing else, which is enough for a plain link and not enough
    /// for a URL the site gates behind an Authorization header, a CSRF token or a
    /// signature. The extension only captures them for media it watched go out, so this
    /// is null for a link taken over from the downloads list.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>An HLS or DASH playlist, which ffmpeg handles instead of the engine.</summary>
    public bool IsStream { get; init; }
}

/// <summary>
/// The endpoint the browser extension talks to.
///
/// Deliberately a raw TcpListener rather than HttpListener: HttpListener needs a URL ACL
/// registration (an admin step) for anything but a handful of prefixes, and asking users to
/// run netsh to install a download manager is not acceptable. The HTTP surface here is a
/// handful of routes, so hand-rolling it is cheaper than the dependency.
///
/// It binds to loopback and checks caller origins; protected routes also require the
/// pairing token. Discovery, preflight and pairing have their own gates before a token
/// exists. Originless protected calls still need the token. Keep those cases distinct:
/// "it came from this computer" is not authentication, however homely the address looks.
/// </summary>
public sealed class HandoffListener : IDisposable
{
    public const int DefaultPort = 9615;

    private readonly Func<string> _token;
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _stopping;

    public HandoffListener(string token, int port = DefaultPort) : this(() => token, port) { }

    /// <summary>Reads the token per request, so revoking an extension can rotate it without
    /// restarting the listener.</summary>
    public HandoffListener(Func<string> token, int port = DefaultPort)
    {
        _token = token;
        _port = port;
    }

    public int Port => _port;
    public bool IsListening { get; private set; }

    /// <summary>
    /// When a paired extension last reached the app, or null if none has since it started.
    /// Lets the setup guide show the moment a freshly loaded extension connects, rather
    /// than asking the user to go and check.
    /// </summary>
    public DateTimeOffset? LastContact { get; private set; }

    /// <summary>
    /// Supplies the capture rules as JSON. The app owns them, so the extension asks for
    /// them rather than keeping its own copy that could drift out of step.
    /// </summary>
    public Func<string>? Rules { get; set; }

    /// <summary>
    /// Asked when an extension wants to pair. Receives the caller's origin
    /// (chrome-extension://id) and returns the token to hand over, or null to refuse.
    ///
    /// This is what replaces making the user copy a token by hand: the extension asks, the
    /// app decides. Only an approved extension should receive the token.
    /// </summary>
    public Func<string, Task<string?>>? PairRequested { get; set; }

    /// <summary>
    /// Asked what to do with a captured download. Returning false means the app declined it
    /// and the browser should carry on with its own download.
    ///
    /// This is why /add answers slowly when the user has asked to confirm captures. The
    /// Chromium extension has already cancelled the browser's copy by then (holding it let
    /// Brave show its own Save dialog too), so a decline is re-issued to the browser by
    /// address: cookies carry over, but a download that began as a form POST does not.
    /// Firefox holds the response instead, so a decline there resumes it untouched.
    /// </summary>
    public Func<HandoffRequest, Task<bool>>? Decide { get; set; }

    /// <summary>Raised on a background thread; handlers must marshal to the UI themselves.</summary>
    public event Action<HandoffRequest>? Received;

    /// <summary>
    /// Adds a host to the skip list (true) or takes it off (false), from the popup's
    /// "Don't capture on this site". Called on a background thread. The answer to the
    /// request is the updated rules, so the extension's cache is right straight away.
    /// </summary>
    public Action<string, bool>? SkipHost { get; set; }

    /// <summary>Decides a batch; returns how many of the links the app took.</summary>
    public Func<BatchHandoff, Task<int>>? DecideBatch { get; set; }

    /// <summary>Browser-detected media for the grabber. Same threading as <see cref="Received"/>.</summary>
    public event Action<MediaReport>? MediaSeen;
    public event Action<string>? Log;

    public void Start()
    {
        if (IsListening) return;

        _stopping = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        IsListening = true;

        _ = AcceptLoopAsync(_stopping.Token);
    }

    public void Stop()
    {
        if (!IsListening) return;

        IsListening = false;
        _stopping?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleAsync(client, ct);   // one slow client must not block the next
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log?.Invoke($"Accept failed: {exception.Message}");
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();

                // Serve requests until the peer goes away. Browsers pool connections and
                // will happily send a second request down one they believe is still open;
                // answering only the first and closing invites response/request desync.
                while (!ct.IsCancellationRequested)
                {
                    // Generous, because a request may be waiting on a person: the New
                    // Download dialog holds /add open until they choose. Loopback with a
                    // handful of clients can afford to keep an idle socket around.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromMinutes(3));

                    if (!await ServeOneAsync(stream, idle.Token, ct).ConfigureAwait(false))
                        return;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The idle timeout on a pooled connection the browser stopped using. Normal,
                // and it used to fill the log with "Request failed" every few minutes.
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                Log?.Invoke($"Request failed: {exception.Message}");
            }
        }
    }

    /// <summary>Handles a single request. Returns false when the connection should close.</summary>
    /// <param name="stream">The client's connection, kept open between requests.</param>
    /// <param name="idle">Limits only the wait for the next request to arrive.</param>
    /// <param name="ct">The listener's lifetime, for everything after. Answering can wait
    /// on a person in the New Download window, and the idle limit used to cut that off at
    /// three minutes, handing the download back to the browser mid-decision.</param>
    private async Task<bool> ServeOneAsync(NetworkStream stream, CancellationToken idle, CancellationToken ct)
    {
        var (head, payload) = await ReadRequestAsync(stream, idle).ConfigureAwait(false);
        if (head.Length == 0)
            return false;

        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return false;

        var method = parts[0];
        var path = parts[1].Split('?')[0];
        var headers = ParseHeaders(lines);

        Log?.Invoke($"<- {method} {path} (body {payload.Length}B)");

        headers.TryGetValue("origin", out var origin);
        headers.TryGetValue("connection", out var connection);

        var keepAlive = !string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase)
                        && lines[0].EndsWith("HTTP/1.1", StringComparison.Ordinal);

        // Brave's unpacked-extension Options page can omit Origin on a direct
        // loopback fetch. The pairing token is still mandatory below, so an
        // originless request is safe to admit here while ordinary web origins
        // remain rejected.
        var allowed = IsExtensionOrigin(origin) || origin is null;
        var context = new Response(origin, allowed, keepAlive);

        // Preflight has to be answered before the token is checked: browsers do not attach
        // custom headers to an OPTIONS request.
        if (method == "OPTIONS")
        {
            // Answered as 200 with a real body rather than 204. A 204 must carry no body,
            // so sending one with Content-Length/Content-Type is malformed framing: on a
            // pooled keep-alive connection that desyncs the client's parser and every
            // later request on that socket reads the previous response.
            await WriteAsync(stream, 200, "{}", context, ct).ConfigureAwait(false);
            return keepAlive;
        }

        if (!allowed)
        {
            Log?.Invoke($"Rejected request from origin '{origin ?? "(none)"}'");
            await WriteAsync(stream, 403, """{"error":"origin"}""", context, ct).ConfigureAwait(false);
            return keepAlive;
        }

        // Discovery and pairing happen before a token exists, so they run ahead of the
        // token gate. Both still demand a real extension origin: a web page cannot forge
        // one, so this is not a door onto the open web.
        if (method == "GET" && path == "/hello")
        {
            await WriteAsync(stream, 200, """{"app":"stretto","version":1}""", context, ct)
                .ConfigureAwait(false);
            return keepAlive;
        }

        if (method == "POST" && path == "/pair")
        {
            // Firefox does not always attach an Origin to an extension's own request for a
            // host it has permission for, so its extension names itself in a header. Taken
            // only when there is no Origin at all: a web page always sends one, so it cannot
            // use this to pass as an extension. The user still has to allow it once.
            if (origin is null
                && headers.TryGetValue("x-stretto-extension", out var named)
                && named.StartsWith("moz-extension://", StringComparison.Ordinal))
                origin = named;

            if (!IsExtensionOrigin(origin) || PairRequested is null)
            {
                await WriteAsync(stream, 403, """{"error":"origin"}""", context, ct).ConfigureAwait(false);
                return keepAlive;
            }

            var issued = await PairRequested(origin!).ConfigureAwait(false);

            if (issued is null)
            {
                Log?.Invoke($"Pairing refused for {origin}");
                await WriteAsync(stream, 403, """{"error":"refused"}""", context, ct).ConfigureAwait(false);
            }
            else
            {
                Log?.Invoke($"Paired with {origin}");
                LastContact = DateTimeOffset.UtcNow;
                await WriteAsync(stream, 200,
                    JsonSerializer.Serialize(new { token = issued }), context, ct).ConfigureAwait(false);
            }

            return keepAlive;
        }

        headers.TryGetValue("x-stretto-token", out var token);
        if (!FixedTimeEquals(token, _token()))
        {
            Log?.Invoke("Rejected request with a bad pairing token");
            await WriteAsync(stream, 401, """{"error":"token"}""", context, ct).ConfigureAwait(false);
            return keepAlive;
        }

        LastContact = DateTimeOffset.UtcNow;

        switch (method, path)
        {
            case ("GET", "/rules"):
                await WriteAsync(stream, 200, Rules?.Invoke() ?? "{}", context, ct).ConfigureAwait(false);
                break;

            case ("GET", "/ping"):
                await WriteAsync(stream, 200, """{"app":"stretto","version":1}""", context, ct)
                    .ConfigureAwait(false);
                break;

            case ("POST", "/add"):
                await HandleAddAsync(stream, payload, context, ct).ConfigureAwait(false);
                break;

            case ("POST", "/media"):
                await HandleMediaAsync(stream, payload, context, ct).ConfigureAwait(false);
                break;

            case ("POST", "/skip-host") when SkipHost is not null:
                try
                {
                    using var json = JsonDocument.Parse(payload);
                    var host = json.RootElement.GetProperty("host").GetString()?.Trim().ToLowerInvariant() ?? "";
                    var skip = !json.RootElement.TryGetProperty("skip", out var flag) || flag.GetBoolean();

                    // A bare host name only: this string ends up in a list matched against
                    // every download, so nothing that could match more than intended.
                    if (host.Length is 0 or > 253 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
                        throw new FormatException();

                    SkipHost(host, skip);
                    await WriteAsync(stream, 200, Rules?.Invoke() ?? "{}", context, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
                {
                    await WriteAsync(stream, 400, """{"error":"host"}""", context, ct).ConfigureAwait(false);
                }
                break;

            case ("POST", "/batch") when DecideBatch is not null:
                await HandleBatchAsync(stream, payload, context, ct).ConfigureAwait(false);
                break;

            default:
                await WriteAsync(stream, 404, """{"error":"route"}""", context, ct).ConfigureAwait(false);
                break;
        }

        return keepAlive;
    }

    /// <summary>Everything needed to frame a reply consistently with the request.</summary>
    private readonly record struct Response(string? Origin, bool AllowOrigin, bool KeepAlive);


    private async Task HandleAddAsync(NetworkStream stream, byte[] body, Response context, CancellationToken ct)
    {
        try
        {
            var request = JsonSerializer.Deserialize<HandoffRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (request is null || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                await WriteAsync(stream, 400, """{"error":"url"}""", context, ct).ConfigureAwait(false);
                return;
            }

            // How the name was chosen, in words the extension supplied, beside whether it is just
            // the address's own name. The name itself is left out of the log on purpose.
            if (!string.IsNullOrWhiteSpace(request.NameNote))
            {
                var addressName = Path.GetFileName(uri.AbsolutePath);
                var sameAsAddress = string.Equals(request.FileName, addressName, StringComparison.OrdinalIgnoreCase);
                var note = request.NameNote.Length > 200 ? request.NameNote[..200] : request.NameNote;
                Log?.Invoke($"Name: {note}; sent name is {(sameAsAddress ? "the address's own" : "different from the address's")}");
            }

            // Decide can take as long as the user does. Without it, fall back to the old
            // fire-and-forget behaviour so the endpoint still works headless.
            if (Decide is not null)
            {
                var accepted = await Decide(request).ConfigureAwait(false);

                await WriteAsync(stream, 200,
                    accepted ? """{"action":"queued"}""" : """{"action":"browser"}""",
                    context, ct).ConfigureAwait(false);

                return;
            }

            Received?.Invoke(request);
            await WriteAsync(stream, 200, """{"action":"queued","queued":true}""", context, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteAsync(stream, 400, """{"error":"json"}""", context, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleBatchAsync(NetworkStream stream, byte[] body, Response context, CancellationToken ct)
    {
        try
        {
            var batch = JsonSerializer.Deserialize<BatchHandoff>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var urls = batch?.Urls
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                .Distinct()
                .ToList() ?? [];

            if (batch is null || urls.Count == 0)
            {
                await WriteAsync(stream, 400, """{"error":"urls"}""", context, ct).ConfigureAwait(false);
                return;
            }

            var accepted = await DecideBatch!(batch with { Urls = urls }).ConfigureAwait(false);
            await WriteAsync(stream, 200, JsonSerializer.Serialize(new { accepted }), context, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteAsync(stream, 400, """{"error":"json"}""", context, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fire-and-forget media sightings. Anything malformed is dropped, never queued.</summary>
    private async Task HandleMediaAsync(NetworkStream stream, byte[] body, Response context, CancellationToken ct)
    {
        try
        {
            var report = JsonSerializer.Deserialize<MediaReport>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (report is null || !Uri.TryCreate(report.Url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                await WriteAsync(stream, 400, """{"error":"url"}""", context, ct).ConfigureAwait(false);
                return;
            }

            MediaSeen?.Invoke(report);
            await WriteAsync(stream, 200, """{"seen":true}""", context, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteAsync(stream, 400, """{"error":"json"}""", context, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads headers byte-wise, then exactly Content-Length more bytes. Decoding the
    /// head as text first would risk swallowing part of the body.</summary>
    private static async Task<(string Head, byte[] Body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        var matched = 0;

        while (matched < 4 && buffer.Length < 32 * 1024)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            buffer.WriteByte(one[0]);
            matched = one[0] switch
            {
                (byte)'\r' when matched is 0 or 2 => matched + 1,
                (byte)'\n' when matched is 1 or 3 => matched + 1,
                _ => 0,
            };
        }

        // Oversized headers or body are refused by dropping the connection. Answering and
        // carrying on would leave the unread bytes in the socket, and the next "request"
        // on this kept-alive connection would be parsed out of the middle of them.
        if (matched < 4 && buffer.Length >= 32 * 1024)
            throw new IOException("Request headers too large.");

        var head = Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r', '\n');
        var length = 0;

        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                _ = int.TryParse(line[15..].Trim(), out length);
        }

        if (length > 1024 * 1024)
            throw new IOException($"Request body of {length} bytes is over the 1 MB limit.");

        if (length <= 0)
            return (head, []);

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return (head, body);
    }

    private static Dictionary<string, string> ParseHeaders(string[] lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1))
        {
            var split = line.IndexOf(':');
            if (split > 0)
                headers[line[..split].Trim()] = line[(split + 1)..].Trim();
        }

        return headers;
    }

    /// <summary>Only an extension (or an originless loopback extension fetch) may talk to us.
    /// The pairing token remains mandatory for every non-preflight request.</summary>
    private static bool IsExtensionOrigin(string? origin) =>
        origin is not null
        && (origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
            || origin.StartsWith("moz-extension://", StringComparison.Ordinal));

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
            return false;

        var difference = 0;
        for (var i = 0; i < a.Length; i++)
            difference |= a[i] ^ b[i];

        return difference == 0;
    }

    private static async Task WriteAsync(
        NetworkStream stream, int status, string? json, Response context, CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK", 204 => "No Content", 400 => "Bad Request",
            401 => "Unauthorized", 403 => "Forbidden", _ => "Not Found",
        };

        var payload = json is null ? [] : Encoding.UTF8.GetBytes(json);
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append($"Content-Length: {payload.Length}\r\n")
            .Append($"Connection: {(context.KeepAlive ? "keep-alive" : "close")}\r\n")
            .Append("Cache-Control: no-store\r\n");

        if (context.AllowOrigin && context.Origin is not null)
        {
            head.Append($"Access-Control-Allow-Origin: {context.Origin}\r\n")
                .Append("Access-Control-Allow-Headers: content-type, x-stretto-token, x-stretto-extension\r\n")
                .Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n")
                .Append("Access-Control-Max-Age: 600\r\n");
        }

        head.Append("\r\n");

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}
