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
