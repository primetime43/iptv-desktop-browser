namespace DesktopApp.Services;

public sealed record ViewportLoadRequest(string Key, int Priority,
    Func<CancellationToken, Task<object?>> Load, Action<object?> Apply);

// UI-thread owned: Replace, Cancel and continuations run on the caller's UI context.
// A snapshot contains only current demand, not every item visited during scrolling.
public sealed class PrioritizedLoadQueue(int concurrency = 4, int capacity = 128,
    CancellationToken lifetimeToken = default) : IDisposable
{
    private sealed record Demand(Func<CancellationToken, Task<object?>> Load, int Priority, Action<object?>[] Subscribers);
    private readonly Dictionary<string, Demand> _wanted = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private bool _pumping;
    private bool _disposed;

    public int ActiveCount => _running.Count;
    public int PendingCount => _wanted.Keys.Count(key => !_running.ContainsKey(key));

    public void Replace(IEnumerable<ViewportLoadRequest> requests)
    {
        if (_disposed || lifetimeToken.IsCancellationRequested) return;
        var snapshot = requests.OrderBy(r => r.Priority).GroupBy(r => r.Key)
            .Take(Math.Max(1, capacity)).ToArray();
        _wanted.Clear();
        foreach (var group in snapshot)
            _wanted[group.Key] = new Demand(group.First().Load, group.First().Priority, group.Select(r => r.Apply).ToArray());
        foreach (var (key, source) in _running.ToArray())
            if (!_wanted.ContainsKey(key)) source.Cancel();
        Pump();
    }

    public void Cancel()
    {
        _wanted.Clear();
        foreach (var source in _running.Values.ToArray()) source.Cancel();
    }

    private void Pump()
    {
        if (_pumping || _disposed || lifetimeToken.IsCancellationRequested) return;
        _pumping = true;
        try
        {
            while (_running.Count < Math.Max(1, concurrency))
            {
                var next = _wanted.Where(p => !_running.ContainsKey(p.Key)).OrderBy(p => p.Value.Priority).FirstOrDefault();
                if (next.Key == null) break;
                var source = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                _running.Add(next.Key, source);
                _ = RunAsync(next.Key, next.Value.Load, source);
            }
        }
        finally { _pumping = false; }
    }

    private async Task RunAsync(string key, Func<CancellationToken, Task<object?>> load, CancellationTokenSource source)
    {
        object? result = null;
        try
        {
            try { result = await load(source.Token); }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            catch { /* A failed resource is delivered as null so the view can back off. */ }

            // Subscribers come from the latest snapshot. A superseded request may
            // finish despite cancellation, but must never update a recycled card.
            if (!source.IsCancellationRequested && !_disposed && _wanted.Remove(key, out var demand))
                foreach (var apply in demand.Subscribers) apply(result);
        }
        finally
        {
            _running.Remove(key);
            source.Dispose();
            Pump();
        }
    }

    public void Dispose() { _disposed = true; Cancel(); }
}
