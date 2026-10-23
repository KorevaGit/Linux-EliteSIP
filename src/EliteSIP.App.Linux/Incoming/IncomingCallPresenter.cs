using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using EliteSIP.App.Platform;
using EliteSIP.CallGuard;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Incoming;

/// <summary>
/// Показ окна входящего вызова и сбор фактов для защиты — перевод
/// <c>Incoming/IncomingCallPresenter</c> Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// То же разделение: здесь — окно, экран, курсор и внимание к панели, в
/// пакете <c>CallGuard</c> — разбор. Ни одна проверка не принимает решения
/// здесь.
/// </para>
/// <para>
/// <b>Чем Linux отличается.</b> Признак программного ввода у X-сервера есть и
/// у нажатия, и у пути курсора (<see cref="PointerWatch"/>). Попытка
/// помечается синтетической, если хоть одно из трёх: нажатие пришло от XTest;
/// рядом с окном курсор вёл XTest; нажатия мышью по окну не было вовсе —
/// кнопку нажали действием AT-SPI или <c>XSendEvent</c>. Отклонять ли такую
/// попытку, решает политика (<c>RejectsSyntheticEvents</c>, по умолчанию
/// нет — по той же причине, что на Windows: x11vnc и средства доступности
/// шлют ввод тем же XTest), но в отчёт она попадает всегда.
/// </para>
/// </remarks>
public sealed class IncomingCallPresenter : IIncomingCallView, IDisposable
{
    /// <summary>Насколько считать курсор «рядом с окном», в точках.</summary>
    ///
    /// <remarks>
    /// Рядом, а не строго внутри: рука подходит к кнопке снаружи, и обрезать
    /// путь по рамке значит требовать движений уже над самой кнопкой.
    /// </remarks>
    private const double NearWindowMargin = 80;

    /// <summary>
    /// Насколько старое нажатие по окну ещё считается нажатием этой попытки.
    /// Щелчок срабатывает на отпускании, а кнопку держат до секунды.
    /// </summary>
    private static readonly TimeSpan PressAge = TimeSpan.FromSeconds(3);

    /// <summary>Как часто забирать путь курсора у слежения.</summary>
    private static readonly TimeSpan PathPoll = TimeSpan.FromMilliseconds(30);

    private readonly Action<string> _log;
    private readonly Func<Window?> _panel;
    private readonly DispatcherTimer _pathTimer;

    private IncomingCallWindow? _window;
    private IncomingCallViewModel? _model;
    private CallGuardSession? _session;
    private IncomingCallPlacement? _placement;
    private PointerWatch? _pointer;
    private Stopwatch? _clock;
    private Action? _onAnswer;
    private Action? _onDecline;
    private CallGuardPolicy? _policy;
    private double _scaling = 1;
    private int _nearMotions;
    private int _xtestMotions;
    private PixelPoint _requested;

    /// <summary>
    /// Где окно было в прошлый раз — чтобы следующая позиция гарантированно
    /// отличалась и оператор не привыкал жать в одну точку.
    /// </summary>
    private ScreenPoint? _lastOrigin;

    /// <param name="log">Журнал приложения.</param>
    /// <param name="panel">Панель софтфона: у неё кнопка в панели задач.</param>
    public IncomingCallPresenter(Action<string> log, Func<Window?> panel)
    {
        _log = log;
        _panel = panel;
        _pathTimer = new DispatcherTimer { Interval = PathPoll };
        _pathTimer.Tick += (_, _) => TakePath();
    }

    public bool IsVisible => _window is not null;

    /// <summary>
    /// Место, выбранное оператором. Берётся, только когда случайная позиция
    /// выключена; <c>null</c> — места ещё нет, карточка встаёт посреди экрана.
    /// </summary>
    public Func<ScreenPoint?>? RestorePlacement { get; init; }

