using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace EliteSIP.App.Theme;

/// <summary>
/// Шкала уровня из 24 сегментов — перевод <c>Theme/LevelMeter</c>: зелёный до
/// 75 %, жёлтый до 92 %, красный выше, пик держится полсекунды, уровень — по
/// корню, как слышит ухо. Числа те же, что у Windows и macOS.
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));

    private const int Segments = 24;
    private const double GreenUntil = 0.75;
    private const double YellowUntil = 0.92;
    private const double Gap = 2;
    private static readonly TimeSpan PeakHold = TimeSpan.FromMilliseconds(500);

    private static readonly IBrush Green = new ImmutableSolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly IBrush Yellow = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
    private static readonly IBrush Red = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));

    private double _shown;
    private double _peak;
    private DateTime _peakAt;

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(LevelProperty);
    }

    public LevelMeter()
    {
        Height = 10;
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != LevelProperty)
        {
            return;
        }

        _shown = Math.Sqrt(Math.Clamp(change.GetNewValue<double>(), 0, 1));
        var now = DateTime.UtcNow;
        if (_shown >= _peak || now - _peakAt > PeakHold)
        {
            _peak = _shown;
            _peakAt = now;
        }
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var unlit = this.TryFindResource("SeparatorBrush", ActualThemeVariant, out var found) && found is IBrush brush
            ? brush
            : Brushes.LightGray;

        var segment = (width - (Gap * (Segments - 1))) / Segments;
        var lit = (int)Math.Round(_shown * Segments);
        var peak = Math.Clamp((int)Math.Ceiling(_peak * Segments) - 1, -1, Segments - 1);

        for (var index = 0; index < Segments; index++)
        {
            var position = (index + 1) / (double)Segments;
            var color = position <= GreenUntil ? Green : position <= YellowUntil ? Yellow : Red;
            var on = index < lit || (index == peak && _peak > 0.02);
            context.DrawRectangle(on ? color : unlit, null, new RoundedRect(new Rect(index * (segment + Gap), 0, segment, height), 1.5));
        }
    }
}
