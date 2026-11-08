using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class PreviewFormatTests
{
    [Fact]
    public void MarkdownParser_ProducesHeadingsListsCodeAndTables()
    {
        const string markdown = """
            # Title

            Paragraph with **bold** and `code`.

            - one
            - two

            ```cs
            var value = 42;
            ```

            | Name | Size |
            | --- | ---: |
            | a.txt | 12 |
            """;

        var blocks = MarkdownPreviewParser.Parse(markdown);

        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Heading && block.Level == 1);
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.List &&
                                         block.Children is { Count: 2 });
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Code &&
                                         block.Language == "cs" &&
                                         block.Text!.Contains("var value"));
        var table = Assert.Single(blocks, block => block.Kind == MarkdownBlockKind.Table).Table;
        Assert.NotNull(table);
        Assert.Equal(["Name", "Size"], table!.Headers);
        Assert.Single(table.Rows);
        Assert.Equal(["a.txt", "12"], table.Rows[0]);
    }

    [Fact]
    public void MarkdownParser_KeepsInlineFormatting()
    {
        var blocks = MarkdownPreviewParser.Parse("**bold** and *italic* and [link](https://example.com)");
        var paragraph = Assert.Single(blocks);
        Assert.Contains(paragraph.Inlines!, inline => inline.Bold && inline.Text == "bold");
        Assert.Contains(paragraph.Inlines!, inline => inline.Italic && inline.Text == "italic");
        Assert.Contains(paragraph.Inlines!, inline => inline.Link == "https://example.com");
    }

    [Fact]
    public void CsvParser_HandlesQuotesAndDetectsHeader()
    {
        const string csv = """
            name,note,size
            "a, b",hello,10
            c,"line 1
            line 2",20
            """;

        var table = CsvPreviewParser.Parse(csv, "csv");

        Assert.True(table.HasHeader);
        Assert.Equal(["name", "note", "size"], table.Headers);
        Assert.Equal("a, b", table.Rows[0][0]);
        Assert.Equal("line 1 line 2", table.Rows[1][1]);
        Assert.Equal(3, table.TotalColumns);
    }

    [Fact]
    public void CsvParser_UsesTabsForTsv()
    {
        var table = CsvPreviewParser.Parse("a\tb\n1\t2", "tsv");
        Assert.Equal("TAB", table.DelimiterName);
        Assert.Equal(["a", "b"], table.Headers);
        Assert.Equal("2", table.Rows[0][1]);
    }

    [Fact]
    public void SyntaxHighlighter_PreservesTextAndFindsTokens()
    {
        var code = SyntaxHighlighter.Highlight("public int Value = 42; // note", "cs");
        var line = Assert.Single(code.Lines);
        Assert.Equal("public int Value = 42; // note", line.Raw);
        Assert.Contains(line.Spans, span => span.Kind == CodeTokenKind.Keyword && span.Text == "public");
        Assert.Contains(line.Spans, span => span.Kind == CodeTokenKind.Number && span.Text == "42");
        Assert.Contains(line.Spans, span => span.Kind == CodeTokenKind.Comment);
        Assert.Equal("C#", code.Language);
    }

    [Fact]
    public void SyntaxHighlighter_HandlesBlockCommentsAcrossLines()
    {
        var code = SyntaxHighlighter.Highlight("/* first\nsecond */ int x;", "js");
        Assert.Equal(CodeTokenKind.Comment, code.Lines[0].Spans.Last().Kind);
        Assert.Contains(code.Lines[1].Spans, span => span.Kind == CodeTokenKind.Comment);
        Assert.Contains(code.Lines[1].Spans, span => span.Kind == CodeTokenKind.Type);
    }
}