    /// <summary>
    /// Запомнить место после переноса оператором. Зовётся, только когда
    /// случайная позиция выключена: перенос при ней фиксированное место не
    /// переписывает.
    /// </summary>
    public Action<ScreenPoint>? SavePlacement { get; init; }

    /// <inheritdoc cref="IIncomingCallView.StartRingtone"/>
    public Action? StartRingtone { get; set; }

    /// <inheritdoc cref="IIncomingCallView.StopRingtone"/>
    public Action? StopRingtone { get; set; }

    /// <summary>
    /// Отчёт защиты по последнему вызову. После <see cref="Hide"/> остаётся
    /// последним, чтобы его успел прочитать тот, кто разбирает завершение звонка.
    /// </summary>
    public CallGuardReport? LastReport { get; private set; }

    /// <summary>Показывает окно и берёт вызов под защиту.</summary>
    public void Show(IncomingCallSubject subject, CallGuardPolicy policy, Action onAnswer, Action onDecline)
    {
        ArgumentNullException.ThrowIfNull(policy);

        Hide();

        policy = policy.Normalized();
        _policy = policy;

        _clock = Stopwatch.StartNew();
        _session = new CallGuardSession(policy, TimeSpan.Zero, Random.Shared);
        LastReport = _session.Report;
        _onAnswer = onAnswer;
        _onDecline = onDecline;
        _nearMotions = 0;
        _xtestMotions = 0;

        // Слежение — первым: курсор нужен, чтобы выбрать экран, и путь
        // считается с первого кадра окна, а не с конца показа.
        _pointer = PointerWatch.Start(out var failure);
        if (_pointer is null)
        {
            _log($"окно входящего: слежение за мышью не встало ({failure}) — путь курсора и источник нажатия не считаются");
        }
        else if (!_pointer.KnowsXTest)
        {
            _log("окно входящего: устройств XTest у X-сервера не нашлось — синтетический ввод не различается");
        }

        _model = new IncomingCallViewModel(subject, _session.Challenge, policy.IsEnabled, Attempt, Decline);
        var window = new IncomingCallWindow(_model);
        _window = window;

        var saved = Randomizes(policy) ? null : RestorePlacement?.Invoke();
        if (saved is { } point && (!double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            saved = null;
        }

        // Сохранённое место — на своём экране, даже если курсор на другом.
        var screen = saved is { } at ? ScreenNearest(window, at) : ScreenUnderCursor(window);
        _scaling = screen?.Scaling ?? 1;
        _placement = PlacementFor(policy, screen);

        var size = window.MeasureFrame(_scaling);
        var origin = _placement.Contained(new ScreenRect(saved ?? NextOrigin(policy, size), size)).Origin;
        window.PlaceAt(origin);
        _lastOrigin = origin;
        _requested = window.Position;

        var xid = window.Xid;
        IncomingWindowHints.BeforeShow(xid);

        window.Opened += OnOpened;

        // Последний рубеж: любое изменение размера после размещения
        // возвращает окно внутрь области. Окно, которое оператор не видит
        // целиком, — это не огрех оформления, а непринятый лид.
        window.SizeChanged += OnWindowSizeChanged;
        window.MovedByUser += OnWindowMovedByUser;
        window.Show();

        _pointer?.WatchWindow(xid);
        _pathTimer.Start();

        if (_panel() is { } panel)
        {
            WindowAttention.Request(Xid(panel));
        }

        // Звонок — после показа: устройство открывается десятки миллисекунд,
        // и делать это до показа значит задержать саму карточку.
        StartRingtone?.Invoke();
    }

    /// <summary>Убирает окно и закрывает отчёт.</summary>
    public void Hide()
    {
        // Первым делом: `Hide` зовут и с начала `Show`, и по отбою, и по
        // ответу, и звонок обязан замолчать в тот же миг.
        StopRingtone?.Invoke();
        _pathTimer.Stop();

        if (_window is not null && _panel() is { } panel)
        {
            WindowAttention.Cancel(Xid(panel));
        }

        _pointer?.Dispose();
        _pointer = null;

        if (_session is not null)
        {
            LastReport = _session.Report;
        }

        if (_window is not null)
        {
            _window.Opened -= OnOpened;
            _window.SizeChanged -= OnWindowSizeChanged;
            _window.MovedByUser -= OnWindowMovedByUser;
            _window.Close();
            _window = null;
        }

        _session = null;
        _model = null;
        _placement = null;
        _policy = null;
        _clock = null;
        _onAnswer = null;
        _onDecline = null;
    }

    public void Dispose() => Hide();

    private static nuint Xid(Window window) => (nuint)(window.TryGetPlatformHandle()?.Handle ?? 0);

    private void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not IncomingCallWindow window || window != _window)
        {
            return;
        }

