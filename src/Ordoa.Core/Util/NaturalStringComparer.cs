namespace Ordoa.Core.Util;

/// <summary>Natural, case-insensitive string ordering ("file2" before "file10").</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var spanX = x.AsSpan(startX, i - startX);
                var spanY = y.AsSpan(startY, j - startY);
                var trimmedX = spanX.TrimStart('0');
                var trimmedY = spanY.TrimStart('0');

                var result = trimmedX.Length.CompareTo(trimmedY.Length);
                if (result != 0)
                    return result;

                result = trimmedX.SequenceCompareTo(trimmedY);
                if (result != 0)
                    return result;

                // Equal numeric value: fewer leading zeros sorts first.
                result = spanX.Length.CompareTo(spanY.Length);
                if (result != 0)
                    return result;
                continue;
            }

            var cx = char.ToUpperInvariant(x[i]);
            var cy = char.ToUpperInvariant(y[j]);
            if (cx != cy)
                return cx.CompareTo(cy);

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
