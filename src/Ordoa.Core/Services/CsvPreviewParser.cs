using System.Text;

namespace Ordoa.Core.Services;

/// <summary>RFC 4180-style CSV/TSV reader with bounded output for previews.</summary>
public static class CsvPreviewParser
{
    private const int MaxRows = 200;
    private const int MaxColumns = 64;
    private const int MaxCellLength = 2048;

    public static CsvTable Parse(string text, string extension)
    {
        var delimiter = extension.Equals("tsv", StringComparison.OrdinalIgnoreCase)
            ? '\t'
            : DetectDelimiter(text);
        var rows = ReadRows(text, delimiter);
        var totalRows = rows.Count;
        var totalColumns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var hasHeader = rows.Count > 1 && LooksLikeHeader(rows[0], rows[1]);
        var headers = hasHeader
            ? rows[0].Take(MaxColumns).Select((value, index) =>
                string.IsNullOrWhiteSpace(value) ? $"#{index + 1}" : value).ToList()
            : Enumerable.Range(1, Math.Min(totalColumns, MaxColumns)).Select(index => $"#{index}").ToList();
        var body = hasHeader ? rows.Skip(1) : rows;

        var resultRows = new List<IReadOnlyList<string>>();
        foreach (var row in body.Take(MaxRows))
        {
            var cells = new List<string>(Math.Min(row.Count, MaxColumns));
            for (var i = 0; i < Math.Min(row.Count, MaxColumns); i++)
                cells.Add(TrimCell(row[i]));
            resultRows.Add(cells);
        }

        return new CsvTable(headers, resultRows, hasHeader, totalRows, totalColumns,
            delimiter == '\t' ? "TAB" : delimiter.ToString());
    }

    private static char DetectDelimiter(string text)
    {
        var sample = text.Length > 8192 ? text[..8192] : text;
        var candidates = new[] { ',', '\t', ';', '|' };
        var best = ',';
        var bestScore = -1;
        foreach (var candidate in candidates)
        {
            var lines = sample.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(8).ToList();
            if (lines.Count == 0)
                continue;

            var counts = lines.Select(line => CountOutsideQuotes(line, candidate)).ToList();
            var score = counts.Count == 0 ? 0 : counts.Min();
            if (counts.Max() == counts.Min() && score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best : ',';
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var inQuotes = false;
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && line[i] == delimiter)
            {
                count++;
            }
        }
        return count;
    }

    private static List<List<string>> ReadRows(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    AppendCell(cell, character);
                }
                continue;
            }

            if (character == '"')
            {
                inQuotes = true;
                continue;
            }

            if (character == delimiter)
            {
                row.Add(TrimCell(cell.ToString()));
                cell.Clear();
                if (row.Count >= MaxColumns)
                {
                    // Keep consuming the row so the parser stays in sync, but
                    // do not grow memory for very wide files.
                }
                continue;
            }

            if (character is '\r' or '\n')
            {
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(TrimCell(cell.ToString()));
                cell.Clear();
                if (row.Count > 0 && !(row.Count == 1 && row[0].Length == 0))
                    rows.Add(row);
                row = [];
                if (rows.Count >= MaxRows + 1)
                    break;
                continue;
            }

            AppendCell(cell, character);
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(TrimCell(cell.ToString()));
            if (row.Count > 0)
                rows.Add(row);
        }

        return rows;
    }

    private static void AppendCell(StringBuilder cell, char character)
    {
        if (cell.Length < MaxCellLength)
            cell.Append(character);
    }

    private static string TrimCell(string value)
    {
        value = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length > 240 ? value[..240] + "…" : value;
    }

    private static bool LooksLikeHeader(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first.Count < 2)
            return false;

        var numericFirst = first.Count(value => double.TryParse(value, out _));
        var numericSecond = second.Count(value => double.TryParse(value, out _));
        if (numericFirst == 0 && numericSecond > 0)
            return true;

        return first.All(value => value.Length <= 80 && !value.Contains('\n'));
    }
}
