using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Stretto.Core;

/// <summary>
/// The engine. Opens N connections to one file, each asking for a different byte range,
/// and writes every response straight into the final file at its own offset.
///
/// The speed does not come from threads -- these are async socket reads, so a 16-connection
/// download occupies roughly no CPU. It comes from sidestepping per-connection server
/// throttling and from letting several TCP streams share the recovery cost of packet loss.
/// </summary>
public sealed class SegmentedDownloader(HttpClient? http = null, int maxRetriesPerSegment = 5)
{
    public const int MaxConnections = 32;
    public const long MinSegmentSize = 1L << 20;   // don't split a file into slivers
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// Smallest piece an idle worker takes over. Smaller than <see cref="MinSegmentSize"/>
    /// because by then the connection setup is already paid for, and the tail is exactly
    /// where one slow connection costs the most. Must stay well above <see cref="BufferSize"/>.
    /// </summary>
    private const long StealMinimum = 256 * 1024;

    private readonly HttpClient _http = http ?? CreateDefaultClient();
    private readonly int _maxRetries = Math.Clamp(maxRetriesPerSegment, 0, 20);
    private readonly HostRequestBudget _hostBudget = new(_ => MaxConnections);

    /// <summary>Per-download mutable counters, kept off the instance so one downloader can serve many jobs.</summary>
    private sealed class Job
    {
        public long Bytes;
        public int Active;
        public int Connections;
        public int Peak;
        public int Refused;
        public int Target;
        public int Retire;
    }

    /// <summary>Connections an adaptive download opens with, before it has measured anything.</summary>
    internal const int RampStart = 4;

    /// <summary>How long each step of the ramp is measured for. Long enough for new connections
    /// to get past TCP slow start, short enough that a ten second download still benefits.</summary>
    internal static TimeSpan RampInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A step must lift the total speed by this factor to count as worth keeping.</summary>
    private const double RampGain = 1.08;

    /// <summary>Not worth opening more connections when the rest should take less than this.</summary>
    private const double RampMinimumRemainingSeconds = 5;

    /// <summary>
    /// A speed limit needs no special case: the ramp grows only while the speed rises, so it
    /// stops by itself once the limit is what holds it.
    /// </summary>
    private static bool Ramps(DownloadRequest request) =>
        request.Adaptive && Math.Clamp(request.Connections, 1, MaxConnections) > RampStart;

    /// <summary>The number of connections to open with: the whole request, unless it will ramp.</summary>
    private static int StartingConnections(DownloadRequest request) =>
        Ramps(request) ? RampStart : Math.Clamp(request.Connections, 1, MaxConnections);

    /// <summary>
    /// Decides, once per interval, whether to open more connections. It adds a batch, then on
    /// the next look keeps going only if the total speed rose enough to pay for it. When a
    /// step does nothing the line (or the server's per-client limit) is full, and more
    /// connections would only make the site's life harder.
    /// </summary>
    private sealed class Ramp(long startBytes)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastBytes = startBytes;
        private TimeSpan _lastAt = TimeSpan.Zero;
        private double _rateBefore;
        private bool _probing;
        private int _connectionsBefore;

        public bool Done { get; private set; }
        public int? RetireTo { get; private set; }

        /// <returns>How many connections to open now.</returns>
        public int Evaluate(long bytes, int running, int ceiling, long remaining, bool refused)
        {
            var now = _clock.Elapsed;
            var seconds = (now - _lastAt).TotalSeconds;
            var rate = seconds > 0 ? (bytes - _lastBytes) / seconds : 0;
            _lastBytes = bytes;
            _lastAt = now;

            if (refused)
            {
                RetireTo = Math.Max(1, _connectionsBefore > 0 ? _connectionsBefore : running / 2);
                Done = true;
                return 0;
            }

            if (_probing)
            {
                // The last batch did not pay for itself: this is as fast as it goes.
                if (rate < _rateBefore * RampGain)
                {
                    RetireTo = _connectionsBefore;
                    Done = true;
                    return 0;
                }
                _probing = false;
            }

            if (running >= ceiling)
            {
                Done = true;
                return 0;
            }

            if (rate > 0 && remaining / rate < RampMinimumRemainingSeconds)
            {
                Done = true;
                return 0;
            }

            _rateBefore = rate;
            _connectionsBefore = running;
            _probing = true;
            return Math.Min(ceiling - running, Math.Max(2, running / 2));
        }

