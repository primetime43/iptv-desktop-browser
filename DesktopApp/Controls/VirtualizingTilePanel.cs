using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DesktopApp.Controls;

// Uniform cards with a single row of overscan above/below the viewport.
// ItemWidth/Height describe the card; ItemSpacing includes its two margins.
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(200d, FrameworkPropertyMetadataOptions.AffectsMeasure), IsPositive);
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(160d, FrameworkPropertyMetadataOptions.AffectsMeasure), IsPositive);
    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double number && double.IsFinite(number) && number >= 0);

    private static bool IsPositive(object value) => value is double number && double.IsFinite(number) && number > 0;
    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public double ItemSpacing { get => (double)GetValue(ItemSpacingProperty); set => SetValue(ItemSpacingProperty, value); }
    private double CellWidth => ItemWidth + ItemSpacing;
    private double CellHeight => ItemHeight + ItemSpacing;
    private int _columns = 1;
    private Size _extent;
    private Size _viewport;
    private double _offset;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null) return new Size();
        // The ItemsControl template supplies a bounded viewport, never an outer
        // ScrollViewer measuring the items with infinite height.
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Max(0, ScrollOwner?.ActualWidth ?? 0);
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : Math.Max(0, ScrollOwner?.ActualHeight ?? 0);
        _viewport = new Size(width, height);
        _columns = Math.Max(1, (int)Math.Floor(width / CellWidth));
        var count = owner.Items.Count;
        _extent = new Size(width, Math.Ceiling((double)count / _columns) * CellHeight);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        ScrollOwner?.InvalidateScrollInfo();

        var first = Math.Max(0, (int)Math.Floor(_offset / CellHeight) - 1) * _columns;
        var last = Math.Min(count - 1, ((int)Math.Ceiling((_offset + height) / CellHeight) + 1) * _columns - 1);
        if (height <= 0 || width <= 0) last = -1;
        // Accessing InternalChildren connects a newly created items host to its generator.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;

        // Recycle before generating so a large scrollbar jump reuses containers.
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            // Keep a focused action button attached to its original item until
            // focus moves away; recycling it would make keyboard input target a new item.
            if ((index >= first && index <= last) || InternalChildren[i].IsKeyboardFocusWithin) continue;
            if (generator is IRecyclingItemContainerGenerator recycling)
                recycling.Recycle(position, 1);
            else
                generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }

        if (last >= first)
        {
            var position = generator.GeneratorPositionFromIndex(first);
            var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
            {
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var newlyRealized);
                    // GenerateNext also returns false for a recycled container;
                    // those still need to rejoin the visual tree and be prepared.
                    if (newlyRealized || childIndex >= InternalChildren.Count ||
                        !ReferenceEquals(InternalChildren[childIndex], child))
                    {
                        InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(CellWidth, CellHeight));
                }
            }
        }
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect(index % _columns * CellWidth,
                index / _columns * CellHeight - _offset, CellWidth, CellHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
                _offset = 0;
                break;
        }
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null || index < 0 || index >= owner.Items.Count) return;
        var top = index / _columns * CellHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + CellHeight > _offset + ViewportHeight)
            SetVerticalOffset(top + CellHeight - ViewportHeight);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual == this || !IsAncestorOf(visual) || rectangle.IsEmpty) return Rect.Empty;
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        var previousOffset = _offset;
        if (bounds.Top < 0) SetVerticalOffset(_offset + bounds.Top);
        else if (bounds.Bottom > ViewportHeight) SetVerticalOffset(_offset + bounds.Bottom - ViewportHeight);
        bounds.Offset(0, previousOffset - _offset);
        bounds.Intersect(new Rect(_viewport));
        return bounds;
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        var next = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (_offset == next) return;
        _offset = next;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double VerticalOffset => _offset;
    public double HorizontalOffset => 0;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(_offset - 24);
    public void LineDown() => SetVerticalOffset(_offset + 24);
    public void PageUp() => SetVerticalOffset(_offset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(_offset + ViewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(_offset - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(_offset + WheelStep);
    private double WheelStep => SystemParameters.WheelScrollLines < 0 ? ViewportHeight : SystemParameters.WheelScrollLines * 24;
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
}
