using DesktopApp.Models;
using DesktopApp.Services;

var passed = 0;
var a = new Category { Id = "a", Name = "A" };
var b = new Category { Id = "b", Name = "B" };

// Deliberately ignore cancellation, as a cache or a response already in flight can do.
using (var h = new Harness())
{
    var old = h.Select(a);
    var current = h.Select(b);
    Check(old.Token.IsCancellationRequested && !current.Token.IsCancellationRequested, "Switch cancels only the superseded request");
    await current.Complete("B channels");
    await old.Complete("A channels");
    Check(h.Display == "B channels" && h.Applied == 1, "Late success cannot overwrite the newer category");
    Check(h.Finished == 1 && !h.Loading, "Only the current request completes the loading state");
}

using (var h = new Harness())
{
    var old = h.Select(a);
    var current = h.Select(b);
    await old.Complete("A channels");
    Check(h.Loading && h.Finished == 0 && h.Applied == 0, "Old completion cannot hide the current loading overlay");
    await current.Complete("B channels");
    Check(!h.Loading && h.Display == "B channels", "Current success applies and finishes normally");
}

using (var h = new Harness())
{
    var old = h.Select(a);
    var current = h.Select(b);
    await old.Fail(new InvalidOperationException("stale failure"));
    Check(h.Errors.Count == 0 && h.Loading, "Stale errors do not affect the current UI");
    await current.Fail(new InvalidOperationException("current failure"));
    Check(h.Errors.SequenceEqual(["current failure"]) && !h.Loading && h.Display == "",
        "Current failure surfaces and leaves no previous category's channels");
}

using (var h = new Harness())
{
    var firstA = h.Select(a);
    var middleB = h.Select(b);
    var finalA = h.Select(a); // Same category object: category equality alone is insufficient.
    await firstA.Complete("outdated A");
    await middleB.Fail(new OperationCanceledException(middleB.Token));
    Check(h.Applied == 0 && h.Errors.Count == 0 && h.Loading, "A-B-A rejects both earlier requests");
    await finalA.Complete("fresh A");
    Check(h.Display == "fresh A" && h.Finished == 1, "The latest request wins even when reselecting the same category");
}

using (var h = new Harness())
{
    var old = h.Select(a);
    var favorites = h.Select(new Category { Id = "⭐ Favorites" }, "favorites");
    await favorites.Done;
    await old.Complete("A channels");
    Check(h.Display == "favorites" && h.Applied == 1 && !h.Loading, "Synchronous Favorites/cache results supersede a network request");
}

using (var h = new Harness())
{
    var request = h.Select(a);
    h.Selected = null;
    await request.Complete("A channels");
    Check(h.Applied == 0 && h.Finished == 0, "Selection is checked again before applying any result");
}

using (var h = new Harness())
{
    var request = h.Select(a);
    h.Loader.Cancel(); // Deselecting or entering global search explicitly takes ownership of the UI.
    h.Display = "global search";
    h.Loading = false;
    await request.Complete("A channels");
    Check(request.Token.IsCancellationRequested && h.Display == "global search" && h.Finished == 0,
        "Cancellation protects global search/deselection from late results");
    var next = h.Select(b);
    await next.Complete("B channels");
    Check(h.Display == "B channels", "Loading can resume after explicit cancellation");
}

using (var h = new Harness())
{
    var request = h.Select(a);
    h.Lifetime.Cancel();
    await request.Fail(new OperationCanceledException(request.Token));
    Check(request.Token.IsCancellationRequested && h.Errors.Count == 0 && h.Finished == 0,
        "Closing the window cancels the request without UI callbacks");
    var afterClose = h.Select(b);
    await afterClose.Done;
    Check(!afterClose.Started && h.Applied == 0, "No work starts with a cancelled window lifetime");
}

Console.WriteLine($"Passed {passed} category regression checks.");
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
}

sealed class Harness : IDisposable
{
    public LatestRequestLoader Loader { get; } = new();
    public CancellationTokenSource Lifetime { get; } = new();
    public Category? Selected;
    public string Display = "previous channels";
    public bool Loading;
    public int Applied;
    public int Finished;
    public List<string> Errors = [];

    public Pending Select(Category category, string? immediateResult = null)
    {
        Selected = category;
        var request = new Pending();
        request.Done = Loader.LoadAsync(token =>
        {
            request.Started = true;
            request.Token = token;
            Loading = true;
            Display = "";
            return immediateResult == null ? request.Response.Task : Task.FromResult(immediateResult);
        }, value => { Display = value; Applied++; }, ex => Errors.Add(ex.Message),
            () => { Loading = false; Finished++; }, () => ReferenceEquals(Selected, category), Lifetime.Token);
        return request;
    }

    public void Dispose() { Loader.Cancel(); Lifetime.Dispose(); }
}

sealed class Pending
{
    public TaskCompletionSource<string> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Token;
    public bool Started;
    public Task Done = Task.CompletedTask;
    public async Task Complete(string value) { Response.SetResult(value); await Done; }
    public async Task Fail(Exception error) { Response.SetException(error); await Done; }
}
