using CommunityToolkit.Mvvm.ComponentModel;

namespace DesktopApp.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    protected CancellationToken CancellationToken => _cancellationTokenSource.Token;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected async Task ExecuteAsync(Func<CancellationToken, Task> operation, string? busyMessage = null)
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            BusyMessage = busyMessage ?? "Loading...";
            await operation(CancellationToken);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    protected async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, string? busyMessage = null)
    {
        if (IsBusy) return default(T)!;

        try
        {
            IsBusy = true;
            BusyMessage = busyMessage ?? "Loading...";
            return await operation(CancellationToken);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }
}