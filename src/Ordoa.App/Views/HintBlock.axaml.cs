using Avalonia;
using Avalonia.Controls;

namespace Ordoa.App.Views;

/// <summary>
/// Explanatory hint that can collapse to an info icon with the full text in a
/// tooltip. The expanded state renders exactly like the former field-hint text.
/// </summary>
public partial class HintBlock : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HintBlock, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsCollapsedProperty =
        AvaloniaProperty.Register<HintBlock, bool>(nameof(IsCollapsed));

    public HintBlock()
    {
        InitializeComponent();
        IsCollapsed = App.Services?.Settings.Current.CollapseHints ?? true;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsCollapsed
    {
        get => GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }
}
