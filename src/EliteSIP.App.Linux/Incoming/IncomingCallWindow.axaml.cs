using Avalonia;
using Avalonia.Controls;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Incoming;

/// <summary>Плавающее окно входящего вызова — перевод <c>Incoming/IncomingCallWindow</c>.</summary>
///
/// <remarks>
/// <para>
/// Те же три требования, что на Windows: не забирать фокус, висеть поверх
/// всех окон, стоять точно и случайно в пикселях. Поверх — <c>Topmost</c>
/// (<c>_NET_WM_STATE_ABOVE</c>); без фокуса — <c>ShowActivated=False</c> и
/// подсказки менеджеру (<c>IncomingWindowHints</c>); место — <see cref="Window.Position"/>,
/// которая у Avalonia на X11 и есть пиксели корневого окна (W-L0Б: просили
/// 187,173 — встало 187,173).
/// </para>
/// <para>
/// Место ставится <b>до</b> показа, по замеренному размеру (<see cref="MeasureFrame"/>).
/// На Windows окно показывали за краем экрана и переставляли: размер там
/// знает только показ. Avalonia меряет содержимое и без показа, а
/// перестановка уже показанного окна на X11 — это лишний запрос к
/// менеджеру, который вправе его поправить.
/// </para>
/// <para>
/// Замеренная высота ставится окну до показа. Без этого X-окно отображается
/// с высотой по умолчанию (752) и сжимается по содержимому только потом, а
/// Muffin при отображении вдвигает окно на экран по той, большой, высоте:
/// на экране 1080 окно, просившее y = 811, вставало на 288 — каждый раз,
/// когда случайная точка выпадала на нижние две трети.
/// </para>
/// </remarks>
public partial class IncomingCallWindow : Window
{
    public IncomingCallWindow()
    {
        InitializeComponent();
    }

    public IncomingCallWindow(IncomingCallViewModel model)
        : this()
    {
        DataContext = model;
    }

    /// <summary>X11-идентификатор окна; есть уже до показа.</summary>
    internal nuint Xid => (nuint)(TryGetPlatformHandle()?.Handle ?? 0);

    /// <summary>
    /// Меряет окно до показа, ставит ему эту высоту и отдаёт размер в
    /// пикселях при масштабе <paramref name="scaling"/>.
    /// </summary>
    internal ScreenSize MeasureFrame(double scaling)
    {
        Measure(new Size(Width, double.PositiveInfinity));
        Height = DesiredSize.Height;
        return new ScreenSize(Math.Ceiling(Width * scaling), Math.Ceiling(Height * scaling));
    }

    /// <summary>Рамка окна в пикселях — та, по которой считается защита.</summary>
    internal ScreenRect PhysicalFrame
    {
        get
        {
            var size = PixelSize.FromSize(ClientSize, RenderScaling);
            return new ScreenRect(Position.X, Position.Y, size.Width, size.Height);
        }
    }

    /// <summary>Ставит окно углом в заданную точку экрана.</summary>
    internal void PlaceAt(ScreenPoint origin)
        => Position = new PixelPoint((int)Math.Round(origin.X), (int)Math.Round(origin.Y));
}
