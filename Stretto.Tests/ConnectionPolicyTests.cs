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
    }

    private sealed class PolicyServer(long size) : HttpMessageHandler
    {
        public int Active;
        public int Peak;
        public int SegmentRequests;
        public int HeaderDelayMs = 25;
        public HttpStatusCode? FailFirstSegment;
        public bool AlwaysFail;
        public ConcurrentQueue<DateTimeOffset> Attempts { get; } = new();
        public byte[] Pattern { get; } = Enumerable.Range(0, checked((int)size)).Select(i => (byte)(i % 251)).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var end = request.Headers.Range?.Ranges.Single().To ?? size - 1;
            var isProbe = from == 0 && end == 0;
            if (!isProbe)
            {
                var attempt = Interlocked.Increment(ref SegmentRequests);
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
            response.Headers.ETag = new EntityTagHeaderValue("\"policy-test\"");
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
                Connections = 16, Adaptive = true, BandwidthLimiter = new BandwidthLimiter(2_000_000) };
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
}
