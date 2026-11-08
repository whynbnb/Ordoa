using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Documents;

using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>Builds lightweight table controls for Markdown and CSV previews.</summary>
public static class PreviewTableBuilder
{
    public static Control Build(MarkdownTable table)
        => Build(table.Headers, table.Rows, headerRow: true);

    public static Control Build(CsvTable table)
        => Build(table.Headers, table.Rows, headerRow: true);

    private static Control Build(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows,
        bool headerRow)
    {
        var columns = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(row => row.Count));
        if (columns == 0)
            return new TextBlock { Text = "—" };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", columns))),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows.Count + 1))),
        };

        if (headerRow)
        {
            for (var column = 0; column < columns; column++)
            {
                var text = column < headers.Count ? headers[column] : string.Empty;
                grid.Children.Add(Cell(text, row: 0, column, isHeader: true));
            }
        }

        var rowOffset = headerRow ? 1 : 0;
        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var text = column < rows[row].Count ? rows[row][column] : string.Empty;
                grid.Children.Add(Cell(text, row + rowOffset, column, isHeader: false));
            }
        }

        return new Border
        {
            BorderBrush = ThemeService.Brush("BorderBrush", "#E3E7E4"),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(5),
            ClipToBounds = true,
            Child = grid,
        };
    }

    private static Control Cell(string text, int row, int column, bool isHeader)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = isHeader
                ? ThemeService.Brush("TextPrimaryBrush", "#1C2320")
                : ThemeService.Brush("TextSecondaryBrush", "#5E6A64"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MinWidth = 76,
            MaxWidth = 280,
            Margin = new Thickness(8, 4),
        };
        var border = new Border
        {
            Background = isHeader ? ThemeService.Brush("SurfaceAltBrush", "#F0F2F1") : Brushes.Transparent,
            BorderBrush = ThemeService.Brush("BorderBrush", "#E3E7E4"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = block,
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        return border;
    }
}

public static class CodeBlockBuilder
{
    public static Control Build(CodePreview code, bool lineNumbers)
    {
        var text = new SelectableTextBlock
        {
            FontFamily = Application.Current?.FindResource("MonoFontFamily") as FontFamily ??
                         new FontFamily("JetBrains Mono, Noto Sans Mono, monospace"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var inlines = new InlineCollection();
        for (var lineIndex = 0; lineIndex < code.Lines.Count; lineIndex++)
        {
            var line = code.Lines[lineIndex];
            foreach (var span in line.Spans)
            {
                var run = new Run(span.Text)
                {
                    Foreground = TokenBrush(span.Kind),
                    FontWeight = span.Kind == CodeTokenKind.Keyword ? FontWeight.SemiBold : FontWeight.Normal,
                };
                inlines.Add(run);
            }

            if (lineIndex < code.Lines.Count - 1)
                inlines.Add(new LineBreak());
        }
        text.Inlines = inlines;

        if (!lineNumbers)
            return text;

        var numbers = new SelectableTextBlock
        {
            Text = string.Join('\n', code.Lines.Select(line => line.Number)),
            FontFamily = text.FontFamily,
            FontSize = 11,
            Foreground = ThemeService.Brush("TextTertiaryBrush", "#8B958F"),
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        grid.Children.Add(numbers);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Background = ThemeService.Brush("SurfaceAltBrush", "#F0F2F1"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8),
            Child = grid,
        };
    }

    private static IBrush TokenBrush(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => ThemeService.Brush("AccentBrush", "#17937F"),
        CodeTokenKind.String => ThemeService.Brush("SuccessBrush", "#2E8B61"),
        CodeTokenKind.Number => ThemeService.Brush("WarningBrush", "#A9741C"),
        CodeTokenKind.Comment => ThemeService.Brush("TextTertiaryBrush", "#8B958F"),
        CodeTokenKind.Type => ThemeService.Brush("DocumentIconBrush", "#4E84D8"),
        CodeTokenKind.Function => ThemeService.Brush("ImageIconBrush", "#2FAE9B"),
        CodeTokenKind.Operator => ThemeService.Brush("TextSecondaryBrush", "#5E6A64"),
        CodeTokenKind.Punctuation => ThemeService.Brush("TextSecondaryBrush", "#5E6A64"),
        CodeTokenKind.Preprocessor => ThemeService.Brush("ArchiveIconBrush", "#A17BE0"),
        _ => ThemeService.Brush("TextPrimaryBrush", "#1C2320"),
    };
}
