using System.Diagnostics;

namespace Stretto.Core;

public interface IBandwidthLimiter
{
    ValueTask ThrottleAsync(int bytes, CancellationToken ct);

}

/// <summary>One shared, allocation-free paced limiter. Reserve happens after a read and before
/// the write, so every segment and every active download shares the same ceiling.</summary>
public sealed class BandwidthLimiter : IBandwidthLimiter
{
    private readonly object _gate = new();
    private long _bytesPerSecond;
    private long _nextTimestamp;

    public BandwidthLimiter(long bytesPerSecond = 0) => SetLimit(bytesPerSecond);

    public void SetLimit(long bytesPerSecond)
    {
        lock (_gate)
        {
            _bytesPerSecond = Math.Max(0, bytesPerSecond);
            _nextTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public async ValueTask ThrottleAsync(int bytes, CancellationToken ct)
    {
        if (bytes <= 0) return;
        long delay;
        lock (_gate)
        {
            if (_bytesPerSecond <= 0) return;
            var now = Stopwatch.GetTimestamp();
            var start = Math.Max(now, _nextTimestamp);
            var ticks = (long)Math.Ceiling(bytes * (double)Stopwatch.Frequency / _bytesPerSecond);
            _nextTimestamp = start + ticks;
            delay = start - now;
        }
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay / (double)Stopwatch.Frequency), ct).ConfigureAwait(false);
    }
}
