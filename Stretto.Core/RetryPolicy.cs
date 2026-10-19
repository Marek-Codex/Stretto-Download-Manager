using System.Net;

namespace Stretto.Core;

public static class RetryPolicy
{
    /// <summary>
    /// A failure that will come out the same way every time: a refused or expired link (4xx),
    /// or a response that is not a download at all. Retrying those only repeats the error.
    /// Timeouts, dropped connections and 5xx are the ones that can fix themselves.
    /// </summary>
    public static bool IsPermanent(Exception exception) =>
        exception is InvalidOperationException
        || exception is HttpRequestException { StatusCode: { } code } && (int)code is >= 400 and < 500;
}
