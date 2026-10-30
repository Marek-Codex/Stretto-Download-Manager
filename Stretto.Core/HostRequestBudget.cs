using System.Net;

namespace Stretto.Core;

/// <summary>
/// One connection budget per hostname, shared across downloads, clients and proxy overrides.
/// A different HttpClient is not a fake moustache that gets us past the bouncer.
/// </summary>
public sealed class HostRequestBudget(Func<Uri, int> limitFor)
{
    private sealed class State
    {
        public int Active;
        public int LearnedLimit = int.MaxValue;
        public DateTimeOffset LimitUntil;
        public DateTimeOffset NotBefore;
        public TaskCompletionSource Changed = NewSignal();
    }

    /// <summary>How long the lowered ceiling holds after a 429 or 503 before recovery starts.</summary>
    internal static TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often one connection is handed back once a host's cooldown ends.</summary>
    internal static TimeSpan RecoveryStep { get; set; } = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, State> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private State For(Uri uri)
    {
        if (!_hosts.TryGetValue(uri.IdnHost, out var state)) _hosts[uri.IdnHost] = state = new();
        return state;
    }
    private int Limit(Uri uri, State state)
    {
        // After the cooldown, earn the seats back one a minute rather than all at once.
        // Leaping straight from "the host asked us to ease off" to the full limit is how a
        // downloader collects its second 429 of the afternoon.
        var now = DateTimeOffset.UtcNow;
        if (state.LearnedLimit != int.MaxValue && state.LimitUntil <= now)
        {
            var steps = 1 + (int)Math.Min(SegmentedDownloader.MaxConnections, (now - state.LimitUntil) / RecoveryStep);
            state.LearnedLimit = state.LearnedLimit + steps >= SegmentedDownloader.MaxConnections
                ? int.MaxValue
                : state.LearnedLimit + steps;
            state.LimitUntil = now + RecoveryStep;
        }
        return Math.Min(state.LearnedLimit, Math.Clamp(limitFor(uri), 1, SegmentedDownloader.MaxConnections));
    }
    private static void Signal(State state)
    {
        var prior = state.Changed;
        state.Changed = NewSignal();
        prior.TrySetResult();
    }

    public int AvailableSlots(Uri uri)
    {
        lock (_gate)
        {
            var state = For(uri);
            return state.NotBefore > DateTimeOffset.UtcNow ? 0 : Math.Max(0, Limit(uri, state) - state.Active);
        }
    }

    public int ConnectionLimit(Uri uri)
    {
        lock (_gate) return Limit(uri, For(uri));
    }

    public void RefreshLimits()
    {
        lock (_gate)
            foreach (var state in _hosts.Values) Signal(state);
    }

    public async ValueTask<IDisposable> AcquireAsync(Uri uri, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task changed;
            TimeSpan delay;
            lock (_gate)
            {
                var state = For(uri);
                delay = state.NotBefore - DateTimeOffset.UtcNow;
                var limit = Limit(uri, state);
                if (delay <= TimeSpan.Zero && state.Active < limit)
                {
                    state.Active++;
                    return new Lease(this, state);
                }
                // Time can free a seat even when no active response finishes. Wake at
                // that deadline as well as on a release/settings signal; no polling task.
                if (state.LearnedLimit != int.MaxValue
                    && limit < Math.Clamp(limitFor(uri), 1, SegmentedDownloader.MaxConnections))
                {
                    var recovery = state.LimitUntil - DateTimeOffset.UtcNow;
                    if (recovery <= TimeSpan.Zero) recovery = TimeSpan.FromMilliseconds(1);
                    if (delay <= TimeSpan.Zero || recovery < delay) delay = recovery;
                }
                changed = state.Changed.Task;
            }
            if (delay > TimeSpan.Zero)
            {
                // Wait in bounded chunks so even an enormous server cooldown stays cancellable.
                // "Try again next century" does not get to disable the Pause button.
                try { await changed.WaitAsync(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, ct).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            else await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Observe(HttpResponseMessage response, Uri uri)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)) return;
        lock (_gate)
        {
            var state = For(uri);
            // Leave useful transfers running; lower the ceiling for new arrivals for five minutes.
            // The host asked for breathing room, not a dramatic evacuation. *ear flick*
            state.LearnedLimit = Math.Min(Limit(uri, state), Math.Max(1, state.Active - 1));
            state.LimitUntil = DateTimeOffset.UtcNow + Cooldown;
            var until = RetryPolicy.RetryNotBefore(response) ?? DateTimeOffset.UtcNow.AddSeconds(1);
            if (until > state.NotBefore) state.NotBefore = until;
            Signal(state);
        }
    }

    private sealed class Lease(HostRequestBudget owner, State state) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            // One lease releases one slot, even if cleanup reaches it twice.
            // We are counting connections, not inventing extra seats.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate)
            {
                state.Active--;
                Signal(state);
            }
        }
    }
}
