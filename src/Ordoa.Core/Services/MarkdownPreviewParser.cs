using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Ordoa.Core.Services;

public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    Code,
    Quote,
    List,
    ListItem,
    Table,
    Divider,
}

public sealed record MarkdownInline(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strike = false,
    string? Link = null);

public sealed record MarkdownTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    int Level = 0,
    IReadOnlyList<MarkdownInline>? Inlines = null,
    string? Text = null,
    string? Language = null,
    IReadOnlyList<MarkdownBlock>? Children = null,
    bool Ordered = false,
    int Start = 1,
    MarkdownTable? Table = null);

public enum CodeTokenKind
{
    Text,
    Keyword,
    String,
    Number,
    Comment,
    Type,
    Function,
    Operator,
    Punctuation,
    Preprocessor,
}

public sealed record CodeSpan(string Text, CodeTokenKind Kind);

public sealed record CodeLine(int Number, string Raw, IReadOnlyList<CodeSpan> Spans);

public sealed record CodePreview(
    IReadOnlyList<CodeLine> Lines,
    string Language,
    bool Truncated,
    string PlainText);

public sealed record CsvTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool HasHeader,
    int TotalRows,
    int TotalColumns,
    string DelimiterName);

/// <summary>
/// Converts Markdown to a small, UI-neutral block model. Markdig is a native
/// .NET parser (no WebView or browser runtime), and rendering stays in Avalonia
/// controls so previews match the rest of the app.
/// </summary>
public static class MarkdownPreviewParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UseGenericAttributes()
        .Build();

    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var document = Markdown.Parse(markdown, Pipeline);
        var blocks = new List<MarkdownBlock>();
        ConvertBlocks(document, blocks);
        return blocks;
    }

    private static void ConvertBlocks(ContainerBlock container, List<MarkdownBlock> target)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    target.Add(new MarkdownBlock(MarkdownBlockKind.Heading,
                        Level: Math.Clamp(heading.Level, 1, 6),
                        Inlines: ConvertInlines(heading.Inline)));
                    break;

                case ParagraphBlock paragraph:
                    target.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph,
                        Inlines: ConvertInlines(paragraph.Inline)));
                    break;

                case ListBlock list:
                    var items = new List<MarkdownBlock>();
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var children = new List<MarkdownBlock>();
                        ConvertBlocks(item, children);
                        items.Add(new MarkdownBlock(MarkdownBlockKind.ListItem, Children: children));
                    }
                    target.Add(new MarkdownBlock(MarkdownBlockKind.List, Children: items, Ordered: list.IsOrdered,
                        Start: list.OrderedStart is { Length: > 0 } start && int.TryParse(start, out var number)
                            ? number
                            : 1));
                    break;

                case QuoteBlock quote:
                    var quoteChildren = new List<MarkdownBlock>();
                    ConvertBlocks(quote, quoteChildren);
                    target.Add(new MarkdownBlock(MarkdownBlockKind.Quote, Children: quoteChildren));
                    break;

                case FencedCodeBlock fenced:
                    target.Add(new MarkdownBlock(MarkdownBlockKind.Code,
                        Text: fenced.Lines.ToString(),
                        Language: fenced.Info?.Trim()));
                    break;

                case CodeBlock code:
                    target.Add(new MarkdownBlock(MarkdownBlockKind.Code, Text: code.Lines.ToString()));
                    break;

                case Table table:
                    target.Add(new MarkdownBlock(MarkdownBlockKind.Table, Table: ConvertTable(table)));
                    break;

                case ThematicBreakBlock:
                    target.Add(new MarkdownBlock(MarkdownBlockKind.Divider));
                    break;

                case HtmlBlock html:
                    target.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph,
                        Inlines: [new MarkdownInline(StripHtml(html.Lines.ToString()))]));
                    break;

                default:
                    if (block is LeafBlock leaf && leaf.Lines.Count > 0)
                    {
                        var text = leaf.Lines.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                            target.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph,
                                Inlines: [new MarkdownInline(text)]));
                    }
                    break;
            }
        }
    }

    private static MarkdownTable ConvertTable(Table table)
    {
        var headers = new List<string>();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.OfType<TableCell>())
            {
                var builder = new StringBuilder();
                foreach (var block in cell)
                {
                    if (block is ParagraphBlock paragraph && paragraph.Inline is not null)
                    {
                        foreach (var inline in paragraph.Inline)
                            AppendInlineText(builder, inline);
                    }
                    else if (block is LeafBlock leaf)
                    {
                        builder.Append(leaf.Lines.ToString());
                    }
                }
                cells.Add(builder.ToString().Trim());
            }

            if (row.IsHeader && headers.Count == 0)
                headers.AddRange(cells);
            else
                rows.Add(cells);
        }

        if (headers.Count == 0 && rows.Count > 0)
        {
            headers.AddRange(rows[0].Select((_, index) => $"#{index + 1}"));
        }

        return new MarkdownTable(headers, rows);
    }

    private static IReadOnlyList<MarkdownInline> ConvertInlines(ContainerInline? container)
    {
        var result = new List<MarkdownInline>();
        if (container is null)
            return result;

        foreach (var inline in container)
            AppendInline(result, inline, bold: false, italic: false, strike: false, link: null);

        return result;
    }

    private static void AppendInline(List<MarkdownInline> target, Inline inline, bool bold, bool italic,
        bool strike, string? link)
    {
        switch (inline)
        {
            case LiteralInline literal:
                if (literal.Content.Length > 0)
                    target.Add(new MarkdownInline(literal.Content.ToString(), bold, italic, Strike: strike, Link: link));
                break;

            case EmphasisInline emphasis:
                var isBold = bold || emphasis.DelimiterCount >= 2;
                var isItalic = italic || emphasis.DelimiterCount == 1;
                foreach (var child in emphasis)
                    AppendInline(target, child, isBold, isItalic, strike, link);
                break;

            case CodeInline code:
                target.Add(new MarkdownInline(code.Content, bold, italic, Code: true, Link: link));
                break;

            case LineBreakInline lineBreak:
                target.Add(new MarkdownInline(lineBreak.IsHard ? "\n" : " "));
                break;

            case LinkInline linkInline:
                var href = linkInline.Url;
                if (linkInline.IsImage)
                {
                    target.Add(new MarkdownInline($"[图片: {DescribeLink(linkInline)}]", bold, italic, Link: href));
                    break;
                }
                foreach (var child in linkInline)
                    AppendInline(target, child, bold, italic, strike, href ?? link);
                break;

            case AutolinkInline autoLink:
                target.Add(new MarkdownInline(autoLink.Url, bold, italic, Link: autoLink.Url));
                break;

            case HtmlInline html:
                target.Add(new MarkdownInline(StripHtml(html.Tag), bold, italic, Link: link));
                break;

            case ContainerInline nested:
                foreach (var child in nested)
                    AppendInline(target, child, bold, italic, strike, link);
                break;
        }
    }

    private static void AppendInlineText(StringBuilder builder, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.Append(literal.Content);
                break;
            case CodeInline code:
                builder.Append(code.Content);
                break;
            case LineBreakInline:
                builder.Append(' ');
                break;
            case ContainerInline nested:
                foreach (var child in nested)
                    AppendInlineText(builder, child);
                break;
        }
    }

    private static string DescribeLink(LinkInline link)
    {
        var builder = new StringBuilder();
        foreach (var child in link)
            AppendInlineText(builder, child);
        return builder.Length > 0 ? builder.ToString() : link.Url ?? string.Empty;
    }

    private static string StripHtml(string value)
    {
        var builder = new StringBuilder(value.Length);
        var inTag = false;
        foreach (var character in value)
        {
            if (character == '<')
            {
                inTag = true;
                continue;
            }
            if (character == '>')
            {
                inTag = false;
                continue;
            }
            if (!inTag)
                builder.Append(character);
        }
        return builder.ToString();
    }
}
