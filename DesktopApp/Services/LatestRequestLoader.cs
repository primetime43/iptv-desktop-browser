namespace DesktopApp.Services;

// Owned by the UI thread. Await preserves its synchronization context so the
// current-request check and UI callbacks run together without another selection interleaving.
public sealed class LatestRequestLoader
{
    private CancellationTokenSource? _current;

    public void Cancel()
    {
        var previous = _current;
        _current = null;
        previous?.Cancel();
        // The request disposes its source after the underlying operation finishes.
    }

    public async Task LoadAsync<T>(Func<CancellationToken, Task<T>> load, Action<T> apply,
        Action<Exception> failed, Action finished, Func<bool> isStillSelected,
        CancellationToken lifetimeToken)
    {
        Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _current = request;
        bool IsCurrent() => ReferenceEquals(_current, request) &&
            !request.IsCancellationRequested && isStillSelected();
        try
        {
            request.Token.ThrowIfCancellationRequested();
            var result = await load(request.Token);
            // Cache/HTTP implementations may return even after cancellation.
            if (IsCurrent()) apply(result);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent()) failed(ex);
        }
        finally
        {
            try
            {
                if (IsCurrent()) finished();
            }
            finally
            {
                if (ReferenceEquals(_current, request)) _current = null;
            }
        }
    }
}
