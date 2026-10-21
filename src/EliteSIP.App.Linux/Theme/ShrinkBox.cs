using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace EliteSIP.App.Theme;

/// <summary>
/// Подпись, которая ужимается, чтобы самое длинное слово влезло целиком, и
/// растёт вместе с высотой клавиши.
/// </summary>
///
/// <remarks>
/// Перенос <c>Theme/ShrinkBox</c> Windows-версии один в один — алгоритм и
/// числа те же (0.6 … 1.75 от исходного кегля, 0.3 высоты клавиши, шаг 0.92).
/// Почему он такой, сказано там: срезанная середина слова на клавише макроса
/// (0.1.58) и подписи, которые росли и съёживались на глазах (0.1.64).
/// </remarks>
public sealed class ShrinkBox : Decorator
{
    public static readonly StyledProperty<double> MinimumScaleProperty =
        AvaloniaProperty.Register<ShrinkBox, double>(nameof(MinimumScale), 0.6);

    public static readonly StyledProperty<double> BaseFontSizeProperty =
        AvaloniaProperty.Register<ShrinkBox, double>(nameof(BaseFontSize), 15.0);

    public static readonly StyledProperty<bool> GrowsWithHeightProperty =
        AvaloniaProperty.Register<ShrinkBox, bool>(nameof(GrowsWithHeight));

    public static readonly StyledProperty<double> MaximumScaleProperty =
        AvaloniaProperty.Register<ShrinkBox, double>(nameof(MaximumScale), 1.75);

    public static readonly StyledProperty<double> FallbackHeightProperty =
        AvaloniaProperty.Register<ShrinkBox, double>(nameof(FallbackHeight), double.PositiveInfinity);

    private const double HeightToFont = 0.3;

    private bool _sized;

    static ShrinkBox()
    {
        AffectsMeasure<ShrinkBox>(
            MinimumScaleProperty, BaseFontSizeProperty, GrowsWithHeightProperty, MaximumScaleProperty, FallbackHeightProperty);
    }

    public double MinimumScale
    {
        get => GetValue(MinimumScaleProperty);
        set => SetValue(MinimumScaleProperty, value);
    }

    public double BaseFontSize
    {
        get => GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    public bool GrowsWithHeight
    {
        get => GetValue(GrowsWithHeightProperty);
        set => SetValue(GrowsWithHeightProperty, value);
    }

    public double MaximumScale
    {
        get => GetValue(MaximumScaleProperty);
        set => SetValue(MaximumScaleProperty, value);
    }

    public double FallbackHeight
    {
        get => GetValue(FallbackHeightProperty);
        set => SetValue(FallbackHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        // Пробный замер на бесконечной высоте кегль не трогает — см. Windows-версию.
        if (GrowsWithHeight && double.IsInfinity(availableSize.Height) && _sized)
        {
            child.Measure(new Size(availableSize.Width, FallbackHeight));
            return new Size(child.DesiredSize.Width, Math.Min(child.DesiredSize.Height, FallbackHeight));
        }

        _sized = true;
        var height = double.IsInfinity(availableSize.Height) ? FallbackHeight : availableSize.Height;
        var start = BaseFontSize;
        if (GrowsWithHeight && !double.IsInfinity(height))
        {
            start = Math.Clamp(height * HeightToFont, BaseFontSize, BaseFontSize * MaximumScale);
        }

        var size = ScaledSize(child, availableSize.Width, start);
        child.SetValue(TextElement.FontSizeProperty, size);
        child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

        var floor = BaseFontSize * MinimumScale;
        while (!double.IsInfinity(height) && child.DesiredSize.Height > height && size > floor)
        {
            size = Math.Max(floor, size * 0.92);
            child.SetValue(TextElement.FontSizeProperty, size);
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        child.Measure(new Size(availableSize.Width, Math.Min(availableSize.Height, height)));
        return child.DesiredSize;
    }

    private double ScaledSize(Control child, double available, double start)
    {
        if (child is not TextBlock text
            || string.IsNullOrEmpty(text.Text)
            || double.IsInfinity(available)
            || available <= 0)
        {
            return start;
        }

        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var longest = 0d;

        foreach (var word in text.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var measured = new FormattedText(
                word, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, start, Brushes.Black);

            longest = Math.Max(longest, measured.WidthIncludingTrailingWhitespace);
        }

        if (longest <= available || longest <= 0)
        {
            return start;
        }

        return Math.Max(start * available / longest, BaseFontSize * MinimumScale);
    }
}
