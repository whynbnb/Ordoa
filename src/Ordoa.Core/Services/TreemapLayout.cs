using Ordoa.Core.Models;

namespace Ordoa.Core.Services;

/// <summary>
/// Squarified treemap layout. Tiles keep their relative area, which makes large
/// folders easy to spot while keeping the rectangles close to square.
/// </summary>
public static class TreemapLayout
{
    public static IReadOnlyList<TreemapTile> Create(IReadOnlyList<TreemapEntry>? entries,
        double width, double height)
    {
        if (entries is null || entries.Count == 0 || width <= 0 || height <= 0)
            return [];

        var items = entries
            .Where(entry => entry.Value > 0 && !double.IsNaN(entry.Value) && !double.IsInfinity(entry.Value))
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (items.Count == 0)
            return [];

        var total = items.Sum(entry => entry.Value);
        if (total <= 0 || double.IsNaN(total) || double.IsInfinity(total))
            return [];

        var area = width * height;
        var scaled = items
            .Select(entry => new ScaledEntry(entry, entry.Value / total * area))
            .ToList();

        var result = new List<TreemapTile>(scaled.Count);
        Squarify(scaled, new TreemapRect(0, 0, width, height), result);
        return result;
    }

    private static void Squarify(List<ScaledEntry> items, TreemapRect free,
        List<TreemapTile> result)
    {
        var row = new List<ScaledEntry>();
        var index = 0;
        while (index < items.Count && free.Width > 0 && free.Height > 0)
        {
            var item = items[index];
            var side = Math.Min(free.Width, free.Height);
            if (row.Count == 0)
            {
                row.Add(item);
                index++;
                continue;
            }

            var currentWorst = Worst(row, side);
            row.Add(item);
            var nextWorst = Worst(row, side);
            if (nextWorst > currentWorst)
            {
                row.RemoveAt(row.Count - 1);
                free = LayoutRow(row, free, result);
                row.Clear();
            }
            else
            {
                index++;
            }
        }

        if (row.Count > 0 && free.Width > 0 && free.Height > 0)
            LayoutRow(row, free, result);
    }

    private static double Worst(IReadOnlyList<ScaledEntry> row, double side)
    {
        if (row.Count == 0 || side <= 0)
            return double.PositiveInfinity;

        var sum = row.Sum(item => item.Area);
        if (sum <= 0)
            return double.PositiveInfinity;

        var max = row.Max(item => item.Area);
        var min = row.Min(item => item.Area);
        if (min <= 0)
            return double.PositiveInfinity;

        var sideSquared = side * side;
        return Math.Max(sideSquared * max / (sum * sum), sum * sum / (sideSquared * min));
    }

    private static TreemapRect LayoutRow(IReadOnlyList<ScaledEntry> row, TreemapRect free,
        List<TreemapTile> result)
    {
        var sum = row.Sum(item => item.Area);
        if (sum <= 0 || free.Width <= 0 || free.Height <= 0)
            return free;

        if (free.Width >= free.Height)
        {
            var thickness = Math.Min(free.Width, sum / free.Height);
            if (thickness <= 0)
                return free;

            var y = free.Y;
            var bottom = free.Y + free.Height;
            for (var i = 0; i < row.Count; i++)
            {
                var height = i == row.Count - 1
                    ? Math.Max(0, bottom - y)
                    : Math.Min(bottom - y, row[i].Area / thickness);
                result.Add(new TreemapTile(row[i].Entry,
                    new TreemapRect(free.X, y, thickness, height)));
                y += height;
            }

            return new TreemapRect(free.X + thickness, free.Y,
                Math.Max(0, free.Width - thickness), free.Height);
        }
        else
        {
            var thickness = Math.Min(free.Height, sum / free.Width);
            if (thickness <= 0)
                return free;

            var x = free.X;
            var right = free.X + free.Width;
            for (var i = 0; i < row.Count; i++)
            {
                var width = i == row.Count - 1
                    ? Math.Max(0, right - x)
                    : Math.Min(right - x, row[i].Area / thickness);
                result.Add(new TreemapTile(row[i].Entry,
                    new TreemapRect(x, free.Y, width, thickness)));
                x += width;
            }

            return new TreemapRect(free.X, free.Y + thickness,
                free.Width, Math.Max(0, free.Height - thickness));
        }
    }

    private sealed record ScaledEntry(TreemapEntry Entry, double Area);
}
