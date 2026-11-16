using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>The approved champagne outline, shared by Linux windows and tray.</summary>
public sealed class BrandMark : Control
{
    internal const string SymbolData = "M7 12 L5 5 L11 8 L16 3 L21 8 L27 5 L25 12 M7 16 C7 12 25 12 25 16 L25 22 Q25 28 19 28 H17 M7 16 L7 21 Q7 27 13 29 M4 16 Q1 16 1 20 Q1 24 4 24 M28 16 Q31 16 31 20 Q31 24 28 24";
    private static readonly Geometry Symbol = Geometry.Parse(SymbolData);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<BrandMark>();
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static BrandMark() { AffectsRender<BrandMark>(ForegroundProperty); }
    protected override Size MeasureOverride(Size availableSize) => new(24, 24);
    internal static void Draw(DrawingContext context, IBrush brush, double size)
    {
        using var transform = context.PushTransform(Matrix.CreateScale(size / 34, size / 34) * Matrix.CreateTranslation(size / 34, size / 34));
        context.DrawGeometry(null, new Pen(brush, 2.3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Symbol);
    }
    public override void Render(DrawingContext context) => Draw(context, Foreground ?? Brushes.Gray, Math.Min(Bounds.Width, Bounds.Height));
    internal static WindowIcon TrayIcon(IBrush status)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(64,64), new Vector(96,96));
        using (var drawing = bitmap.CreateDrawingContext()) { Draw(drawing, status, 64); }
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default); png.Position = 0;
        return new WindowIcon(png);
    }
}
