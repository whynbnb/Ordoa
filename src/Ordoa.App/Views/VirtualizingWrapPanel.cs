using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Generators;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace Ordoa.App.Views;

/// <summary>
/// A minimal virtualizing wrap panel used by the grid view. Only rows inside
/// the effective viewport (plus a small buffer) are realized, so folders with
/// tens of thousands of entries stay responsive.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemWidth), 118);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemHeight), 108);

    private readonly Dictionary<int, Control> _realized = [];
    private Rect _viewport;
    private int _columns = 1;

    static VirtualizingWrapPanel()
    {
        AffectsMeasure<VirtualizingWrapPanel>(ItemWidthProperty, ItemHeightProperty);
        AffectsArrange<VirtualizingWrapPanel>(ItemWidthProperty, ItemHeightProperty);
    }

    public VirtualizingWrapPanel()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            if (_viewport != e.EffectiveViewport)
            {
                _viewport = e.EffectiveViewport;
                InvalidateMeasure();
            }
        };
    }

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Items.Count;
        if (count == 0)
        {
            ClearRealized();
            return default;
        }

        var itemWidth = Math.Max(1, ItemWidth);
        var itemHeight = Math.Max(1, ItemHeight);
        var width = double.IsInfinity(availableSize.Width)
            ? itemWidth
            : Math.Max(1, availableSize.Width);
        _columns = Math.Max(1, (int)Math.Floor(width / itemWidth));
        var rows = (count + _columns - 1) / _columns;

        var viewportHeight = _viewport.Height;
        int firstRow, lastRow;
        if (viewportHeight > 0)
        {
            firstRow = Math.Max(0, (int)Math.Floor(_viewport.Top / itemHeight) - 2);
            lastRow = Math.Min(rows - 1, (int)Math.Ceiling(_viewport.Bottom / itemHeight) + 2);
        }
        else
        {
            firstRow = 0;
            lastRow = Math.Min(rows - 1, 9);
        }

        var first = firstRow * _columns;
        var last = Math.Min(count - 1, (lastRow + 1) * _columns - 1);

        foreach (var index in _realized.Keys.Where(index => index < first || index > last).ToList())
            Recycle(index);

        for (var index = first; index <= last; index++)
        {
            var container = GetOrCreate(index);
            container.Measure(new Size(itemWidth, itemHeight));
            if (container.DesiredSize.Width > width)
                container.Measure(new Size(width, itemHeight));
        }

        return new Size(width, rows * itemHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = Math.Max(1, ItemWidth);
        var itemHeight = Math.Max(1, ItemHeight);
        _columns = Math.Max(1, (int)Math.Floor(Math.Max(1, finalSize.Width) / itemWidth));

        foreach (var (index, container) in _realized)
        {
            var row = index / _columns;
            var column = index % _columns;
            container.Arrange(new Rect(column * itemWidth, row * itemHeight, itemWidth, itemHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items,
        NotifyCollectionChangedEventArgs e)
    {
        ClearRealized();
        InvalidateMeasure();
        base.OnItemsChanged(items, e);
    }

    protected override IEnumerable<Control>? GetRealizedContainers()
        => _realized.Values;

    protected override Control? ContainerFromIndex(int index)
        => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, item) in _realized)
        {
            if (ReferenceEquals(item, container))
                return index;
        }
        return -1;
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count)
            return null;

        var container = GetOrCreate(index);
        container.Measure(new Size(Math.Max(1, ItemWidth), Math.Max(1, ItemHeight)));
        container.Arrange(GetRect(index));
        container.BringIntoView();
        return container;
    }

    protected override Control? GetControl(NavigationDirection direction,
        IInputElement? from, bool wrap)
    {
        var count = Items.Count;
        if (count == 0)
            return null;

        var fromControl = from as Control;
        var fromIndex = fromControl is not null ? IndexFromContainer(fromControl) : -1;
        var toIndex = fromIndex < 0 ? 0 : fromIndex;

        switch (direction)
        {
            case NavigationDirection.First:
                toIndex = 0;
                break;
            case NavigationDirection.Last:
                toIndex = count - 1;
                break;
            case NavigationDirection.Next:
                toIndex++;
                break;
            case NavigationDirection.Previous:
                toIndex--;
                break;
            case NavigationDirection.Left:
                toIndex--;
                break;
            case NavigationDirection.Right:
                toIndex++;
                break;
            case NavigationDirection.Up:
                toIndex -= _columns;
                break;
            case NavigationDirection.Down:
                toIndex += _columns;
                break;
            default:
                return null;
        }

        if (wrap)
        {
            if (toIndex < 0)
                toIndex = count - 1;
            else if (toIndex >= count)
                toIndex = 0;
        }

        toIndex = Math.Clamp(toIndex, 0, count - 1);
        return ScrollIntoView(toIndex);
    }

    private Rect GetRect(int index)
    {
        var row = index / _columns;
        var column = index % _columns;
        return new Rect(column * ItemWidth, row * ItemHeight,
            Math.Max(1, ItemWidth), Math.Max(1, ItemHeight));
    }

    private Control GetOrCreate(int index)
    {
        if (_realized.TryGetValue(index, out var existing))
            return existing;

        var item = Items[index];
        var generator = ItemContainerGenerator;
        if (generator is null)
            throw new InvalidOperationException("Item container generator is unavailable.");

        Control container;
        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            container = generator.CreateContainer(item, index, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }
        else
        {
            container = (Control)item!;
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }

        _realized[index] = container;
        return container;
    }

    private void Recycle(int index)
    {
        if (!_realized.TryGetValue(index, out var container) || container.IsKeyboardFocusWithin)
            return;
        _realized.Remove(index);
        ItemContainerGenerator?.ClearItemContainer(container);
        RemoveInternalChild(container);
    }

    private void ClearRealized()
    {
        foreach (var index in _realized.Keys.ToList())
            Recycle(index);
    }
}