        public void Stop() => Done = true;
    }

    /// <summary>
    /// Builds the client every download shares. <paramref name="proxy"/> null means the
    /// system proxy, which is what a browser would do; pass <see cref="NoProxy"/> to go
    /// direct. Optional credentials answer server challenges; callers should scope those
    /// credentials to the intended origin.
    /// </summary>
    public static HttpClient CreateDefaultClient(
        int connectTimeoutSeconds = 30,
        IWebProxy? proxy = null,
        bool useWindowsCredentials = false,
        ICredentials? credentials = null,
        Socks5ProxyOptions? socks5Proxy = null)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = socks5Proxy is null ? proxy : null,
            UseProxy = socks5Proxy is null && proxy is not NoWebProxy,
            // Range offsets describe the transferred representation. Automatic decompression
            // would change those bytes underneath us. Same numbers, different furniture. No.
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = MaxConnections * 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(connectTimeoutSeconds, 5, 300)),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            PreAuthenticate = credentials is not null || useWindowsCredentials,
            ConnectCallback = socks5Proxy is null ? null : (context, cancellationToken) =>
                ConnectThroughSocks5Async(socks5Proxy, context.DnsEndPoint, cancellationToken),
        };

        if (credentials is not null)
            handler.Credentials = credentials;
        else if (useWindowsCredentials)
            handler.Credentials = CredentialCache.DefaultNetworkCredentials;

        if (useWindowsCredentials)
        {
            // Covers NTLM, Kerberos and a proxy that wants the same, which is what an
            // intranet or a corporate proxy asks for. Off by default: sending the signed in
            // account's credentials to arbitrary internet hosts is not a sane default.
            handler.DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials;
            handler.PreAuthenticate = true;
        }

        // Overall timeouts are the caller's job via CancellationToken; a long download
        // must not trip the HttpClient stopwatch.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectThroughSocks5Async(
        Socks5ProxyOptions proxy, DnsEndPoint destination, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxy.Host, proxy.Port, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            var hasCredentials = !string.IsNullOrEmpty(proxy.Username);
            await stream.WriteAsync(hasCredentials
                ? new byte[] { 5, 2, 0, 2 }
                : new byte[] { 5, 1, 0 }, cancellationToken).ConfigureAwait(false);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
            if (greeting[0] != 5 || greeting[1] == 0xFF)
                throw new IOException("The SOCKS5 proxy rejected the offered authentication methods.");

            if (greeting[1] == 2)
            {
                if (!hasCredentials)
                    throw new IOException("The SOCKS5 proxy requires a user name and password.");

                var username = Encoding.UTF8.GetBytes(proxy.Username!);
                var password = Encoding.UTF8.GetBytes(proxy.Password ?? "");
                if (username.Length is 0 or > 255 || password.Length > 255)
                    throw new IOException("SOCKS5 user names and passwords must fit within 255 UTF-8 bytes.");

                var auth = new byte[3 + username.Length + password.Length];
                auth[0] = 1;
                auth[1] = (byte)username.Length;
                username.CopyTo(auth, 2);
                auth[2 + username.Length] = (byte)password.Length;
                password.CopyTo(auth, 3 + username.Length);
                await stream.WriteAsync(auth, cancellationToken).ConfigureAwait(false);

                var authReply = new byte[2];
                await stream.ReadExactlyAsync(authReply, cancellationToken).ConfigureAwait(false);
                if (authReply[0] != 1 || authReply[1] != 0)
                    throw new IOException("SOCKS5 proxy authentication failed.");
            }
            else if (greeting[1] != 0)
            {
                throw new IOException("The SOCKS5 proxy selected an unsupported authentication method.");
            }

            var address = EncodeSocksAddress(destination.Host);
            var request = new byte[3 + address.Length + 2];
            request[0] = 5;
            request[1] = 1; // CONNECT
            request[2] = 0;
            address.CopyTo(request, 3);
            request[^2] = (byte)(destination.Port >> 8);
            request[^1] = (byte)destination.Port;
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);
            if (reply[0] != 5 || reply[1] != 0)
                throw new IOException($"SOCKS5 could not connect to the download host (reply {reply[1]}).");

            var boundAddressLength = reply[3] switch
            {
                1 => 4,
                4 => 16,
                3 => (await ReadLengthAsync(stream, cancellationToken).ConfigureAwait(false)),
                _ => throw new IOException("The SOCKS5 proxy returned an invalid address type."),
            };
            var remainder = new byte[boundAddressLength + 2]; // bound address and port
            await stream.ReadExactlyAsync(remainder, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async ValueTask<int> ReadLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        return length[0];
    }

    private static byte[] EncodeSocksAddress(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
            return [ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4, .. ip.GetAddressBytes()];

        var domain = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(host));
        if (domain.Length is 0 or > 255)
            throw new IOException("The download host name cannot be represented in a SOCKS5 request.");

        return [3, (byte)domain.Length, .. domain];
    }

    /// <summary>Marker for "go direct", which is not the same as "use the system proxy".</summary>
    public static IWebProxy NoProxy { get; } = new NoWebProxy();

    private sealed class NoWebProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => null;
        public bool IsBypassed(Uri host) => true;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default,
        RemoteFileInfo? probedInfo = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var info = probedInfo ?? await HttpProbe.ProbeAsync(_http, request, ct).ConfigureAwait(false);

        Directory.CreateDirectory(request.TargetDirectory);
        var path = ResolvePath(request, info.SuggestedFileName, info);

        if (!info.CanSegment)
        {
            // No Content-Length, or the server ignored Range. One stream is all we get.
            // When the App passed probe metadata this avoids asking the server twice.
            var written = await DownloadSingleStreamAsync(request, info, path, progress, ct)
                .ConfigureAwait(false);

            return new DownloadResult
            {
                FilePath = path,
                BytesWritten = written,
                Elapsed = stopwatch.Elapsed,
                ConnectionsUsed = 1,
                WasResumed = false,
            };
        }

        var total = info.TotalLength!.Value;
        var existing = DownloadState.TryLoad(path, info);
        var resumed = existing is not null;

        var segments = existing?.ToSegments() ?? PlanSegments(total, StartingConnections(request));
        var state = existing ?? new DownloadState
        {
            Url = info.FinalUrl.ToString(),
            TotalLength = total,
            Validator = info.Validator,
        };

        var job = new Job { Bytes = segments.Sum(s => s.Completed) };

        // Ask the filesystem to reserve space up front where supported. This can expose
        // a full disk early; it does not promise one contiguous extent or every future write.
        // The disk is still allowed to have a personality. Unfortunately.
        using var handle = resumed
            ? File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.Read,
                FileOptions.Asynchronous)
            : File.OpenHandle(path, request.Overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                FileOptions.Asynchronous, preallocationSize: total);

        request.OutputFileOpened?.Invoke(path);

        using var reporting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reporter = ReportProgressAsync(job, total, path, state, segments, progress, reporting.Token);

        try
        {
            await RunWorkersAsync(request, info, segments, handle, job, ct).ConfigureAwait(false);
        }
        catch
        {
            state.UpdateFrom(Snapshot(segments));
            state.Save(path);   // keep the resume point before surfacing the failure
            throw;
        }
        finally
        {
            reporting.Cancel();
            await reporter.ConfigureAwait(false);
        }

        DownloadState.Delete(path);
        ApplyTimestamp(request, info, path);

        return new DownloadResult
        {
            FilePath = path,
            BytesWritten = total,
            Elapsed = stopwatch.Elapsed,
            ConnectionsUsed = segments.Count,
            WasResumed = resumed,
            PeakConnections = Volatile.Read(ref job.Peak),
            ExtraConnectionsRefused = Volatile.Read(ref job.Refused) != 0,
        };
    }

    /// <summary>
    /// Stamps the saved file with the server's own date, so a downloaded file sorts by when
    /// it was published rather than by when it happened to be fetched.
    /// </summary>
    private static void ApplyTimestamp(DownloadRequest request, RemoteFileInfo info, string path)
    {
        if (!request.UseServerTimestamp || info.LastModified is not { } stamp)
            return;

        try
        {
            File.SetLastWriteTimeUtc(path, stamp.UtcDateTime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cosmetic: the bytes arrived. A refused timestamp does not get to un-finish them.
        }
    }

    /// <summary>
    /// Picks the file to write. When the caller asked not to overwrite, an existing file
    /// pushes the download to "name (2).ext" -- unless a resume sidecar sits beside it, which
    /// means those bytes are this download's own work in progress rather than someone else's file.
    /// </summary>
    private static string ResolvePath(DownloadRequest request, string fileName, RemoteFileInfo info) =>
        // A sidecar only earns the original path if it is a usable resume point for this
        // exact remote file. A stale sidecar beside an unrelated file must not turn the
        // no-overwrite path into an overwrite. Familiar filename, entirely wrong guest.
        FilePaths.ClashFree(request.TargetDirectory, fileName, request.Overwrite,
            path => DownloadState.TryLoad(path, info) is not null,
            request.TagClashes ? SourceTag.For(request.Url) : null);

    /// <summary>Even split, but never into pieces so small that connection setup dominates.</summary>
    public static List<Segment> PlanSegments(long total, int requested)
    {
        var count = Math.Clamp(requested, 1, MaxConnections);
        count = (int)Math.Max(1, Math.Min(count, total / MinSegmentSize));

        var size = total / count;
        var segments = new List<Segment>(count);

        for (var i = 0; i < count; i++)
        {
            var start = i * size;
            var end = i == count - 1 ? total - 1 : start + size - 1;
            segments.Add(new Segment { Start = start, End = end });
        }

        return segments;
    }

    /// <summary>
    /// How long a connection may go without delivering a byte before it is dropped and
    /// retried. Without this a socket that stalls without closing hangs the download
    /// forever -- the classic "stuck at 99%".
    /// </summary>
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the workers, and whenever one finishes, hands it the back half of the largest
    /// range still in flight. Without this the last, slowest connection finishes the file
    /// alone while the others sit idle, which is where most of a download's tail time goes.
    /// The same step tops up connections on a resume where most segments were already done.
    /// </summary>
    private async Task RunWorkersAsync(
        DownloadRequest request, RemoteFileInfo info, List<Segment> segments,
        SafeFileHandle handle, Job job, CancellationToken ct)
    {
        var ceiling = Math.Clamp(request.Connections, 1, MaxConnections);
        var wanted = StartingConnections(request);
        job.Target = wanted;
        var running = new List<Task>();
        var budget = request.HostBudget ?? _hostBudget;

        // Saved splits are bookkeeping, not permission slips for extra connections.
        // Queue every unfinished range; the current limit decides how many workers get chairs.
        foreach (var segment in segments.Where(s => !s.IsDone)) segment.Orphaned = true;

        while (running.Count < wanted && (TakeOrphan(segments) ?? SplitLargest(segments)) is { } extra)
            running.Add(RunSegmentAsync(request, info, extra, handle, job, ct));

        // Only a download that can grow has anything to measure.
        var ramp = Ramps(request) ? new Ramp(Interlocked.Read(ref job.Bytes)) : null;
        var tick = ramp is null ? null : Task.Delay(RampInterval, ct);

        Exception? failure = null;

        while (running.Count > 0)
        {
            Task finished;
            if (tick is null)
            {
                finished = await Task.WhenAny(running).ConfigureAwait(false);
            }
            else
            {
                var waiting = new List<Task>(running.Count + 1) { tick };
                waiting.AddRange(running);
                finished = await Task.WhenAny(waiting).ConfigureAwait(false);
            }

            if (ReferenceEquals(finished, tick))
            {
                tick = null;
                if (failure is not null || ct.IsCancellationRequested || ramp is null)
                    continue;

                var add = ramp.Evaluate(
                    Interlocked.Read(ref job.Bytes), running.Count, Math.Min(ceiling, running.Count + budget.AvailableSlots(info.FinalUrl)),
                    Snapshot(segments).Sum(s => Math.Max(0, s.Remaining)),
                    Volatile.Read(ref job.Refused) != 0);

                if (ramp.RetireTo is { } lower)
                {
                    job.Target = Math.Max(1, Math.Min(lower, budget.ConnectionLimit(info.FinalUrl)));
                    Interlocked.Exchange(ref job.Retire, Math.Max(0, Volatile.Read(ref job.Active) - job.Target));
                }
                else job.Target = Math.Min(ceiling, running.Count + add);

                for (var i = 0; i < add; i++)
                {
                    var more = TakeOrphan(segments) ?? SplitLargest(segments);
                    if (more is null)
                    {
                        ramp.Stop();   // nothing left worth splitting
                        break;
                    }

                    running.Add(RunExtraAsync(request, info, more, handle, job, ct));
                }

                if (!ramp.Done)
                    tick = Task.Delay(RampInterval, ct);
                continue;
            }

            running.Remove(finished);

            if (finished.Exception is { } exception)
                failure ??= exception.InnerException ?? exception;
            else if (finished.IsCanceled)
                failure ??= new OperationCanceledException(ct);

            // A connection that was turned away has not freed a slot; the server just showed
            // it has none to give, so its range waits for a worker that did finish. Only when
            // nothing else is running is it started at once, so it cannot be left behind.
            var turnedAway = finished is Task<bool> { IsCompletedSuccessfully: true } extra && !extra.Result;

            // No new work once something has failed or the user paused: the remaining
            // workers are left to finish or fail on their own, as before. A range an added
            // connection was turned away from goes first.
            if (failure is null && !ct.IsCancellationRequested && running.Count < job.Target
                && (!turnedAway || running.Count == 0)
                && (TakeOrphan(segments) ?? SplitLargest(segments)) is { } stolen)
                running.Add(RunSegmentAsync(request, info, stolen, handle, job, ct));
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// A connection opened mid-download, which is allowed to be refused. Turned away before
    /// it delivers anything, it hands its range back and the ramp stops growing; it never
    /// fails the download, because the connections already running are doing fine.
    /// </summary>
    /// <returns>False when the connection was turned away and its range handed back.</returns>
    private async Task<bool> RunExtraAsync(
        DownloadRequest request, RemoteFileInfo info, Segment segment,
        SafeFileHandle handle, Job job, CancellationToken ct)
    {
        try
        {
            await RunSegmentAsync(request, info, segment, handle, job, ct, retries: 1).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && segment.Completed == 0)
        {
            segment.Orphaned = true;
            Volatile.Write(ref job.Refused, 1);
            return false;
        }
    }

    /// <summary>Claims a range an added connection gave back, or null when there is none.</summary>
    private static Segment? TakeOrphan(List<Segment> segments)
    {
        lock (segments)
        {
            foreach (var s in segments)
            {
                if (s.Orphaned && !s.IsDone)
                {
                    s.Orphaned = false;
                    return s;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Cuts the range with the most left to fetch in two and returns the new back half.
    ///
    /// The cut is at least <see cref="StealMinimum"/> ahead of the owner's write position,
    /// two buffers' worth, so the owner can never have a write in flight past it:
    /// it re-reads End before every write and simply stops early.
    /// </summary>
    private static Segment? SplitLargest(List<Segment> segments)
    {
        lock (segments)
        {
            Segment? largest = null;
            foreach (var s in segments)
            {
                // An orphaned range has no worker to move out of the way, and cutting it
                // would leave its front half with nobody.
                if (!s.IsDone && !s.Orphaned && (largest is null || s.Remaining > largest.Remaining))
                    largest = s;
            }

            if (largest is null || largest.Remaining < 2 * StealMinimum)
                return null;

            var cut = largest.CurrentOffset + (largest.Remaining / 2);
            var back = new Segment { Start = cut, End = largest.End };
            largest.End = cut - 1;
            segments.Add(back);
            return back;
        }
    }

    /// <summary>A stable copy for saving, since a split may add to the list meanwhile.</summary>
    private static Segment[] Snapshot(List<Segment> segments)
    {
        lock (segments)
            return [.. segments];
    }

    private async Task RunSegmentAsync(
        DownloadRequest request, RemoteFileInfo info, Segment segment,
        SafeFileHandle handle, Job job, CancellationToken ct, int? retries = null)
    {
        // Yield first, so starting a worker never runs its connection setup inline on the
        // caller's thread.
        await Task.Yield();

        var limit = retries ?? _maxRetries;
        var attempt = 0;
        Interlocked.Increment(ref job.Active);

        try
        {
            while (!segment.IsDone)
            {
                ct.ThrowIfCancellationRequested();

                var before = segment.Completed;

                try
                {
                    if (!await PumpSegmentAsync(request, info, segment, handle, job, ct).ConfigureAwait(false))
                        return; // retired cleanly; scheduler retains its remaining bytes

                    // A connection that closed early without delivering anything is a
                    // failure, not a success: resetting the count here would let a server
                    // that answers with empty bodies spin this loop forever.
                    if (segment.Completed > before)
                        attempt = 0;
                    else if (!segment.IsDone)
                        throw new IOException("The server closed the connection without sending any data.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                                           && !RetryPolicy.IsPermanent(ex) && attempt < limit)
                {
                    // Keep committed bytes and retry from CurrentOffset. An unfinished read
                    // may need fetching again; the whole file does not. Small mercy, nyaa~
                    if (segment.Completed > before)
                        attempt = 0;
                    attempt++;
                    var backoff = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
                    await RetryPolicy.WaitAsync(RetryPolicy.DelayFor(ex, backoff), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref job.Active);
        }
    }

    private static void RaisePeak(Job job, int open)
    {
        int seen;
        while ((seen = Volatile.Read(ref job.Peak)) < open
               && Interlocked.CompareExchange(ref job.Peak, open, seen) != seen)
        {
        }
    }

    private static bool TryRetire(Job job)
    {
        if (Volatile.Read(ref job.Active) <= Volatile.Read(ref job.Target)) return false;
        int retire;
        while ((retire = Volatile.Read(ref job.Retire)) > 0)
            if (Interlocked.CompareExchange(ref job.Retire, retire - 1, retire) == retire) return true;
        return false;
    }

    private sealed class ConnectionActivity : IDisposable
    {
        private readonly Job _job;
        public ConnectionActivity(Job job)
        {
            _job = job;
            RaisePeak(job, Interlocked.Increment(ref job.Connections));
        }
        public void Dispose() => Interlocked.Decrement(ref _job.Connections);
    }

    private async Task<bool> PumpSegmentAsync(
        DownloadRequest request, RemoteFileInfo info, Segment segment,
        SafeFileHandle handle, Job job, CancellationToken ct)
    {
        var budget = request.HostBudget ?? _hostBudget;
        using var lease = await budget.AcquireAsync(info.FinalUrl, ct).ConfigureAwait(false);
        using var activity = new ConnectionActivity(job);
        // One timer per connection, pushed back on every byte. Firing turns into an
        // IOException, which the retry loop treats like any dropped connection.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        try
        {
            var requestedStart = segment.CurrentOffset;
            var requestedEnd = segment.End;
            using var message = HttpProbe.BuildRequest(request);
            message.RequestUri = info.FinalUrl;
            message.Headers.Range = new RangeHeaderValue(requestedStart, requestedEnd);

            using var response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);

            budget.Observe(response, info.FinalUrl);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                Volatile.Write(ref job.Refused, 1);
            RetryPolicy.EnsureSuccess(response);

            // A 200 here means the server decided to send the whole file down a segment
            // connection. Writing that at this segment offset would corrupt the output.
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new IOException($"Expected 206 for range request, got {(int)response.StatusCode}.");

            // A 206 alone is not enough: broken caches and servers sometimes return a
            // different slice. Writing it at the requested offset silently corrupts the
            // completed file, so reject any range that does not describe this response.
            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange is null
                || !contentRange.HasRange
                || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                || contentRange.From != requestedStart
                || contentRange.To is not { } responseEnd
                || responseEnd < requestedStart
                || responseEnd > requestedEnd
                || contentRange.Length != info.TotalLength)
            {
                throw new IOException("The server returned a mismatched Content-Range for the requested bytes.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            var responseRemaining = responseEnd - requestedStart + 1;

            try
            {
                while (!segment.IsDone && responseRemaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryRetire(job))
                    {
                        segment.Orphaned = true;
                        return false;
                    }
                    stall.CancelAfter(StallTimeout);

                    var cap = (int)Math.Min(BufferSize, Math.Min(segment.Remaining, responseRemaining));
                    var read = await stream.ReadAsync(buffer.AsMemory(0, cap), stall.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;   // connection ended early; the retry loop picks up where we stopped

                    // Paused while we throttle and write: a low speed limit can hold a
                    // worker far longer than the stall timeout, and that is not a stall.
                    // We asked it to slow down. Let's not file a complaint when it obeys.
                    stall.CancelAfter(Timeout.InfiniteTimeSpan);

                    if (request.BandwidthLimiter is { } limiter)
                        await limiter.ThrottleAsync(read, ct).ConfigureAwait(false);

                    // End may have moved in while this read was in flight, when an idle
                    // worker took over the back half. Anything past it is theirs now.
                    var take = (int)Math.Min(read, segment.Remaining);
                    if (take <= 0)
                        break;

                    await RandomAccess
                        .WriteAsync(handle, buffer.AsMemory(0, take), segment.CurrentOffset, ct)
                        .ConfigureAwait(false);

                    segment.Completed += take;
                    Interlocked.Add(ref job.Bytes, take);
                    responseRemaining -= take;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new IOException($"No data for {StallTimeout.TotalSeconds:0} seconds; reconnecting.");
        }
        return true;
    }

    private async Task<long> DownloadSingleStreamAsync(
        DownloadRequest request, RemoteFileInfo info, string path,
        IProgress<ProgressSnapshot>? progress, CancellationToken ct)
    {
        var job = new Job { Active = 1, Connections = 1 };
        var budget = request.HostBudget ?? _hostBudget;
        using var lease = await budget.AcquireAsync(info.FinalUrl, ct).ConfigureAwait(false);

        using var message = HttpProbe.BuildRequest(request);
        message.RequestUri = info.FinalUrl;

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        budget.Observe(response, info.FinalUrl);
        RetryPolicy.EnsureSuccess(response);

        using var reporting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reporter = ReportProgressAsync(job, info.TotalLength, path, null, null, progress, reporting.Token);

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // Unbuffered: writes already arrive in BufferSize chunks, so a FileStream buffer
            // of the same size would only copy every byte a second time.
            await using var target = new FileStream(path, request.Overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, bufferSize: 0, useAsync: true);
            request.OutputFileOpened?.Invoke(path);

            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), stall.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    stall.CancelAfter(Timeout.InfiniteTimeSpan);

                    if (request.BandwidthLimiter is { } limiter)
                        await limiter.ThrottleAsync(read, ct).ConfigureAwait(false);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    Interlocked.Add(ref job.Bytes, read);
                }
            }
            catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // One stream cannot resume, so a stall here is a failure the app can retry.
                throw new IOException($"No data for {StallTimeout.TotalSeconds:0} seconds.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            reporting.Cancel();
            await reporter.ConfigureAwait(false);
        }

        return Interlocked.Read(ref job.Bytes);
    }

    private static async Task ReportProgressAsync(
        Job job, long? total, string path, DownloadState? state,
        List<Segment>? segments, IProgress<ProgressSnapshot>? progress,
        CancellationToken ct)
    {
        if (progress is null && state is null)
            return;

        var interval = TimeSpan.FromMilliseconds(250);
        var lastBytes = Interlocked.Read(ref job.Bytes);
        var lastTick = Stopwatch.GetTimestamp();
        var speed = 0d;
        var sinceSave = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var bytes = Interlocked.Read(ref job.Bytes);
            var elapsed = Stopwatch.GetElapsedTime(lastTick);
            lastTick = Stopwatch.GetTimestamp();

            // Exponential moving average: raw per-tick deltas jitter far too much to display.
            var instant = elapsed.TotalSeconds > 0 ? (bytes - lastBytes) / elapsed.TotalSeconds : 0;
            speed = speed == 0 ? instant : (speed * 0.7) + (instant * 0.3);
            lastBytes = bytes;

            progress?.Report(new ProgressSnapshot
            {
                BytesCompleted = bytes,
                TotalBytes = total,
                BytesPerSecond = speed,
                ActiveConnections = Volatile.Read(ref job.Connections),
            });

            sinceSave += elapsed;
            if (state is not null && segments is not null && sinceSave >= TimeSpan.FromSeconds(1))
            {
                sinceSave = TimeSpan.Zero;
                state.UpdateFrom(Snapshot(segments));
                try { state.Save(path); } catch (IOException) { /* resume is best effort */ }
            }
        }
    }
}
