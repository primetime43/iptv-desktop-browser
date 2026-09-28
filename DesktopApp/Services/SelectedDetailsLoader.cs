using DesktopApp.Models;

namespace DesktopApp.Services;

// One owner for the shared movie/series details panel. Like LatestRequestLoader,
// this is called on the UI thread and resumes on that thread after awaiting.
public sealed class SelectedDetailsLoader
{
    private readonly LatestRequestLoader _requests = new();
    private IWatchableContent? _current;
    private long _version;

    public void Cancel()
    {
        _version++;
        _requests.Cancel();
        if (_current != null) _current.DetailsLoading = false;
        _current = null;
    }

    public async Task LoadAsync<T>(T content, Func<T, CancellationToken, Task> load,
        Action<T, T> apply, Action<Exception> failed, Func<bool> isStillSelected,
        CancellationToken lifetimeToken) where T : class, IWatchableContent, new()
    {
        if (ReferenceEquals(_current, content) || lifetimeToken.IsCancellationRequested || !isStillSelected()) return;

        Cancel();
        if (content.DetailsLoaded) return;
        var version = _version;
        _current = content;
        content.DetailsLoading = true;
        try
        {
            await _requests.LoadAsync(async token =>
            {
                // Services may mutate their input or ignore cancellation. Never give an
                // in-flight operation the bound model; commit only after the selection guard.
                var snapshot = new T { Id = content.Id };
                await load(snapshot, token);
                return snapshot;
            }, details => apply(content, details), failed, () => { }, isStillSelected, lifetimeToken);
        }
        finally
        {
            // A-B-A can have two requests for the same model: the old completion must
            // not clear the new request's spinner or prevent it from being cancelled.
            if (_version == version)
            {
                content.DetailsLoading = false;
                _current = null;
            }
        }
    }
}
