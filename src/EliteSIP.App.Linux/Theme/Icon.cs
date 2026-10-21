using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace EliteSIP.App.Theme;

/// <summary>
/// Значок из комплекта (<see cref="IconLibrary"/>) цветом <see cref="Foreground"/>.
/// </summary>
///
/// <remarks>
/// То же, что <c>Theme/Icon</c> Windows-версии: имя значка, сторона в точках,
/// цвет — от <c>Foreground</c>. Там значок был холстом из словаря в
/// <c>Viewbox</c>; здесь он рисуется сам, без дерева элементов, — значков на
/// панели два десятка, и каждый из них на Windows был полудюжиной элементов.
/// </remarks>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Glyph));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), IconSizes.Medium);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(GlyphProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
        FocusableProperty.OverrideDefaultValue<Icon>(false);
    }

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (IconLibrary.Find(Glyph) is not { } shape || Foreground is not { } brush)
        {
            return;
        }

        var scale = Size / IconLibrary.CanvasSize;
        var offset = new Point((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);

        using var placed = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y));
        foreach (var stroke in shape.Strokes)
        {
            using var transformed = context.PushTransform(stroke.Transform);
            var pen = stroke.IsStroked
                ? new Pen(brush, stroke.Thickness, lineCap: stroke.Cap, lineJoin: stroke.Join)
                : null;

            context.DrawGeometry(stroke.IsFilled ? brush : null, pen, stroke.Geometry);
        }
    }
}
