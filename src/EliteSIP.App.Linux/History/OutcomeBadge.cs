using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>
/// Значок исхода звонка — перевод <c>History/OutcomeBadge</c>: стрелка в
/// кольце (не состоялся) или на диске (состоялся), к себе или от себя.
/// </summary>
///
/// <remarks>
/// Рисуется сам, без шаблона: на Windows это был шаблон с тремя фигурами и
/// восемью триггерами цвета, а в строке истории таких значков десятки. Цвет —
/// ключом палитры по исходу, тем же разбором, что в WPF-триггерах.
/// </remarks>
public sealed class OutcomeBadge : Control
{
    public static readonly StyledProperty<bool> IsIncomingProperty =
        AvaloniaProperty.Register<OutcomeBadge, bool>(nameof(IsIncoming), true);

    public static readonly StyledProperty<bool> IsCompletedProperty =
        AvaloniaProperty.Register<OutcomeBadge, bool>(nameof(IsCompleted), true);

    public static readonly StyledProperty<CallOutcome> OutcomeProperty =
        AvaloniaProperty.Register<OutcomeBadge, CallOutcome>(nameof(Outcome), CallOutcome.Completed);

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<OutcomeBadge, double>(nameof(Size), 22.0);

    /// <summary>Свой цвет вместо цвета по исходу (окно входящего — всегда «ответ»).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<OutcomeBadge>();

    private static readonly Geometry Incoming = Geometry.Parse("M 13.5,6.5 L 6.5,13.5 M 11,13.5 H 6.5 V 9");
    private static readonly Geometry Outgoing = Geometry.Parse("M 6.5,13.5 L 13.5,6.5 M 9,6.5 H 13.5 V 11");

    static OutcomeBadge()
    {
        AffectsRender<OutcomeBadge>(IsIncomingProperty, IsCompletedProperty, OutcomeProperty, ForegroundProperty);
        AffectsMeasure<OutcomeBadge>(SizeProperty);
    }

    public bool IsIncoming
    {
        get => GetValue(IsIncomingProperty);
        set => SetValue(IsIncomingProperty, value);
    }

    public bool IsCompleted
    {
        get => GetValue(IsCompletedProperty);
        set => SetValue(IsCompletedProperty, value);
    }

    public CallOutcome Outcome
    {
        get => GetValue(OutcomeProperty);
        set => SetValue(OutcomeProperty, value);
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
        var color = IsSet(ForegroundProperty) ? Foreground : Brush(ColorKey());
        if (color is null)
        {
            return;
        }

        var scale = Size / 20;
        using var placed = context.PushTransform(Matrix.CreateScale(scale, scale));

        var arrow = IsIncoming ? Incoming : Outgoing;
        if (IsCompleted)
        {
            context.DrawEllipse(color, null, new Point(10, 10), 10, 10);
            context.DrawGeometry(null, Pen(Brush("WindowBackgroundBrush") ?? Brushes.White), arrow);
        }
        else
        {
            context.DrawEllipse(null, Pen(color), new Point(10, 10), 9.3, 9.3);
            context.DrawGeometry(null, Pen(color), arrow);
        }
    }

    private static Pen Pen(IBrush brush) => new(brush, 1.43, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private string ColorKey() => Outcome switch
    {
        CallOutcome.Missed => "OutcomeMissedBrush",
        CallOutcome.Busy or CallOutcome.NoAnswer or CallOutcome.UnknownNumber
            or CallOutcome.Declined or CallOutcome.Failed => "OutcomeUnreachableBrush",
        CallOutcome.Completed when IsIncoming => "OutcomeAnsweredBrush",
        _ => "OutcomeCompletedBrush",
    };

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
}
