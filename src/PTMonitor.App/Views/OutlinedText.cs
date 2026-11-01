using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace PTMonitor.App.Views;

/// <summary>
/// A short text run drawn with a hard outline around the glyphs. The label
/// stays readable on top of a busy background (the rolling speed graph)
/// without an opaque plate hiding the background behind it.
/// </summary>
public sealed class OutlinedText : Control
{
    /// <summary>The text to draw.</summary>
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<OutlinedText, string>(nameof(Text), string.Empty);

    /// <summary>The glyph size in pixels.</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<OutlinedText, double>(nameof(FontSize), 10d);

    /// <summary>The font to draw with.</summary>
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<OutlinedText, FontFamily>(
            nameof(FontFamily), new FontFamily("Consolas,Inter,monospace"));

    /// <summary>The glyph weight.</summary>
    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<OutlinedText, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    /// <summary>The glyph fill colour.</summary>
    public static readonly StyledProperty<IBrush> FillProperty =
        AvaloniaProperty.Register<OutlinedText, IBrush>(nameof(Fill), Brushes.White);

    /// <summary>The colour painted around the glyphs.</summary>
    public static readonly StyledProperty<IBrush> OutlineProperty =
        AvaloniaProperty.Register<OutlinedText, IBrush>(nameof(Outline), Brushes.Black);

    /// <summary>The outline width in pixels.</summary>
    public static readonly StyledProperty<double> OutlineThicknessProperty =
        AvaloniaProperty.Register<OutlinedText, double>(nameof(OutlineThickness), 1d);

    /// <summary>The eight neighbour offsets that make up a pixel outline.</summary>
    private static readonly (double X, double Y)[] Offsets =
    [
        (-1, 0), (1, 0), (0, -1), (0, 1),
        (-1, -1), (1, -1), (-1, 1), (1, 1),
    ];

    private TextLayout? _layout;
    private TextLayout? _outlineLayout;
    private string? _layoutText;
    private double _layoutFontSize = -1;
    private Typeface? _layoutTypeface;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public IBrush Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush Outline
    {
        get => GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    public double OutlineThickness
    {
        get => GetValue(OutlineThicknessProperty);
        set => SetValue(OutlineThicknessProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty
            || change.Property == FontSizeProperty
            || change.Property == FontFamilyProperty
            || change.Property == FontWeightProperty
            || change.Property == FillProperty
            || change.Property == OutlineProperty)
        {
            // The brush is baked into the text layout, so a colour change
            // rebuilds it too.
            _layout = null;
            _outlineLayout = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == OutlineThicknessProperty)
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = EnsureLayout();
        return new Size(layout.WidthIncludingTrailingWhitespace, layout.Height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (string.IsNullOrEmpty(Text)) return;

        var thickness = OutlineThickness;
        if (thickness > 0 && Outline is { } outline && _outlineLayout is { } outlineLayout)
        {
            // The union of the offset copies is a hard outline around the
            // glyphs: the graph behind stays visible everywhere else.
            for (var radius = 1; radius <= Math.Ceiling(thickness); radius++)
            {
                foreach (var (dx, dy) in Offsets)
                {
                    outlineLayout.Draw(context, new Point(dx * radius, dy * radius));
                }
            }
        }

        EnsureLayout().Draw(context, new Point(0, 0));
    }

    /// <summary>Rebuilds the text layouts when the text or its look changed.</summary>
    private TextLayout EnsureLayout()
    {
        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        if (_layout is not null
            && _outlineLayout is not null
            && _layoutText == Text
            && _layoutFontSize == FontSize
            && Equals(_layoutTypeface, typeface))
        {
            return _layout;
        }

        _layoutText = Text;
        _layoutFontSize = FontSize;
        _layoutTypeface = typeface;
        _layout = new TextLayout(Text ?? string.Empty, typeface, FontSize, Fill);
        _outlineLayout = new TextLayout(Text ?? string.Empty, typeface, FontSize, Outline);
        return _layout;
    }
}
