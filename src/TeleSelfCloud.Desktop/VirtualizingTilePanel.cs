using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TeleSelfCloud.Desktop;

/// <summary>Fixed-size Explorer tiles: only viewport rows (plus one buffer row) are realized.</summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    private const double TileWidth = 180;
    private const double TileHeight = 226;
    private int _columns = 1;
    private Size _extent;
    private Size _viewport;
    private double _offset;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new Size();
        var width = double.IsInfinity(availableSize.Width) ? TileWidth : Math.Max(0, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height) ? TileHeight : Math.Max(0, availableSize.Height);
        _columns = Math.Max(1, (int)(width / TileWidth));
        _viewport = new Size(width, height);
        _extent = new Size(width, Math.Ceiling((double)owner.Items.Count / _columns) * TileHeight);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent.Height - height));
        ScrollOwner?.InvalidateScrollInfo();

        var first = Math.Min(owner.Items.Count, (int)(_offset / TileHeight) * _columns);
        var last = Math.Min(owner.Items.Count - 1, ((int)((_offset + height) / TileHeight) + 2) * _columns - 1);
        // Accessing InternalChildren connects this items host to its generator on first layout.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;
        // Remove from the back so generator positions remain valid; recycling is essential here.
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= first && itemIndex <= last) continue;
            if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1);
            else generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
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
                    // A panel switch can return a generated container which has not yet been
                    // attached to this new items host. It still needs a visual parent.
                    if (newlyRealized || childIndex >= InternalChildren.Count || InternalChildren[childIndex] != child)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(width / _columns, TileHeight));
                }
            }
        }
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(index, 0));
            InternalChildren[index].Arrange(new Rect(itemIndex % _columns * finalSize.Width / _columns,
                itemIndex / _columns * TileHeight - _offset, finalSize.Width / _columns, TileHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // A source reset invalidates generator positions, including previously recycled containers.
        if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            RemoveInternalChildRange(0, InternalChildren.Count);
        else if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove or
                 System.Collections.Specialized.NotifyCollectionChangedAction.Replace or
                 System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(Math.Max(0, args.Position.Index), args.ItemUICount);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        var top = index / _columns * TileHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + TileHeight > _offset + _viewport.Height) SetVerticalOffset(top + TileHeight - _viewport.Height);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = visual as DependencyObject;
        while (child is not null && VisualTreeHelper.GetParent(child) != this)
            child = VisualTreeHelper.GetParent(child);
        if (child is UIElement element)
        {
            var index = InternalChildren.IndexOf(element);
            if (index >= 0) BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(index, 0)));
        }
        return rectangle;
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        var value = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(value - _offset) < 0.1) return;
        _offset = value;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(_offset - TileHeight);
    public void LineDown() => SetVerticalOffset(_offset + TileHeight);
    public void MouseWheelUp() => SetVerticalOffset(_offset - TileHeight);
    public void MouseWheelDown() => SetVerticalOffset(_offset + TileHeight);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
