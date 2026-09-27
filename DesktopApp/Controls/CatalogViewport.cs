using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopApp.Controls;

public sealed record CatalogViewportItem(object Item, int Priority);

public static class CatalogViewport
{
    // The virtualizing panels already realize a small nearby buffer. Inspect only
    // those containers, never walk all items in a provider's catalog.
    public static IReadOnlyList<CatalogViewportItem> Read(ItemsControl control)
    {
        if (!control.IsVisible) return [];
        var scroll = Find<ScrollViewer>(control);
        var presenter = scroll == null ? null : Find<ScrollContentPresenter>(scroll);
        var itemsPresenter = presenter == null ? null : Find<ItemsPresenter>(presenter);
        if (presenter == null || itemsPresenter == null || VisualTreeHelper.GetChildrenCount(itemsPresenter) == 0) return [];
        var panel = VisualTreeHelper.GetChild(itemsPresenter, 0);
        var viewport = new Rect(presenter.RenderSize);
        var nearby = new Rect(0, -viewport.Height, viewport.Width, viewport.Height * 3);
        var result = new List<CatalogViewportItem>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is not FrameworkElement container) continue;
            var bounds = container.TransformToAncestor(presenter).TransformBounds(new Rect(container.RenderSize));
            if (!bounds.IntersectsWith(nearby)) continue; // e.g. an offscreen focused item
            var item = control.ItemContainerGenerator.ItemFromContainer(container);
            if (item == DependencyProperty.UnsetValue) continue;
            result.Add(new(item, bounds.IntersectsWith(viewport) ? 1 : 2));
        }
        return result.OrderBy(r => r.Priority).ToList();
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