        IncomingWindowHints.AfterShow(window.Xid);

        // Менеджер вправе поставить окно не туда, куда просили, — и делает это
        // молча. Случайная позиция — мера защиты, поэтому расхождение
        // исправляется, а не терпится: окно, вставшее на одну и ту же высоту,
        // оператор находит рукой не глядя.
        if (window.Position != _requested)
        {
            _log($"окно входящего: менеджер поставил окно в {window.Position} вместо {_requested} — переставлено");
            window.Position = _requested;
        }

        KeepOnScreen();

        _log(PlacementSummary(window.PhysicalFrame));
        _log($"окно входящего: {IncomingWindowHints.Describe(window.Xid)}");
    }

    /// <summary>Строка для журнала: где окно оказалось и в какой области его держали.</summary>
    // не переводится: журнал.
    private string PlacementSummary(ScreenRect frame)
    {
        var area = _placement?.Bounds ?? new ScreenRect(0, 0, 0, 0);
        var fits = area.Contains(frame) ? "внутри" : "ВЫШЛО ЗА ОБЛАСТЬ";
        return $"окно входящего: рамка {Short(frame)}, область {Short(area)} — {fits}";
    }

    private static string Short(ScreenRect rect) => string.Format(
        CultureInfo.InvariantCulture,
        "({0}, {1}, {2}×{3})",
        (int)rect.X,
        (int)rect.Y,
        (int)rect.Width,
        (int)rect.Height);

    /// <summary>Разбирает попытку принять вызов.</summary>
    ///
    /// <remarks>
    /// Источник у попытки всегда один — мышь. Клавиатурного приёма нет
    /// намеренно, а у окна нет и самой клавиатуры (<c>WM_HINTS.input = False</c>).
    /// </remarks>
    private void Attempt(char target)
    {
        if (_session is not CallGuardSession session || _clock is not Stopwatch clock)
        {
            return;
        }

        // Всё, что сервер прислал до отпускания кнопки, — в разбор этой
        // попытки: путь до последней точки и нажатие, которое её начало.
        _pointer?.Sync();
        TakePath();
        var press = _pointer?.TakePress(PressAge);
        var origin = Classify(_pointer is not null, press, _xtestMotions, _nearMotions);

        var attempt = new CallGuardAttempt(target, origin.IsSynthetic, clock.Elapsed);
        var verdict = session.Evaluate(attempt);
        LastReport = session.Report;

        if (verdict.Rejection is CallGuardRejection reason)
        {
            // Окно остаётся на месте: скрыть его в ответ на отклонённое
            // нажатие значило бы потерять лид из-за собственной защиты.
            if (_model is not null)
            {
                _model.Refusal = reason.OperatorMessage();
            }

            _log($"вызов не принят: {reason.LogMessage()}, {origin.Text}, {IdleSummary()}");
            return;
        }

        var answer = _onAnswer;
        _log($"вызов принят: {session.Report.Summary()}, {origin.Text}, {IdleSummary()}");
        Hide();
        answer?.Invoke();
    }

    /// <summary>Откуда пришла попытка — признак для разбора и строка для журнала.</summary>
    ///
    /// <param name="watching">Слежение за мышью встало.</param>
    /// <param name="press">Нажатие мышью по окну, которое начало эту попытку.</param>
    /// <param name="xtestMotions">Сколько шагов пути рядом с окном вёл XTest.</param>
    /// <param name="nearMotions">Сколько всего шагов пути было рядом с окном.</param>
    // не переводится: журнал.
    internal static (bool IsSynthetic, string Text) Classify(
        bool watching, PointerPress? press, int xtestMotions, int nearMotions)
    {
        if (!watching)
        {
            // Слежения нет — признаков нет. Считать такую попытку
            // синтетической значило бы отказывать честному оператору за сбой
            // нашей же стороны; в журнале при показе это уже сказано.
            return (false, "источник нажатия неизвестен (слежения нет)");
        }

        var path = $"путь XTest {xtestMotions} из {nearMotions}";

        if (press is not { } found)
        {
            return (true, $"нажатия мышью по окну не было (AT-SPI или XSendEvent), {path}");
        }

        var source = $"нажатие от «{found.Device}» (id {found.SourceId}{(found.FromXTest ? ", XTest" : string.Empty)})";
        return (found.FromXTest || xtestMotions > 0, $"{source}, {path}");
    }

    private void Decline()
    {
        var decline = _onDecline;
        Hide();
        decline?.Invoke();
    }

    /// <summary>Сколько X-сервер не видел ввода к моменту решения.</summary>
    // не переводится: журнал.
    private string IdleSummary()
        => _pointer?.SystemIdleTime() is { } idle
            ? $"без ввода {(int)idle.TotalMilliseconds} мс"
            : "время без ввода неизвестно";

    private void TakePath() => _pointer?.TakeMotions(NoteCursor);

    /// <summary>Считает только те перемещения, что случились рядом с окном.</summary>
    private void NoteCursor(PointerMotion motion)
    {
        if (_session is not CallGuardSession session || _window is not IncomingCallWindow window)
        {
            return;
        }

        var point = new ScreenPoint(motion.X, motion.Y);
        if (!window.PhysicalFrame.Inset(-NearWindowMargin * _scaling).Contains(point))
        {
            return;
        }

        _nearMotions++;
        if (motion.FromXTest)
        {
            _xtestMotions++;
        }

        session.NoteCursor(point);
    }

    /// <summary>Экран под курсором — рабочее место вполне может быть на втором мониторе.</summary>
    private Screen? ScreenUnderCursor(Window window)
    {
        var screens = window.Screens;
        if (_pointer?.Cursor() is var (x, y) && screens.ScreenFromPoint(new PixelPoint(x, y)) is { } exact)
        {
            return exact;
        }

        // Курсор вне всех экранов или слежения нет — главный экран.
        return screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
    }

    /// <summary>
    /// Экран, ближайший к точке: на нём или, если монитор с той точкой
    /// отключили, тот, до чьей рамки ближе всего.
    /// </summary>
    private static Screen? ScreenNearest(Window window, ScreenPoint point)
    {
        var screens = window.Screens;
        var pixel = new PixelPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
        if (screens.ScreenFromPoint(pixel) is { } exact)
        {
            return exact;
        }

        return screens.All.Count == 0
            ? null
            : screens.All.MinBy(screen => Distance(screen.Bounds, point));

        static double Distance(PixelRect bounds, ScreenPoint point)
        {
            double dx = Math.Max(Math.Max(bounds.X - point.X, 0), point.X - bounds.Right);
            double dy = Math.Max(Math.Max(bounds.Y - point.Y, 0), point.Y - bounds.Bottom);
            return (dx * dx) + (dy * dy);
        }
    }

    private static bool Randomizes(CallGuardPolicy policy) => policy.IsEnabled && policy.IsRandomPositionEnabled;

    /// <summary>Область, в которой окну разрешено появиться на этом вызове.</summary>
    ///
    /// <remarks>
    /// Рабочая область, а не весь экран: под панелью Cinnamon окно видно
    /// наполовину, и это тот же непринятый лид. Отступ и минимальное смещение
    /// заданы в точках и умножаются на масштаб экрана — расстояние, на которое
    /// окно обязано отъехать, это расстояние для глаза.
    /// </remarks>
    private IncomingCallPlacement PlacementFor(CallGuardPolicy policy, Screen? screen)
    {
        var work = screen?.WorkingArea is { Width: > 0, Height: > 0 } area
            ? new ScreenRect(area.X, area.Y, area.Width, area.Height)

            // Экрана нет — сеанс без монитора. Показывать окно всё равно
            // надо: разбирать «почему вызов не пришёл» дороже, чем окно в углу.
            : new ScreenRect(0, 0, 1024, 768);

        // Окно ставится в целые пиксели. При масштабе 1,25 отступ в 10 точек —
        // 12,5 пикселя: границы округляются внутрь, чтобы повторное размещение
        // не оставляло окно на полпикселя снаружи.
        var inset = work.Inset(policy.ScreenMargin * _scaling);
        var left = Math.Ceiling(inset.MinX);
        var top = Math.Ceiling(inset.MinY);
        var bounds = new ScreenRect(left, top,
            Math.Max(0, Math.Floor(inset.MaxX) - left), Math.Max(0, Math.Floor(inset.MaxY) - top));
        return new IncomingCallPlacement(bounds, policy.MinimumTravel * _scaling);
    }

    private ScreenPoint NextOrigin(CallGuardPolicy policy, ScreenSize size)
    {
        var placement = _placement!;

        if (!Randomizes(policy))
        {
            return new ScreenPoint(
                placement.Bounds.MidX - (size.Width / 2),
                placement.Bounds.MidY - (size.Height / 2));
        }

        return placement.Origin(size, _lastOrigin, Random.Shared);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Во время переноса окно ведёт оператор: возвращать его на исходный
        // экран посреди движения нельзя.
        if (_window?.IsUserMoving is false)
        {
            KeepOnScreen();
        }
    }

    /// <summary>
    /// Оператор отпустил карточку: вдвинуть её в рабочую область экрана, на
    /// который её перенесли, и запомнить место, если случайная позиция выключена.
    /// </summary>
    private void OnWindowMovedByUser(object? sender, EventArgs e)
    {
        if (sender is not IncomingCallWindow window || window != _window || _policy is not { } policy)
        {
            return;
        }

        KeepOnScreen();

        // Следующее случайное место отсчитывается от того, куда окно
        // перенесли, а не от того, где оно появилось.
        var origin = window.PhysicalFrame.Origin;
        _lastOrigin = origin;
        if (!Randomizes(policy))
        {
            SavePlacement?.Invoke(origin);
        }
    }

    /// <summary>Возвращает окно внутрь области, если оно оттуда вылезло.</summary>
    ///
    /// <remarks>
    /// Рамка не выбирается заново, а вдвигается обратно ровно на столько, на
    /// сколько вылезла: позиция остаётся случайной.
    /// </remarks>
    private void KeepOnScreen()
    {
        if (_window is not IncomingCallWindow window || _placement is null || _policy is not { } policy)
        {
            return;
        }

        // Область — экрана, где окно сейчас: после переноса это может быть
        // другой монитор со своим масштабом.
        var frame = window.PhysicalFrame;
        var screen = ScreenNearest(window, new ScreenPoint(frame.MidX, frame.MidY));
        _scaling = screen?.Scaling ?? _scaling;
        var placement = PlacementFor(policy, screen);
        _placement = placement;
        var corrected = placement.Contained(frame);
        if (corrected.Origin == frame.Origin)
        {
            return;
        }

        window.PlaceAt(corrected.Origin);

        // Следующий вызов обязан отсчитывать смещение от того места, где окно
        // оказалось на самом деле.
        _lastOrigin = corrected.Origin;
    }
}
