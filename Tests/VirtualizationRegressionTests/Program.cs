using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopApp.Controls;
using DesktopApp.Models;

internal static partial class Program
{
    private static int _passed;

    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = new ResourceDictionary
        {
            Source = new Uri("/iptv-desktop-browser;component/Controls/CatalogStyles.xaml", UriKind.Relative)
        };
        try
        {
            foreach (var style in new[] { "ChannelGridCatalogStyle", "PosterGridCatalogStyle", "CatalogListStyle" })
                VerifyCatalog(resources, style);
            VerifyDashboardTemplates(app);
            VerifyExtractedPages(app);
            VerifyBulkCollections();
            VerifySettingsCommands();
            VerifyMediaDetails();
            Dispatcher.CurrentDispatcher.Invoke(VerifyRecordingForm);
            Dispatcher.CurrentDispatcher.Invoke(VerifyRecordingManagement);
            Dispatcher.CurrentDispatcher.Invoke(VerifySeriesRecordingDialogs);
            Dispatcher.CurrentDispatcher.Invoke(VerifyCatalogOrchestration);
            Console.WriteLine($"Passed {_passed} virtualization regression checks.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        finally { app.Shutdown(); }
    }

    private static void VerifyCatalog(ResourceDictionary resources, string style)
    {
        var data = new BulkObservableCollection<string>();
        data.ReplaceAll(Enumerable.Range(0, 10000).Select(i => $"Item {i}"));
        var view = new ListCollectionView(data);
        var sizing = new Sizing();
        var control = new ItemsControl
        {
            Style = (Style)resources[style], ItemsSource = view, DataContext = sizing,
            ItemTemplate = (DataTemplate)XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Border Height="80" Margin="8" Background="#223247">
                        <TextBlock Text="{Binding}" Foreground="White"/>
                    </Border>
                </DataTemplate>
                """)
        };
        // A hidden native presentation source supplies real WPF visibility state.
        using var source = new HwndSource(new HwndSourceParameters("Catalog regression fixture")
        { Width = 640, Height = 400, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
        source.RootVisual = control;
        Layout(control);
        var panel = Descendants(control).OfType<VirtualizingPanel>().Single();
        var scroll = (IScrollInfo)panel;
        Check(scroll.ScrollOwner?.CanContentScroll == true, "The internal ScrollViewer delegates scrolling to the virtualizing panel");
        var initial = Children(panel).ToHashSet();
        var viewport = CatalogViewport.Read(control);
        Check(viewport.Any(i => i.Priority == 1) && viewport.Any(i => i.Priority == 2) && viewport.Count < 100,
            "Viewport demand distinguishes visible items from the nearby buffer");
        Check(initial.Count > 0 && initial.Count < 100, $"{style}: only a viewport-sized set of 10,000 items is realized");
        Check(control.ItemContainerGenerator.ContainerFromIndex(9999) == null, "Distant items have no UI containers");
        Console.WriteLine($"{style}: {initial.Count} containers for {data.Count:N0} items at 640x400.");

        scroll.ScrollOwner!.ScrollToBottom();
        Layout(control);
        var end = Children(panel).ToList();
        Check(end.Count < 100 && control.ItemContainerGenerator.ContainerFromIndex(9999) != null, "Scrollbar jump realizes the final item");
        Check(end.Any(initial.Contains), "Scrolling recycles existing containers");
        Check(control.ItemContainerGenerator.ContainerFromIndex(0) == null, "Scrolled-away items are released");
        Check(end.All(c => ((FrameworkElement)c).DataContext is string s && s.StartsWith("Item 99")), "Recycled containers receive the current item data");

        scroll.PageUp();
        Layout(control);
        Check(scroll.VerticalOffset < scroll.ExtentHeight - scroll.ViewportHeight, "Page scrolling changes the visible range");
        scroll.SetVerticalOffset(0);
        Layout(control);
        Check(control.ItemContainerGenerator.ContainerFromIndex(0) != null, "Scrolling back restores the first item");

        if (panel is VirtualizingTilePanel tiles)
        {
            var wideExtent = tiles.ExtentHeight;
            Layout(control, 300, 400);
            Check(tiles.ExtentHeight > wideExtent && Children(panel).Count() < 100, "Narrow resize reflows columns while remaining virtualized");
            sizing.Resize();
            Layout(control);
            Check(tiles.ItemWidth == sizing.TileWidth && Children(panel).Count() < 100, "Tile-size changes update layout without realizing the catalog");
        }

        // Removing/replacing/moving both visible and unrealized items exercises generator positions.
        data.RemoveAt(0);
        data.RemoveAt(data.Count - 1);
        data[1] = "Replacement";
        data.Move(2, 5);
        Layout(control);
        Check(control.ItemContainerGenerator.ContainerFromIndex(1) is FrameworkElement item &&
            (string)item.DataContext == "Replacement", "Collection changes keep container mappings correct");
        scroll.SetVerticalOffset(scroll.ExtentHeight);
        Layout(control);
        data.RemoveAt(0);
        data.Move(data.Count - 1, 0);
        Layout(control);
        Check(Children(panel).Count() < 100, "Offscreen remove and move keep a bounded realized range");

        var random = new Random(42);
        for (var step = 0; step < 24; step++)
        {
            var index = random.Next(data.Count);
            switch (step % 4)
            {
                case 0: data.Insert(index, $"Inserted {step}"); break;
                case 1: data.RemoveAt(index); break;
                case 2: data.Move(index, random.Next(data.Count)); break;
                case 3: data[index] = $"Replaced {step}"; break;
            }
            scroll.SetVerticalOffset(random.NextDouble() * scroll.ExtentHeight);
            Layout(control);
            Check(Children(panel).Count() < 100 && Children(panel).All(child =>
            {
                var itemIndex = control.ItemContainerGenerator.IndexFromContainer(child);
                return itemIndex >= 0 && Equals(((FrameworkElement)child).DataContext, view.GetItemAt(itemIndex));
            }), "Mixed scrolling and collection changes preserve generator mappings");
        }

        view.Filter = item => ((string)item).EndsWith("7");
        Layout(control);
        Check(Children(panel).All(c => ((string)((FrameworkElement)c).DataContext).EndsWith("7")), "Filtering never displays recycled stale data");
        data.Clear();
        Layout(control);
        Check(Children(panel).Count() == 0 && scroll.VerticalOffset == 0, "Empty category clears containers and clamps scrolling");
        data.Add("New category 7");
        Layout(control);
        Check(control.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first &&
            (string)first.DataContext == "New category 7", "Category replacement renders correctly after reset");
        control.Visibility = Visibility.Collapsed;
        Layout(control);
        Check(CatalogViewport.Read(control).Count == 0, "Hidden views request no images or guides");
        control.Visibility = Visibility.Visible;
        Layout(control);
        Check(Children(panel).Count() == 1, "Hiding and restoring a view retains correct content");
        view.Filter = null;
        data.ReplaceAll(Enumerable.Range(0, 10000).Select(i => $"Batch {i}"));
        Layout(control);
        Check(Children(panel).Count() is > 0 and < 100 &&
            control.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement batch && (string)batch.DataContext == "Batch 0",
            "One bulk reset replaces a large catalog without stale containers or losing virtualization");
    }

    private static void Layout(FrameworkElement control, double width = 640, double height = 400)
    {
        for (var i = 0; i < 3; i++)
        {
            control.Measure(new Size(width, height));
            control.Arrange(new Rect(0, 0, width, height));
            control.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    private static void VerifyDashboardTemplates(Application app)
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        app.Resources["NullToVisibilityConverter"] = new DesktopApp.Converters.NullToVisibilityConverter();
        var dictionary = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Dashboard", "DashboardResources.xaml")).Root!;
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        dictionary.Descendants(wpf + "ResourceDictionary").Single().SetAttributeValue("Source",
            "/iptv-desktop-browser;component/Controls/CatalogStyles.xaml");
        foreach (var name in new[] { "ChannelsGridView", "ChannelsListView", "MoviesGridView", "MoviesListView", "SeriesGridView", "SeriesListView" })
        {
            var dashboard = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Dashboard",
                name.StartsWith("Channels") ? "LiveTvPageView.xaml" : "MoviesSeriesPageView.xaml"));
            var element = new XElement(dashboard.Descendants(wpf + "ItemsControl").Single(e => (string?)e.Attribute(x + "Name") == name));
            element.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            element.SetAttributeValue("ItemsSource", null);
            element.SetAttributeValue("Visibility", "Visible");
            // Load the production template without constructing a real dashboard/session.
            // Only remove code-behind handlers and redirect the window sizing bindings to the fixture host.
            foreach (var attribute in element.Descendants().Attributes().ToList())
            {
                if (attribute.Name.LocalName is "Click" or "MouseLeftButtonDown" or "PreviewMouseLeftButtonDown" or "MouseEnter")
                    attribute.Remove();
                else if (attribute.Value.Contains("AncestorType=Window"))
                    attribute.Value = attribute.Value.Replace("{Binding ", "{Binding DataContext.")
                        .Replace("AncestorType=Window", "AncestorType=ItemsControl");
            }
            var root = new XElement(wpf + "Grid",
                new XElement(wpf + "Grid.Resources", new XElement(dictionary)), element);
            var host = (Grid)XamlReader.Parse(root.ToString());
            using var source = new HwndSource(new HwndSourceParameters("Thumbnail viewport fixture")
            { Width = 640, Height = 400, WindowStyle = unchecked((int)0x80000000), PositionX = -10000, PositionY = -10000 });
            source.RootVisual = host;
            host.DataContext = new Sizing();
            var control = host.Children.OfType<ItemsControl>().Single();
            control.ItemsSource = Enumerable.Range(0, 10000).Select(i => name.StartsWith("Channels")
                ? (object)new Channel { Id = i, Number = i + 1, Name = $"Channel {i}", IsFavorite = i % 2 == 0 }
                : name.StartsWith("Movies") ? new VodContent { Id = i, Name = $"Movie {i}", Plot = "Movie description" }
                : new SeriesContent { Id = i, Name = $"Series {i}", Plot = "Series description" }).ToList();
            Layout(host);
            var panel = Descendants(control).OfType<VirtualizingPanel>().Single();
            Check(Children(panel).Count() is > 0 and < 100, $"{name}: production templates virtualize");
            Check(Descendants(control).OfType<TextBlock>().Any(t => t.Text.EndsWith(" 0")), $"{name}: item bindings render the first title");
            var thumbnails = CatalogViewport.Read(control);
            Check(thumbnails.Count > 0 && thumbnails.All(t => t.ImageWidth > 0 && t.ImageHeight > 0),
                $"{name}: empty image sources still supply their display dimensions");
            var dpi = VisualTreeHelper.GetDpi(control);
            var expectedSize = name switch
            {
                "ChannelsGridView" => (Width: 48, Height: 48),
                "ChannelsListView" => (Width: 48, Height: 32),
                "MoviesListView" or "SeriesListView" => (Width: 60, Height: 80),
                _ => (Width: 0, Height: 0)
            };
            if (expectedSize.Width > 0)
                Check(thumbnails.All(t => t.ImageWidth == (int)Math.Ceiling(expectedSize.Width * dpi.DpiScaleX) &&
                    t.ImageHeight == (int)Math.Ceiling(expectedSize.Height * dpi.DpiScaleY)),
                    $"{name}: requested pixels match the thumbnail slot at the current DPI");
            var scroll = (IScrollInfo)panel;
            scroll.SetVerticalOffset(scroll.ExtentHeight);
            Layout(host);
            Check(Descendants(control).OfType<TextBlock>().Any(t => t.Text.EndsWith(" 9999")), $"{name}: scrolling renders the final title");
            if (name == "ChannelsGridView")
            {
                var card = Descendants(control).OfType<Border>().First(b => b.Triggers.Count > 0);
                card.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                    { RoutedEvent = UIElement.MouseEnterEvent });
                Check(Descendants(control).OfType<Button>().Any(b => b.Name == "FavoriteButton") &&
                    Descendants(control).OfType<Button>().Any(b => b.Name == "RecordingButton"), "Recycled channel cards retain hover-action targets");
            }
        }
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject parent) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(parent)).Select(i => VisualTreeHelper.GetChild(parent, i));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in Children(parent))
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
    }

    private sealed class Sizing : INotifyPropertyChanged
    {
        public double TileWidth { get; private set; } = 160;
        public double TileHeight => 120;
        public double VodTileHeight => 200;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Resize() { TileWidth = 240; PropertyChanged?.Invoke(this, new(nameof(TileWidth))); }
    }
}
