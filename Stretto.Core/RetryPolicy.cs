using System.Net;

namespace Stretto.Core;

public sealed class DownloadHttpException(HttpStatusCode status, DateTimeOffset? retryNotBefore)
    : HttpRequestException($"The server returned HTTP {(int)status} ({status}).", null, status)
{
    public DateTimeOffset? RetryNotBefore { get; } = retryNotBefore;
}

public static class RetryPolicy
{
    /// <summary>
    /// Most 4xx responses and InvalidOperationException are treated as permanent here.
    /// 408 and 429 are exceptions: waiting can help those, as it can dropped connections
    /// and 5xx responses. Asking an expired link seventeen more times is not persistence;
    /// it is just the same answer with a larger electricity bill.
    /// </summary>
    public static bool IsPermanent(Exception exception) =>
        exception is InvalidOperationException
        || exception is HttpRequestException { StatusCode: { } code }
            && (int)code is >= 400 and < 500 && code is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    public static DateTimeOffset? RetryNotBefore(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date) return date;
        if (retry?.Delta is { } delta)
        {
            var now = DateTimeOffset.UtcNow;
            return delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delta;
        }
        return null;
    }

    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new DownloadHttpException(response.StatusCode, RetryNotBefore(response));
    }

    public static TimeSpan DelayFor(Exception exception, TimeSpan fallback) =>
        exception is DownloadHttpException { RetryNotBefore: { } until } && until - DateTimeOffset.UtcNow > fallback
            ? until - DateTimeOffset.UtcNow : fallback;

    /// <summary>
    /// How long a connection waits before trying again: doubling from half a second, capped
    /// at thirty, and spread over the upper half of that range. The spread matters more than
    /// the curve. Sixteen connections that failed together and retry together are a burst,
    /// and a burst is what earns the 429. Persistent, yes; a synchronised drum line, no.
    /// </summary>
    public static TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(30_000, 500 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 16)));
        return TimeSpan.FromMilliseconds(ceiling * (0.5 + Random.Shared.NextDouble() / 2));
    }

    public static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        while (delay > TimeSpan.Zero)
        {
            // Task.Delay has a finite range. The server's optimism apparently does not.
            // Chunk the wait without losing cancellation between or during chunks.
            var part = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
            await Task.Delay(part, ct).ConfigureAwait(false);
            delay -= part;
        }
    }
}
