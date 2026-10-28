using System.Net;

namespace Stretto.Core;

/// <summary>One host budget shared across file downloads, clients and proxy overrides.</summary>
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
        if (state.LimitUntil <= DateTimeOffset.UtcNow) state.LearnedLimit = int.MaxValue;
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
                if (delay <= TimeSpan.Zero && state.Active < Limit(uri, state))
                {
                    state.Active++;
                    return new Lease(this, state);
                }
                changed = state.Changed.Task;
            }
            if (delay > TimeSpan.Zero)
            {
                // Long server cooldowns remain cancellable and avoid Task.Delay's upper bound.
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
            // Keep established useful transfers, but remember that the latest request
            // exceeded what this host currently accepts. This is session-local.
            state.LearnedLimit = Math.Min(Limit(uri, state), Math.Max(1, state.Active - 1));
            state.LimitUntil = DateTimeOffset.UtcNow.AddMinutes(5);
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
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate)
            {
                state.Active--;
                Signal(state);
            }
        }
    }
}
