using Avalonia;
using Avalonia.Controls;

namespace DeployNest.Linux.Controls;

/// <summary>
/// Lays out equally wide items in as many columns as fit (at least <see cref="MinItemWidth"/> each);
/// items in a row share the tallest item's height.
/// </summary>
public sealed class UniformWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<UniformWrapPanel, double>(nameof(MinItemWidth), 300);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<UniformWrapPanel, double>(nameof(Spacing), 12);

    static UniformWrapPanel()
    {
        AffectsMeasure<UniformWrapPanel>(MinItemWidthProperty, SpacingProperty);
    }

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private int Columns(double width) =>
        double.IsInfinity(width) ? 3 : Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));

    private double ItemWidth(double width, int columns) =>
        double.IsInfinity(width) ? MinItemWidth : (width - Spacing * (columns - 1)) / columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var itemWidth = ItemWidth(availableSize.Width, columns);
        double total = 0, rowHeight = 0;
        var index = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++index % columns == 0)
            {
                total += rowHeight + Spacing;
                rowHeight = 0;
            }
        }
        total += rowHeight;
        if (index > 0 && index % columns == 0)
            total -= Spacing;
        var width = double.IsInfinity(availableSize.Width) ? columns * MinItemWidth : availableSize.Width;
        return new Size(width, Math.Max(0, total));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var itemWidth = ItemWidth(finalSize.Width, columns);
        var children = Children.ToList();
        double y = 0;
        for (var row = 0; row * columns < children.Count; row++)
        {
            var rowChildren = children.Skip(row * columns).Take(columns).ToList();
            var rowHeight = rowChildren.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < rowChildren.Count; i++)
                rowChildren[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
