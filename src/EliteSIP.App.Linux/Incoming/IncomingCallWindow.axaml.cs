using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
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
    /// <summary>
    /// Сколько позиция должна стоять, чтобы перенос считался законченным.
    /// </summary>
    ///
    /// <remarks>
    /// Перенос ведёт диспетчер окон (<c>_NET_WM_MOVERESIZE</c>), и отпускание
    /// кнопки достаётся ему, а не окну. Конец переноса окно узнаёт по тому,
    /// что его перестали двигать.
    /// </remarks>
    private static readonly TimeSpan MoveSettle = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _moveSettle;

    /// <summary>Сколько подпись говорит «номер скопирован».</summary>
    private static readonly TimeSpan CopiedNoticeTime = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _copiedNotice;

    private PixelPoint _moveStart;

    public IncomingCallWindow()
    {
        InitializeComponent();

        _moveSettle = new DispatcherTimer { Interval = MoveSettle };
        _moveSettle.Tick += (_, _) => EndUserMove();
        Header.PointerPressed += OnHeaderPressed;
        NumberBlock.PointerReleased += OnNumberReleased;

        _copiedNotice = new DispatcherTimer { Interval = CopiedNoticeTime };
        _copiedNotice.Tick += (_, _) =>
        {
            _copiedNotice.Stop();
            if (DataContext is IncomingCallViewModel model)
            {
                model.IsNumberCopied = false;
            }
        };
        PositionChanged += (_, _) =>
        {
            if (IsUserMoving)
            {
                _moveSettle.Stop();
                _moveSettle.Start();
            }
        };
        Closed += (_, _) =>
        {
            // Закрытая посреди переноса карточка своё место не сохраняет.
            _moveSettle.Stop();
            _copiedNotice.Stop();
            IsUserMoving = false;
        };
    }

    /// <summary>Оператор тащит окно. Программное размещение этого не включает.</summary>
    internal bool IsUserMoving { get; private set; }

    /// <summary>Оператор закончил перенос.</summary>
    internal event EventHandler? MovedByUser;

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        IsUserMoving = true;
        _moveStart = Position;
        _moveSettle.Start();
        BeginMoveDrag(e);
    }

    /// <summary>Номер — в буфер обмена. Фокус окно при этом не берёт.</summary>
    private async void OnNumberReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton is not MouseButton.Left
            || DataContext is not IncomingCallViewModel { CopyableNumber: { } number } model
            || Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(number);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException)
        {
            // Буфер занят чужим владельцем или X-сервер не ответил: номер
            // не скопировался, а вызову это не мешает.
            return;
        }

        model.IsNumberCopied = true;
        _copiedNotice.Stop();
        _copiedNotice.Start();
    }

    private void EndUserMove()
    {
        _moveSettle.Stop();
        if (!IsUserMoving)
        {
            return;
        }

        IsUserMoving = false;

        // Щелчок по шапке без сдвига — не выбор места.
        if (Position != _moveStart)
        {
            MovedByUser?.Invoke(this, EventArgs.Empty);
        }
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
