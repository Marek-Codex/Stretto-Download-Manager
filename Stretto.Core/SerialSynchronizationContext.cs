using System.Collections.Concurrent;

namespace Stretto.Core;

/// <summary>
/// A single thread that runs posted work one piece at a time, for a host with no UI thread.
///
/// The download service keeps its state (the list, each item's progress, the queue pump)
/// consistent by running on one context rather than by locking. In the app that context is
/// the UI thread's; a CLI or a service has none, so it gives the service one of these and
/// makes its own calls to the service through <see cref="InvokeAsync{T}"/>.
/// </summary>
public sealed class SerialSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public SerialSynchronizationContext()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Stretto engine" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_queue.IsAddingCompleted)
            _queue.Add((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception exception) { failure = exception; }
            finally { done.Set(); }
        }, null);
        done.Wait();

        if (failure is not null)
            throw new AggregateException(failure);
    }

    /// <summary>Runs <paramref name="work"/> on the context and returns what it returned.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try { source.SetResult(work()); }
            catch (Exception exception) { source.SetException(exception); }
        }, null);
        return source.Task;
    }

    public Task InvokeAsync(Action work) => InvokeAsync<object?>(() => { work(); return null; });

    private void Loop()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            callback(state);
    }

    public void Dispose() => _queue.CompleteAdding();
}
