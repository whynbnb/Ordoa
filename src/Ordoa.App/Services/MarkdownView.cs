using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>Renders the UI-neutral Markdown block model with native Avalonia controls.</summary>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<MarkdownBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownBlock>?>(nameof(Blocks));

    private readonly StackPanel _root = new() { Spacing = 8 };

    public MarkdownView()
    {
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 10),
            Content = _root,
        };
    }

    public IReadOnlyList<MarkdownBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    static MarkdownView()
    {
        BlocksProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());
    }

    private void Render()
    {
        _root.Children.Clear();
        if (Blocks is { Count: > 0 } blocks)
            AddBlocks(_root, blocks, depth: 0);
    }

    private void AddBlocks(Panel target, IReadOnlyList<MarkdownBlock> blocks, int depth)
    {
        foreach (var block in blocks)
            AddBlock(target, block, depth);
    }

    private void AddBlock(Panel target, MarkdownBlock block, int depth)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
                target.Children.Add(TextBlockFor(block.Inlines, HeadingSize(block.Level), bold: true,
                    margin: new Thickness(0, block.Level <= 2 ? 9 : 5, 0, 1)));
                break;

            case MarkdownBlockKind.Paragraph:
                if (block.Inlines is { Count: > 0 } inlines)
                    target.Children.Add(TextBlockFor(inlines, 12.5, bold: false, margin: new Thickness(0, 1, 0, 2)));
                break;

            case MarkdownBlockKind.List:
                var list = new StackPanel { Spacing = 3, Margin = new Thickness(4, 1, 0, 2) };
                var number = block.Start;
                foreach (var item in block.Children ?? [])
                {
                    var marker = block.Ordered ? $"{number++}." : "•";
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("22,*"),
                        Margin = new Thickness(0, 0, 0, 1),
                    };
                    row.Children.Add(new TextBlock
                    {
                        Text = marker,
                        FontSize = 12.5,
                        Foreground = ThemeService.Brush("TextTertiaryBrush", "#8B958F"),
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 1, 0, 0),
                    });
                    var content = new StackPanel { Spacing = 4 };
                    AddBlocks(content, item.Children ?? [], depth + 1);
                    Grid.SetColumn(content, 1);
                    row.Children.Add(content);
                    list.Children.Add(row);
                }
                target.Children.Add(list);
                break;

            case MarkdownBlockKind.Quote:
                var quote = new StackPanel { Spacing = 5 };
                AddBlocks(quote, block.Children ?? [], depth + 1);
                target.Children.Add(new Border
                {
                    BorderBrush = ThemeService.Brush("AccentBrush", "#17937F"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(9, 3, 4, 3),
                    Margin = new Thickness(0, 3, 0, 3),
                    Child = quote,
                });
                break;

            case MarkdownBlockKind.Code:
                var codeText = block.Text ?? string.Empty;
                var extension = LanguageExtension(block.Language);
                var code = string.IsNullOrWhiteSpace(extension)
                    ? null
                    : SyntaxHighlighter.Highlight(codeText, extension);
                if (code is not null)
                {
                    target.Children.Add(CodeBlockBuilder.Build(code, lineNumbers: false));
                }
                else
                {
                    target.Children.Add(new Border
                    {
                        Background = ThemeService.Brush("SurfaceAltBrush", "#F0F2F1"),
                        CornerRadius = new CornerRadius(7),
                        Padding = new Thickness(10, 8),
                        Child = new SelectableTextBlock
                        {
                            Text = codeText.TrimEnd(),
                            FontFamily = Application.Current?.FindResource("MonoFontFamily") as FontFamily ??
                                         new FontFamily("monospace"),
                            FontSize = 11.5,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    });
                }
                if (!string.IsNullOrWhiteSpace(block.Language))
                {
                    target.Children.Add(new TextBlock
                    {
                        Text = block.Language.Trim(),
                        FontSize = 9.5,
                        Foreground = ThemeService.Brush("TextTertiaryBrush", "#8B958F"),
                        Margin = new Thickness(2, -5, 0, 0),
                    });
                }
                break;

            case MarkdownBlockKind.Table when block.Table is { } table:
                target.Children.Add(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = PreviewTableBuilder.Build(table),
                    Margin = new Thickness(0, 4, 0, 5),
                });
                break;

            case MarkdownBlockKind.Divider:
                target.Children.Add(new Border
                {
                    Height = 1,
                    Background = ThemeService.Brush("BorderBrush", "#E3E7E4"),
                    Margin = new Thickness(0, 7, 0, 7),
                });
                break;

            default:
                if (block.Children is { Count: > 0 })
                    AddBlocks(target, block.Children, depth + 1);
                break;
        }
    }

    private static SelectableTextBlock TextBlockFor(IReadOnlyList<MarkdownInline>? inlines, double fontSize,
        bool bold, Thickness margin)
    {
        var block = new SelectableTextBlock
        {
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (inlines is not { Count: > 0 })
            return block;

        var collection = new InlineCollection();
        foreach (var inline in inlines)
        {
            var pieces = inline.Text.Split('\n');
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0)
                    collection.Add(new LineBreak());
                if (pieces[i].Length == 0)
                    continue;

                var run = new Run(pieces[i])
                {
                    FontWeight = inline.Bold || bold ? FontWeight.SemiBold : FontWeight.Normal,
                    FontStyle = inline.Italic ? FontStyle.Italic : FontStyle.Normal,
                    Foreground = inline.Link is not null
                        ? ThemeService.Brush("AccentBrush", "#17937F")
                        : ThemeService.Brush("TextPrimaryBrush", "#1C2320"),
                };
                if (inline.Code)
                {
                    run.FontFamily = Application.Current?.FindResource("MonoFontFamily") as FontFamily ??
                                     new FontFamily("monospace");
                    run.FontSize = Math.Max(10, fontSize - 1);
                }
                if (inline.Strike)
                    run.TextDecorations = TextDecorations.Strikethrough;
                collection.Add(run);
            }
        }
        block.Inlines = collection;
        return block;
    }

    private static double HeadingSize(int level) => level switch
    {
        1 => 21,
        2 => 18,
        3 => 16,
        4 => 14.5,
        5 => 13.5,
        _ => 13,
    };

    private static string LanguageExtension(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return string.Empty;

        return language.Trim().ToLowerInvariant() switch
        {
            "csharp" or "c#" or "cs" => "cs",
            "javascript" or "js" => "js",
            "typescript" or "ts" => "ts",
            "python" or "py" => "py",
            "bash" or "sh" or "shell" or "zsh" => "sh",
            "json" => "json",
            "yaml" or "yml" => "yaml",
            "xml" or "html" or "xaml" => "xml",
            "css" or "scss" => "css",
            "sql" => "sql",
            "go" => "go",
            "rust" or "rs" => "rs",
            "java" or "kotlin" or "kt" => "java",
            "c" or "cpp" or "c++" or "cc" => "cpp",
            "php" => "php",
            "ruby" or "rb" => "rb",
            _ => language.Trim(),
        };
    }
}
