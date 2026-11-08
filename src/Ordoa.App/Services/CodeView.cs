using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>Scrollable syntax-highlighted code preview with line numbers.</summary>
public sealed class CodeView : UserControl
{
    public static readonly StyledProperty<CodePreview?> CodeProperty =
        AvaloniaProperty.Register<CodeView, CodePreview?>(nameof(Code));

    public CodeView()
    {
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10),
        };
    }

    public CodePreview? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    static CodeView()
    {
        CodeProperty.Changed.AddClassHandler<CodeView>((view, _) => view.Render());
    }

    private void Render()
    {
        if (Code is not { Lines.Count: > 0 } code)
        {
            Content = null;
            return;
        }

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10),
            Content = CodeBlockBuilder.Build(code, lineNumbers: true),
        };
    }
}

/// <summary>Scrollable CSV preview table with a sticky-looking header row.</summary>
public sealed class CsvView : UserControl
{
    public static readonly StyledProperty<CsvTable?> TableProperty =
        AvaloniaProperty.Register<CsvView, CsvTable?>(nameof(Table));

    public CsvView()
    {
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10),
        };
    }

    public CsvTable? Table
    {
        get => GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    static CsvView()
    {
        TableProperty.Changed.AddClassHandler<CsvView>((view, _) => view.Render());
    }

    private void Render()
    {
        if (Table is not { Headers.Count: > 0 } table)
        {
            Content = null;
            return;
        }

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{table.TotalRows} × {table.TotalColumns} · {table.DelimiterName}",
            FontSize = 10,
            Foreground = ThemeService.Brush("TextTertiaryBrush", "#8B958F"),
            Margin = new Thickness(1, 0, 0, 2),
        });
        stack.Children.Add(PreviewTableBuilder.Build(table));

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10),
            Content = stack,
        };
    }
}
