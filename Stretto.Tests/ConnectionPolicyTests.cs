using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using Stretto.Core;

namespace Stretto.Tests;

public static partial class Program
{
    private static async Task RunConnectionPolicyTests(Runner runner)
    {
        await runner.Run("shares a host budget across independent download clients", SharedHostConnections);
        await runner.Run("resumes many saved ranges within the new connection limit", BoundedResume);
        await runner.Run("honors 429 and 503 Retry-After without losing bytes", ServerRetryDelays);
        await runner.Run("does not retry a permanently refused segment", PermanentSegmentFailure);
        await runner.Run("retires unhelpful connections and preserves their remaining ranges", RetiresPlateauConnections);
        await runner.Run("cancels host waits without leaking a connection slot", CancelsHostWait);
        await runner.Run("reads dated Retry-After and classifies retryable status codes", DatedRetryAfter);
        await runner.Run("spreads retries and caps their wait", SpreadsRetries);
        await runner.Run("earns connections back gradually after a cooldown", RecoversGradually);
        await runner.Run("wakes a queued host request when capacity recovers", WakesRecoveredHostRequest);
        await runner.Run("re-checks a settled download politely, and never after a refusal", RevisitsSettledRamp);
        await runner.Run("revisits a plateau mid-download without disturbing the file", RevisitsDuringDownload);
        await runner.Run("refuses a changed entity before writing its ranges", RejectsChangedEntity);
        await runner.Run("does not treat weak entity tags as byte identity", IgnoresWeakEntityTags);
        await runner.Run("waits for evidence before growing and confirms marginal throughput", AdaptiveMeasurementEvidence);
        await runner.Run("cancels sibling reads promptly after a permanent failure", CancelsFailedDownloadSiblings);
        await runner.Run("does not hide an entity change as an optional connection refusal", RejectsEntityChangeOnExtraConnection);
    }

