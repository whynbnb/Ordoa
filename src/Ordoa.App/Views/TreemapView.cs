using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Ordoa.App.ViewModels.Dialogs;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Views;

public sealed class TreemapEntryEventArgs : EventArgs
{
    public TreemapEntryEventArgs(TreemapEntry entry) => Entry = entry;
    public TreemapEntry Entry { get; }
}

/// <summary>Fast custom-drawn squarified treemap.</summary>
public sealed class TreemapView : Control
{
    private static readonly IBrush BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
    private static readonly IPen BorderPen = new Pen(BorderBrush, 1);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Colors.White), 2);
    private static readonly Typeface LabelTypeface =
        new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface ValueTypeface =
        new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    private readonly List<(TreemapEntry Entry, Rect Rect)> _hits = [];
    private string? _hoveredId;

    public static readonly StyledProperty<IReadOnlyList<TreemapEntry>?> ItemsProperty =
        AvaloniaProperty.Register<TreemapView, IReadOnlyList<TreemapEntry>?>(nameof(Items));

    static TreemapView()
    {
        AffectsRender<TreemapView>(ItemsProperty);
    }

    public TreemapView()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<TreemapEntry>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public event EventHandler<TreemapEntryEventArgs>? EntryActivated;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _hits.Clear();

        var entries = Items;
        if (entries is null || entries.Count == 0 || Bounds.Width <= 8 || Bounds.Height <= 8)
            return;

        var colorIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
            colorIndex[entries[index].Id] = index;

        const double padding = 4;
        var width = Bounds.Width - padding * 2;
        var height = Bounds.Height - padding * 2;
        var tiles = TreemapLayout.Create(entries, width, height);
        foreach (var tile in tiles)
        {
            var rect = new Rect(
                padding + tile.Rect.X,
                padding + tile.Rect.Y,
                Math.Max(0, tile.Rect.Width),
                Math.Max(0, tile.Rect.Height));
            if (rect.Width < 1 || rect.Height < 1)
                continue;

            var index = colorIndex.GetValueOrDefault(tile.Entry.Id);
            var fill = new Rect(rect.X + 1, rect.Y + 1,
                Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 2));
            context.DrawRectangle(AnalysisPalette.Brush(index), BorderPen, fill, 3, 3);

            if (rect.Width > 44 && rect.Height > 22)
            {
                var textBrush = AnalysisPalette.TextBrush(index);
                using (context.PushClip(new Rect(rect.X + 5, rect.Y + 4,
                           Math.Max(0, rect.Width - 10), Math.Max(0, rect.Height - 8))))
                {
                    var label = CreateText(tile.Entry.Label, 11.5, LabelTypeface, textBrush,
                        Math.Max(1, rect.Width - 10));
                    context.DrawText(label, new Point(rect.X + 6, rect.Y + 5));

                    if (rect.Height > 38)
                    {
                        var value = CreateText(Humanize.Size((long)tile.Entry.Value), 10,
                            ValueTypeface, textBrush, Math.Max(1, rect.Width - 10));
                        context.DrawText(value, new Point(rect.X + 6, rect.Y + 22));
                    }
                }
            }

            if (rect.Width > 6 && rect.Height > 6)
                _hits.Add((tile.Entry, rect));

            if (string.Equals(_hoveredId, tile.Entry.Id, StringComparison.Ordinal))
                context.DrawRectangle(null, HoverPen, fill, 3, 3);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var entry = HitTest(e.GetPosition(this));
            if (entry is { IsDirectory: true, IsAggregate: false })
            {
                EntryActivated?.Invoke(this, new TreemapEntryEventArgs(entry));
                e.Handled = true;
            }
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var entry = HitTest(e.GetPosition(this));
        var id = entry?.Id;
        if (!string.Equals(id, _hoveredId, StringComparison.Ordinal))
        {
            _hoveredId = id;
            Cursor = entry is { IsDirectory: true, IsAggregate: false }
                ? HandCursor
                : ArrowCursor;
            ToolTip.SetTip(this, entry is null
                ? null
                : $"{entry.Label}\n{Humanize.Size((long)entry.Value)}");
            InvalidateVisual();
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoveredId is not null)
        {
            _hoveredId = null;
            Cursor = ArrowCursor;
            ToolTip.SetTip(this, null);
            InvalidateVisual();
        }

        base.OnPointerExited(e);
    }

    private TreemapEntry? HitTest(Point point)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Rect.Contains(point))
                return _hits[i].Entry;
        }

        return null;
    }

    private static FormattedText CreateText(string text, double size, Typeface typeface,
        IBrush brush, double maxWidth)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
}
