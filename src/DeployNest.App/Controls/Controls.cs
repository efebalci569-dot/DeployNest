using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeployNest.App.Controls;

public enum PillKind
{
    Neutral,
    Success,
    Warning,
    Danger,
    Accent,
}

/// <summary>Small rounded status label (e.g. "Kurulu", "Başarısız").</summary>
public sealed class StatusPill : Control
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(PillKind), typeof(StatusPill), new PropertyMetadata(PillKind.Neutral));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusPill), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StatusPill), new PropertyMetadata(null));

    static StatusPill()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusPill), new FrameworkPropertyMetadata(typeof(StatusPill)));
    }

    public PillKind Kind
    {
        get => (PillKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}

/// <summary>
/// Program icon: the real icon of an installed program when available, otherwise a colored
/// monogram tile. Brand logos are not bundled to respect publishers' trademarks.
/// </summary>
public sealed class AppIcon : Control
{
    public static readonly DependencyProperty MonogramProperty =
        DependencyProperty.Register(nameof(Monogram), typeof(string), typeof(AppIcon), new PropertyMetadata("?", OnVisualChanged));

    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(nameof(AccentColor), typeof(Color), typeof(AppIcon), new PropertyMetadata(Colors.SlateGray, OnVisualChanged));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(AppIcon), new PropertyMetadata(null, OnVisualChanged));

    private static readonly DependencyPropertyKey TileBrushKey =
        DependencyProperty.RegisterReadOnly(nameof(TileBrush), typeof(Brush), typeof(AppIcon), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey MonogramFontSizeKey =
        DependencyProperty.RegisterReadOnly(nameof(MonogramFontSize), typeof(double), typeof(AppIcon), new PropertyMetadata(16d));

    private static readonly DependencyPropertyKey HasImageKey =
        DependencyProperty.RegisterReadOnly(nameof(HasImage), typeof(bool), typeof(AppIcon), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey ImageMarginKey =
        DependencyProperty.RegisterReadOnly(nameof(ImageMargin), typeof(Thickness), typeof(AppIcon), new PropertyMetadata(new Thickness(5)));

    public static readonly DependencyProperty TileBrushProperty = TileBrushKey.DependencyProperty;
    public static readonly DependencyProperty MonogramFontSizeProperty = MonogramFontSizeKey.DependencyProperty;
    public static readonly DependencyProperty HasImageProperty = HasImageKey.DependencyProperty;
    public static readonly DependencyProperty ImageMarginProperty = ImageMarginKey.DependencyProperty;

    static AppIcon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(typeof(AppIcon)));
        WidthProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(40d, OnVisualChanged));
    }

    public AppIcon() => Update();

    public string Monogram { get => (string)GetValue(MonogramProperty); set => SetValue(MonogramProperty, value); }
    public Color AccentColor { get => (Color)GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Brush? TileBrush => (Brush?)GetValue(TileBrushProperty);
    public double MonogramFontSize => (double)GetValue(MonogramFontSizeProperty);
    public bool HasImage => (bool)GetValue(HasImageProperty);
    public Thickness ImageMargin => (Thickness)GetValue(ImageMarginProperty);

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AppIcon)d).Update();

    private void Update()
    {
        var color = AccentColor;
        var light = Color.FromRgb(Lighten(color.R), Lighten(color.G), Lighten(color.B));
        var brush = new LinearGradientBrush(light, color, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        SetValue(TileBrushKey, brush);

        var size = double.IsNaN(Width) ? 40 : Width;
        var length = Math.Max(1, (Monogram ?? "?").Length);
        var factor = length switch { 1 => 0.48, 2 => 0.40, 3 => 0.31, _ => 0.25 };
        SetValue(MonogramFontSizeKey, Math.Round(size * factor));
        SetValue(HasImageKey, Source is not null);
        SetValue(ImageMarginKey, new Thickness(Math.Round(size * 0.13)));
    }

    private static byte Lighten(byte channel) => (byte)Math.Min(255, channel + (255 - channel) * 0.28);
}

/// <summary>Arranges items in equal-width columns; the column count follows the available width.</summary>
public sealed class UniformWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(UniformWrapPanel),
            new FrameworkPropertyMetadata(300d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(UniformWrapPanel),
            new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

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
        foreach (UIElement child in InternalChildren)
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
        var children = InternalChildren.Cast<UIElement>().ToList();
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

/// <summary>Content host that fades and slides new pages in (kept short to stay unobtrusive).</summary>
public sealed class TransitioningContentControl : ContentControl
{
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (SystemParameters.ClientAreaAnimation == false)
            return;

        var transform = new TranslateTransform(0, 10);
        RenderTransform = transform;
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }
}