    private sealed class PolicyServer(long size) : HttpMessageHandler
    {
        public int Active;
        public int Peak;
        public int SegmentRequests;
        public int HeaderDelayMs = 25;
        public HttpStatusCode? FailFirstSegment;
        public bool AlwaysFail;
        public bool ChangeEntity;
        public bool IgnorePreconditions;
        public bool WeakTag;
        public bool SawEntityCondition;
        public int ChangeAfterSegment = int.MaxValue;
        public ConcurrentQueue<DateTimeOffset> Attempts { get; } = new();
        public byte[] Pattern { get; } = Enumerable.Range(0, checked((int)size)).Select(i => (byte)(i % 251)).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var end = request.Headers.Range?.Ranges.Single().To ?? size - 1;
            var isProbe = from == 0 && end == 0;
            var attempt = isProbe ? 0 : Interlocked.Increment(ref SegmentRequests);
            var changed = !isProbe && (ChangeEntity || attempt > ChangeAfterSegment);
            if (!isProbe && request.Headers.IfMatch.Count > 0) SawEntityCondition = true;
            if (changed && !IgnorePreconditions && request.Headers.IfMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed) { RequestMessage = request, Content = new ByteArrayContent([]) };
            }
            if (!isProbe)
            {
                Attempts.Enqueue(DateTimeOffset.UtcNow);
                if (FailFirstSegment is { } failure && (attempt == 1 || AlwaysFail))
                {
                    var refused = new HttpResponseMessage(failure) { RequestMessage = request, Content = new ByteArrayContent([]) };
                    refused.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return refused;
                }
            }
            var active = Interlocked.Increment(ref Active);
            int prior;
            while ((prior = Volatile.Read(ref Peak)) < active && Interlocked.CompareExchange(ref Peak, active, prior) != prior) { }
            try { await Task.Delay(HeaderDelayMs, ct); }
            catch { Interlocked.Decrement(ref Active); throw; }
            var content = new CountedContent(Pattern, (int)from, checked((int)(end - from + 1)), () => Interlocked.Decrement(ref Active));
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, end, size);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request, Content = content };
            response.Headers.ETag = new EntityTagHeaderValue(changed ? "\"changed\"" : "\"policy-test\"", WeakTag);
            return response;
        }
    }

    private sealed class CountedContent(byte[] bytes, int offset, int count, Action close) : ByteArrayContent(bytes, offset, count)
    {
        private int _closed;
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) close();
            base.Dispose(disposing);
        }
    }

    private static DownloadRequest PolicyRequest(string name, string directory, HostRequestBudget? budget = null, int connections = 4) => new()
    {
        Url = new Uri("https://same-host.test/" + name), TargetDirectory = directory, HostBudget = budget, Connections = connections,
    };

    private static async Task SharedHostConnections()
    {
        const long size = 4 * 1024 * 1024;
        using var folder = new TempFolder();
        var server = new PolicyServer(size);
        using var firstHttp = new HttpClient(server, disposeHandler: false);
        using var secondHttp = new HttpClient(server, disposeHandler: false);
        var budget = new HostRequestBudget(_ => 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var results = await Task.WhenAll(
            new SegmentedDownloader(firstHttp).DownloadAsync(PolicyRequest("one.bin", folder.Path, budget), ct: timeout.Token),
            new SegmentedDownloader(secondHttp).DownloadAsync(PolicyRequest("two.bin", folder.Path, budget), ct: timeout.Token));
        foreach (var result in results) VerifyPattern(result.FilePath, size);
        Assert.True(server.Peak <= 2, $"combined host requests never exceed two (peak {server.Peak})");
        Assert.Equal(0, server.Active, "all leases and response bodies released");
    }

    private static async Task BoundedResume()
    {
        const long size = 8 * 1024 * 1024;
        using var folder = new TempFolder();
        using var http = new HttpClient(new PolicyServer(size));
        var request = PolicyRequest("resume.bin", folder.Path, connections: 2);
        var info = await HttpProbe.ProbeAsync(http, request, CancellationToken.None);
        var path = Path.Combine(folder.Path, "resume.bin");
        File.WriteAllBytes(path, new byte[size]);
        var segments = SegmentedDownloader.PlanSegments(size, 8);
        new DownloadState { Url = info.FinalUrl.ToString(), Validator = info.Validator, TotalLength = size,
            Segments = segments.Select(s => new DownloadState.SegmentState { Start = s.Start, End = s.End, Completed = 0 }).ToList() }.Save(path);
        var result = await new SegmentedDownloader(http).DownloadAsync(request, probedInfo: info);
        Assert.True(result.WasResumed, "saved ranges reused");
        Assert.True(result.PeakConnections <= 2, $"resume respects two-worker setting (peak {result.PeakConnections})");
        VerifyPattern(result.FilePath, size);
    }

    private static Task RevisitsSettledRamp()
    {
        const long remaining = 1L << 40;   // hours to go at these rates
        static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

        var ramp = new SegmentedDownloader.Ramp(0);
        Assert.Equal(2, ramp.EvaluateAt(At(2), 2000, 4, 16, remaining, false), "first step");
        ramp.EvaluateAt(At(4), 3000, 6, 16, remaining, false);        // 500/s, well below 1000/s
        Assert.True(ramp.Done && ramp.RetireTo == 4, "a step that slowed things down is undone");
        Assert.Equal(SegmentedDownloader.RevisitInterval, ramp.RevisitAfter, "and checked again later");

        ramp.RevisitAt(At(200), 100_000);
        Assert.True(!ramp.Done && ramp.RetireTo is null, "a revisit reopens the ramp");
        Assert.Equal(1, ramp.EvaluateAt(At(202), 102_000, 4, 16, remaining, false), "a revisit tries one connection, not a batch");
        ramp.EvaluateAt(At(204), 104_000, 5, 16, remaining, false);   // no gain
        Assert.True(!ramp.Done, "a flat result is confirmed once before it counts");
        ramp.EvaluateAt(At(206), 106_000, 5, 16, remaining, false);
        Assert.Equal(4, ramp.RetireTo, "an unhelpful extra is retired again");
        Assert.Equal(SegmentedDownloader.RevisitInterval * 2, ramp.RevisitAfter, "and the next knock waits twice as long");

        ramp.RevisitAt(At(600), 200_000);
        ramp.EvaluateAt(At(602), 202_000, 4, 16, remaining, true);
        Assert.True(ramp.Done && ramp.RevisitAfter is null, "a refusal is final for this download");

        var nearlyDone = new SegmentedDownloader.Ramp(0);
        nearlyDone.EvaluateAt(At(2), 2000, 16, 16, 30_000, false);
        Assert.True(nearlyDone.Done && nearlyDone.RevisitAfter is null, "no revisits with under a minute to go");
        return Task.CompletedTask;
    }

    private static Task SpreadsRetries()
    {
        var first = Enumerable.Range(0, 200).Select(_ => RetryPolicy.Backoff(1)).ToList();
        Assert.True(first.All(d => d >= TimeSpan.FromMilliseconds(250) && d <= TimeSpan.FromMilliseconds(500)), "first wait stays in its band");
        Assert.True(first.Distinct().Count() > 50, "simultaneous failures do not retry in lockstep");
        Assert.True(RetryPolicy.Backoff(40) <= TimeSpan.FromSeconds(30), "long outages wait at most thirty seconds per try");
        return Task.CompletedTask;
    }

    private static async Task RecoversGradually()
    {
        var (cooldown, step) = (HostRequestBudget.Cooldown, HostRequestBudget.RecoveryStep);
        HostRequestBudget.Cooldown = TimeSpan.FromMilliseconds(200);
        HostRequestBudget.RecoveryStep = TimeSpan.FromSeconds(30);
        try
        {
            var host = new Uri("https://slow.example.test/file");
            var budget = new HostRequestBudget(_ => 8);
            var leases = new List<IDisposable>();
            for (var i = 0; i < 4; i++) leases.Add(await budget.AcquireAsync(host, CancellationToken.None));

            budget.Observe(new HttpResponseMessage(HttpStatusCode.TooManyRequests), host);
            Assert.Equal(3, budget.ConnectionLimit(host), "a 429 lowers the ceiling below what was running");

            await Task.Delay(300);
            Assert.Equal(4, budget.ConnectionLimit(host), "after the cooldown one seat comes back, not all of them");
            Assert.Equal(4, budget.ConnectionLimit(host), "and the next waits for its own step");
            leases.ForEach(l => l.Dispose());
        }
        finally
        {
            (HostRequestBudget.Cooldown, HostRequestBudget.RecoveryStep) = (cooldown, step);
        }
    }

    private static async Task WakesRecoveredHostRequest()
    {
        var (cooldown, step) = (HostRequestBudget.Cooldown, HostRequestBudget.RecoveryStep);
        HostRequestBudget.Cooldown = TimeSpan.FromMilliseconds(100);
        HostRequestBudget.RecoveryStep = TimeSpan.FromMilliseconds(100);
        var leases = new List<IDisposable>();
        try
        {
            var host = new Uri("https://recovery.example.test/file");
            var budget = new HostRequestBudget(_ => 4);
            for (var i = 0; i < 3; i++) leases.Add(await budget.AcquireAsync(host, CancellationToken.None));
            using var refused = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            refused.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            budget.Observe(refused, host);
            leases[0].Dispose(); leases.RemoveAt(0);
            // Keep both active slots held. No release, polling or settings change wakes this.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var recovered = await budget.AcquireAsync(host, timeout.Token);
            Assert.Equal(2, leases.Count, "the original responses are still holding their slots");
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            (HostRequestBudget.Cooldown, HostRequestBudget.RecoveryStep) = (cooldown, step);
        }
    }

    private static async Task ServerRetryDelays()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            using var folder = new TempFolder();
            var server = new PolicyServer(1024 * 1024) { FailFirstSegment = status };
            using var http = new HttpClient(server);
            var budget = new HostRequestBudget(_ => 4);
            var result = await new SegmentedDownloader(http).DownloadAsync(PolicyRequest("retry.bin", folder.Path, budget, 1));
            var attempts = server.Attempts.ToArray();
            Assert.Equal(2, attempts.Length, "one refused attempt then a successful retry");
            Assert.True(attempts[1] - attempts[0] >= TimeSpan.FromMilliseconds(950), status + " delay is honored");
            VerifyPattern(result.FilePath, 1024 * 1024);
        }
    }

    private static async Task PermanentSegmentFailure()
    {
        using var folder = new TempFolder();
        var server = new PolicyServer(1024 * 1024) { FailFirstSegment = HttpStatusCode.Forbidden, AlwaysFail = true };
        using var http = new HttpClient(server);
        try
        {
            await new SegmentedDownloader(http).DownloadAsync(PolicyRequest("denied.bin", folder.Path, connections: 1));
            throw new AssertionException("403 should fail");
        }
        catch (HttpRequestException e) { Assert.Equal(HttpStatusCode.Forbidden, e.StatusCode, "status is preserved"); }
        Assert.Equal(1, server.SegmentRequests, "permanent segment failures are not retried");
    }

    private sealed class InlineProgress(Action<ProgressSnapshot> report) : IProgress<ProgressSnapshot>
    {
        public void Report(ProgressSnapshot value) => report(value);
    }

    // Simulate an unknown network bottleneck. Unlike the user-configured limiter,
    // the engine must discover this ceiling from throughput rather than read its setting.
    private sealed class NetworkPacing(BandwidthLimiter limiter) : IBandwidthLimiter
    {
        public ValueTask ThrottleAsync(int bytes, CancellationToken ct) => limiter.ThrottleAsync(bytes, ct);
    }

    private static async Task RetiresPlateauConnections()
    {
        const long size = 20 * 1024 * 1024;
        using var folder = new TempFolder();
        using var http = new HttpClient(new PolicyServer(size) { HeaderDelayMs = 0 });
        var interval = SegmentedDownloader.RampInterval;
        SegmentedDownloader.RampInterval = TimeSpan.FromMilliseconds(500);
        var grew = false;
        var retired = false;
        try
        {
            var request = new DownloadRequest { Url = new Uri("https://same-host.test/plateau.bin"), TargetDirectory = folder.Path,
                Connections = 16, Adaptive = true, BandwidthLimiter = new NetworkPacing(new BandwidthLimiter(2_000_000)) };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await new SegmentedDownloader(http).DownloadAsync(request,
                new InlineProgress(p =>
                {
                    if (p.ActiveConnections >= 6) grew = true;
                    if (grew && p.ActiveConnections <= 4 && p.BytesCompleted < size / 2) retired = true;
                }), timeout.Token);
            Assert.True(grew, "controller tried another batch");
            Assert.True(retired, "unhelpful batch retired before the download tail");
            VerifyPattern(result.FilePath, size);
        }
        finally { SegmentedDownloader.RampInterval = interval; }
    }

    private static async Task RevisitsDuringDownload()
    {
        const long size = 20 * 1024 * 1024;
        using var folder = new TempFolder();
        using var http = new HttpClient(new PolicyServer(size) { HeaderDelayMs = 0 });
        var (interval, revisit, minimum) = (SegmentedDownloader.RampInterval, SegmentedDownloader.RevisitInterval,
            SegmentedDownloader.RevisitMinimumRemainingSeconds);
        SegmentedDownloader.RampInterval = TimeSpan.FromMilliseconds(500);
        SegmentedDownloader.RevisitInterval = TimeSpan.FromSeconds(1);
        SegmentedDownloader.RevisitMinimumRemainingSeconds = 0;
        var phase = 0;   // 0 growing, 1 grew, 2 retired, 3 revisited
        try
        {
            var request = new DownloadRequest { Url = new Uri("https://same-host.test/revisit.bin"), TargetDirectory = folder.Path,
                Connections = 16, Adaptive = true, BandwidthLimiter = new NetworkPacing(new BandwidthLimiter(2_000_000)) };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await new SegmentedDownloader(http).DownloadAsync(request,
                new InlineProgress(p =>
                {
                    if (phase == 0 && p.ActiveConnections >= 6) phase = 1;
                    else if (phase == 1 && p.ActiveConnections <= 4) phase = 2;
                    else if (phase == 2 && p.ActiveConnections == 5 && p.BytesCompleted < size * 9 / 10) phase = 3;
                }), timeout.Token);
            Assert.True(phase == 3, $"a settled plateau was probed again with one connection (reached phase {phase})");
            VerifyPattern(result.FilePath, size);
        }
        finally
        {
            (SegmentedDownloader.RampInterval, SegmentedDownloader.RevisitInterval, SegmentedDownloader.RevisitMinimumRemainingSeconds)
                = (interval, revisit, minimum);
        }
    }

    private static async Task CancelsHostWait()
    {
        var budget = new HostRequestBudget(_ => 1);
        var uri = new Uri("https://same-host.test/a");
        var first = await budget.AcquireAsync(uri, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            using var unexpected = await budget.AcquireAsync(uri, cancellation.Token);
            throw new AssertionException("queued acquisition must cancel");
        }
        catch (OperationCanceledException) { }
        first.Dispose();
        using var next = await budget.AcquireAsync(uri, CancellationToken.None);
        Assert.Equal(0, budget.AvailableSlots(uri), "cancellation did not leak or invent a slot");
        using var refusal = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        refusal.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        budget.Observe(refusal, uri);
        next.Dispose();
        using var other = await budget.AcquireAsync(new Uri("https://other-host.test/a"), CancellationToken.None);
        using var cooldownCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            using var unexpected = await budget.AcquireAsync(uri, cooldownCancellation.Token);
            throw new AssertionException("host cooldown must remain cancellable");
        }
        catch (OperationCanceledException) { }
        using var afterCooldown = await budget.AcquireAsync(new Uri("https://same-host.test/other-download"), CancellationToken.None);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(950), "cooldown also covers a different download on this host");

        var limit = 1;
        var adjustable = new HostRequestBudget(_ => limit);
        using var held = await adjustable.AcquireAsync(uri, CancellationToken.None);
        var waiting = adjustable.AcquireAsync(uri, CancellationToken.None).AsTask();
        limit = 2;
        adjustable.RefreshLimits();
        using var expanded = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, adjustable.AvailableSlots(uri), "settings change wakes queued requests without releasing an active transfer");
    }

    private static Task DatedRetryAfter()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(until);
        Assert.Equal(until, RetryPolicy.RetryNotBefore(response), "absolute server retry date retained");
        Assert.False(RetryPolicy.IsPermanent(new HttpRequestException("rate limit", null, HttpStatusCode.TooManyRequests)), "429 is retryable");
        Assert.False(RetryPolicy.IsPermanent(new HttpRequestException("timeout", null, HttpStatusCode.RequestTimeout)), "408 is retryable");
        Assert.True(RetryPolicy.IsPermanent(new HttpRequestException("expired", null, HttpStatusCode.Forbidden)), "403 stays permanent");
        return Task.CompletedTask;
    }

    private static async Task RejectsChangedEntity()
    {
        foreach (var ignore in new[] { false, true })
        {
            using var folder = new TempFolder();
            var server = new PolicyServer(2 * 1024 * 1024) { ChangeEntity = true, IgnorePreconditions = ignore };
            using var http = new HttpClient(server);
            try
            {
                await new SegmentedDownloader(http).DownloadAsync(PolicyRequest("changed.bin", folder.Path, connections: 1));
                throw new AssertionException("changed content must not complete");
            }
            catch (HttpRequestException ex) when (!ignore) { Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode, "precondition refusal preserved"); }
            catch (InvalidOperationException) when (ignore) { }
            Assert.True(server.SawEntityCondition, "segment request pins the probed strong ETag");
            Assert.Equal(1, server.SegmentRequests, "identity mismatch is not retried");
            Assert.Equal(0L, new FileInfo(Path.Combine(folder.Path, "changed.bin")).Length, "changed bytes never reach the output");
        }
    }

    private static async Task IgnoresWeakEntityTags()
    {
        using var folder = new TempFolder();
        using var http = new HttpClient(new PolicyServer(1024 * 1024) { WeakTag = true });
        var info = await HttpProbe.ProbeAsync(http, PolicyRequest("weak.bin", folder.Path), CancellationToken.None);
        Assert.True(info.Validator is null, "weak tag without Last-Modified cannot authorize a saved resume");
    }

    private static Task AdaptiveMeasurementEvidence()
    {
        const long remaining = 1_000_000_000;
        var stalled = new SegmentedDownloader.Ramp(0);
        Assert.Equal(0, stalled.EvaluateAt(TimeSpan.FromSeconds(2), 0, 4, 16, remaining, false), "zero throughput does not open more requests");
        Assert.False(stalled.Done, "stall recovery can still provide a later useful sample");

        var marginal = new SegmentedDownloader.Ramp(0);
        Assert.Equal(2, marginal.EvaluateAt(TimeSpan.FromSeconds(2), 2000, 4, 16, remaining, false), "first measured step is bounded");
        Assert.Equal(0, marginal.EvaluateAt(TimeSpan.FromSeconds(4), 4000, 6, 16, remaining, false), "marginal sample gets one confirmation");
        Assert.False(marginal.Done, "one noisy sample does not retire a useful batch");
        Assert.True(marginal.EvaluateAt(TimeSpan.FromSeconds(6), 6600, 6, 16, remaining, false) > 0, "a recovered useful rate can grow");

        var plateau = new SegmentedDownloader.Ramp(0);
        plateau.EvaluateAt(TimeSpan.FromSeconds(2), 2000, 4, 16, remaining, false);
        plateau.EvaluateAt(TimeSpan.FromSeconds(4), 4000, 6, 16, remaining, false);
        plateau.EvaluateAt(TimeSpan.FromSeconds(6), 6000, 6, 16, remaining, false);
        Assert.True(plateau.Done && plateau.RetireTo == 4, "confirmed plateau restores the smaller useful count");

        var refused = new SegmentedDownloader.Ramp(0);
        Assert.Equal(0, refused.EvaluateAt(TimeSpan.FromSeconds(2), 2000, 8, 16, remaining, true), "server refusal never triggers growth");
        Assert.True(refused.Done && refused.RetireTo == 4, "server refusal backs off without a confirmation delay");

        var capped = new SegmentedDownloader.Ramp(0);
        Assert.Equal(0, capped.EvaluateAt(TimeSpan.FromSeconds(2), 2000, 4, 16, remaining, false, speedLimit: 1000), "explicit speed limit avoids an unnecessary probe");
        Assert.True(capped.Done, "controller rests at the configured ceiling");
        return Task.CompletedTask;
    }

    private sealed class FailingSiblingServer : HttpMessageHandler
    {
        private int _requests;
        public int Waiting;
        public int Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range!.Ranges.Single();
            if (range.From == 0 && range.To == 0)
            {
                var probe = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request, Content = new ByteArrayContent([0]) };
                probe.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 8 * 1024 * 1024);
                return probe;
            }
            if (Interlocked.Increment(ref _requests) == 1)
            {
                await Task.Delay(100, ct);
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request, Content = new ByteArrayContent([]) };
            }
            Interlocked.Increment(ref Waiting);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
            throw new AssertionException("pending headers should be cancelled");
        }
    }

    private static async Task CancelsFailedDownloadSiblings()
    {
        using var folder = new TempFolder();
        var server = new FailingSiblingServer();
        using var http = new HttpClient(server);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        try
        {
            await new SegmentedDownloader(http).DownloadAsync(PolicyRequest("siblings.bin", folder.Path), ct: timeout.Token);
            throw new AssertionException("permanent failure must surface");
        }
        catch (HttpRequestException ex) { Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode, "original failure survives sibling cancellation"); }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "failure does not wait for a stall or external timeout");
        Assert.True(server.Waiting > 0 && server.Cancelled == server.Waiting, "all sibling requests were cancelled and observed");
    }

    private static async Task RejectsEntityChangeOnExtraConnection()
    {
        using var folder = new TempFolder();
        var server = new PolicyServer(16 * 1024 * 1024) { ChangeAfterSegment = 4, HeaderDelayMs = 0 };
        using var http = new HttpClient(server);
        var interval = SegmentedDownloader.RampInterval;
        SegmentedDownloader.RampInterval = TimeSpan.FromMilliseconds(250);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        try
        {
            await new SegmentedDownloader(http).DownloadAsync(new DownloadRequest {
                Url = new Uri("https://same-host.test/changes-later.bin"), TargetDirectory = folder.Path,
                Connections = 8, Adaptive = true, BandwidthLimiter = new NetworkPacing(new BandwidthLimiter(500_000))
            }, ct: timeout.Token);
            throw new AssertionException("an added connection must not hide changed content");
        }
        catch (HttpRequestException ex) { Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode, "changed entity remains the failure cause"); }
        finally { SegmentedDownloader.RampInterval = interval; }
        Assert.True(server.SegmentRequests > 4, "failure came from a connection added during the transfer");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "changed entity does not wait for the remaining old responses");
        Assert.Equal(0, server.Active, "old response bodies are disposed on cancellation");
    }
}
