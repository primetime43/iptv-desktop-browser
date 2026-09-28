using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DesktopApp.Models;

// Replace prepared data on the owning UI thread. WPF's collection views support
// Reset, whereas multi-item Add notifications are not supported by all views.
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        CheckReentrancy();
        // Enumerate first: self-replacement and an enumeration failure must not lose data.
        var snapshot = values.ToArray();
        var storage = (List<T>)Items;
        storage.Clear();
        storage.AddRange(snapshot);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
