using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using DesktopApp.Models;
using DesktopApp.ViewModels;
using DesktopApp.Views.Dashboard;

internal static partial class Program
{
    private static void VerifyExtractedPages(Application app)
    {
        var live = new LiveTvPageViewModel();
        var catalog = new MoviesSeriesPageViewModel();
        var scheduler = new SchedulerPageViewModel();
        var settings = CreateSettingsFixture().Model;
        var context = new { LiveTv = live, Catalog = catalog, SchedulerPageModel = scheduler, SettingsPageModel = settings,
            TileWidth = 180d, TileHeight = 150d, VodTileHeight = 240d };
        live.Channels.ReplaceAll([new Channel { Id = 1, Name = "Live channel" }]);
        catalog.Movies.ReplaceAll([new VodContent { Id = 2, Name = "Movie" }]);
        catalog.Series.ReplaceAll([new SeriesContent { Id = 3, Name = "Series" }]);
        foreach (var view in new UserControl[] { new LiveTvPageView(), new MoviesSeriesPageView(), new SchedulerPageView(), new SettingsPageView() })
        {
            using var source = new HwndSource(new HwndSourceParameters("Dashboard page fixture")
            { Width = 640, Height = 400, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
            view.DataContext = view is SettingsPageView ? settings : context;
            source.RootVisual = view;
            Layout(view);
            Check(view.Content is FrameworkElement, $"{view.GetType().Name}: standalone compiled XAML resolves its shared resources");
            if (view is LiveTvPageView)
                Check(((ItemsControl)view.FindName("ChannelsGridView")).Items.Count == 1,
                    "Live TV view binds its page model's collection view across the new namescope");
            if (view is MoviesSeriesPageView)
                Check(((ItemsControl)view.FindName("MoviesGridView")).Items.Count == 1 &&
                    ((ItemsControl)view.FindName("SeriesGridView")).Items.Count == 1,
                    "Movie and series views bind separate collections owned by the catalog model");
            if (view is SchedulerPageView)
                Check(view.FindName("ChannelCombo") is ComboBox && view.FindName("ScheduledGrid") is DataGrid,
                    "Scheduler controls remain accessible to the gradual migration adapter");
            if (view is SettingsPageView)
            {
                VerifySettingsBindings((SettingsPageView)view, settings);
                settings.SetStatus("Saved", false);
                Layout(view);
                Check(((TextBlock)view.FindName("SettingsStatusText")).Text == "Saved", "Settings status is driven by its view model");
                settings.SetStatus("Invalid path", true);
                Layout(view);
                Check(((TextBlock)view.FindName("SettingsStatusText")).Text == "Invalid path" && settings.StatusIsError,
                    "Settings errors update through binding without searching for a text control");
            }
        }
        var navigation = new DashboardNavigationViewModel();
        var changes = new List<string?>();
        navigation.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        navigation.ActivePage = DashboardPage.Settings;
        navigation.ActivePage = DashboardPage.Settings;
        Check(changes.SequenceEqual([nameof(navigation.ActivePage)]), "Explicit navigation state notifies once per page change");
        catalog.ContentType = CatalogContentType.Series;
        Check(catalog.ContentType == CatalogContentType.Series && catalog.Movies.Count == 1 && catalog.Series.Count == 1,
            "Content type is independent of button colors, layout visibility, and catalog data");
        live.SelectedChannel = live.Channels[0];
        scheduler.SelectedChannel = new Channel { Id = 99 };
        Check(live.SelectedChannel.Id == 1 && scheduler.SelectedChannel.Id == 99,
            "Scheduler and live selection state are independent");
    }

    private static void VerifyBulkCollections()
    {
        var items = new BulkObservableCollection<int>();
        var resets = 0;
        var properties = new List<string?>();
        items.CollectionChanged += (_, e) => { Check(e.Action == NotifyCollectionChangedAction.Reset, "Bulk replacement emits a WPF-compatible reset"); resets++; };
        ((INotifyPropertyChanged)items).PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        var view = new ListCollectionView(items) { Filter = value => (int)value % 2 == 0 };
        view.SortDescriptions.Add(new SortDescription("", ListSortDirection.Descending));
        items.ReplaceAll(Enumerable.Range(0, 10000));
        Check(resets == 1 && properties.SequenceEqual(["Count", "Item[]"]),
            "Ten thousand items produce one collection notification and two property notifications");
        Check(view.Count == 5000 && (int)view.GetItemAt(0) == 9998, "Filtering and sorting survive bulk replacement without a second refresh");
        items.ReplaceAll(items);
        Check(items.Count == 10000 && view.Count == 5000, "Replacing a collection from itself does not lose data");
        var before = resets;
        try { items.ReplaceAll(Broken()); throw new InvalidOperationException("Expected enumeration failure"); }
        catch (FormatException) { }
        Check(items.Count == 10000 && resets == before, "Failed preparation leaves the previous collection and view intact");
        items.ReplaceAll([]);
        Check(view.Count == 0, "An empty batch clears the bound view");

        static IEnumerable<int> Broken() { yield return 1; throw new FormatException(); }
    }
}
