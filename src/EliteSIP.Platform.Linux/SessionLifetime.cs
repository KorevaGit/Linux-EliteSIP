using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Конец сеанса и пробуждение — то, что на Windows приходило событиями
/// <c>SessionEnding</c> и <c>SystemEvents.PowerModeChanged</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Зачем снимать регистрацию на выходе.</b> Регистрация на АТС живёт до
/// конца своего срока. Выключенная машина, не сказавшая «я ушла», ещё
/// несколько минут числится на месте, и вызовы всё это время уходят в никуда
/// вместо очереди или соседа. Поэтому на выходе уходит REGISTER с
/// <c>expires=0</c> — и на это нужно время.
/// </para>
/// <para>
/// <b>Откуда приходит конец.</b> Три пути, и все сходятся в одно
/// <see cref="Ending"/>, которое приходит один раз.
/// </para>
/// <list type="bullet">
/// <item>
/// Выход из сеанса объявляет менеджер сеанса (<c>cinnamon-session</c>, у
/// GNOME — <c>gnome-session</c>) по D-Bus <c>org.gnome.SessionManager</c>:
/// зарегистрированный клиент получает <c>EndSession</c>, и выход ждёт его
/// ответа. Это точный аналог <c>SessionEnding</c> Windows, и он нужен именно
/// потому, что приходит, пока X-сервер ещё жив. Без него первым приходит не
/// <c>SIGTERM</c>, а обрыв X: Xlib зовёт <c>exit(1)</c> прямо из потока
/// окна, и снять регистрацию не успевает никто (замер на Xephyr 21 октября
/// 2026).
/// </item>
/// <item>
/// Выключение и перезагрузка сначала объявляются сигналом logind
/// <c>PrepareForShutdown</c>, и logind ждёт, пока держатели задерживающего
/// замка (<c>Inhibit</c> с режимом <c>delay</c>) его не отпустят — не дольше
/// <c>InhibitDelayMaxSec</c>, по умолчанию 5 с. Замок берётся заранее, при
/// запуске: в момент сигнала брать его уже поздно.
/// </item>
/// <item>
/// <c>systemctl --user stop</c>, остановка менеджера пользователя и
/// <c>kill</c> присылают <c>SIGTERM</c>.
/// </item>
/// </list>
/// <para>
/// <para>
/// <b>Сон — только пробуждение.</b> Как и на Windows, уход в сон регистрацию
/// не снимает: машина, погасившая экран по бездействию, не должна пропускать
/// вызовы. После пробуждения связь поднимается заново и стук переигрывается
/// — это <see cref="Resumed"/>.
/// </para>
/// </remarks>
internal sealed class SessionLifetime : IDisposable
{
    private const string Logind = "org.freedesktop.login1";
    private const string ManagerPath = "/org/freedesktop/login1";
    private const string ManagerInterface = "org.freedesktop.login1.Manager";

    private const string SessionManager = "org.gnome.SessionManager";
    private const string SessionManagerPath = "/org/gnome/SessionManager";
    private const string ClientInterface = "org.gnome.SessionManager.ClientPrivate";

    /// <summary>
    /// Сколько ждать снятия регистрации перед тем, как отпустить замок.
    /// </summary>
    ///
    /// <remarks>
    /// Меньше пяти секунд logind по умолчанию: отпустить замок самим лучше,
    /// чем быть пережданными, — так в журнале видно, успели или нет.
    /// </remarks>
    internal static readonly TimeSpan EndingBudget = TimeSpan.FromSeconds(4);

    private readonly Action<string> _log;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _gate = new();

    private DBusConnection? _system;
    private SafeFileHandle? _shutdownDelay;
    private bool _ended;

    /// <summary>Путь нашего клиента у менеджера сеанса; <see langword="null"/> — не зарегистрированы.</summary>
    private string? _sessionClient;

    /// <summary>Менеджер сеанса прислал <c>EndSession</c> и ждёт ответа.</summary>
    private bool _endSessionPending;

    internal SessionLifetime(Action<string> log)
    {
        _log = log;
    }

    /// <summary>
    /// Машина проснулась. Не в потоке интерфейса.
    /// </summary>
    internal event Action? Resumed;

    /// <summary>
    /// Сеанс кончается: снять регистрацию и выйти. Не в потоке интерфейса.
    /// </summary>
    ///
    /// <remarks>
    /// Обработчик получает повод (для журнала) и возвращает задачу; замок
    /// выключения отпускается, когда она кончится или выйдет
    /// <see cref="EndingBudget"/>. Выход из процесса — дело обработчика.
    /// </remarks>
    internal Func<string, Task>? Ending { get; set; }

    /// <summary>
    /// Подписывается на сигналы. Отсутствие logind — не ошибка: в контейнере
    /// и на стенде его нет, и софтфон там просто живёт без этих событий.
    /// </summary>
    internal async Task StartAsync()
    {
        _subscriptions.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal));
        _subscriptions.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal));

        try
        {
            _system = new DBusConnection(DBusAddress.System!);
            await _system.ConnectAsync().ConfigureAwait(false);

            _subscriptions.Add(await _system.WatchSignalAsync(
                    Logind, ManagerPath, ManagerInterface, "PrepareForSleep",
                    static (message, _) => message.GetBodyReader().ReadBool(),
                    OnPrepareForSleep, ObserverFlags.None, false, null)
                .ConfigureAwait(false));

            _subscriptions.Add(await _system.WatchSignalAsync(
                    Logind, ManagerPath, ManagerInterface, "PrepareForShutdown",
                    static (message, _) => message.GetBodyReader().ReadBool(),
                    OnPrepareForShutdown, ObserverFlags.None, false, null)
                .ConfigureAwait(false));

            await TakeShutdownDelayAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or System.Net.Sockets.SocketException)
        {
            _log($"сеанс: logind недоступен ({error.Message}) — сон и выключение не отслеживаются");
        }

        await RegisterSessionClientAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        ReleaseShutdownDelay();
        _system?.Dispose();

        // UnregisterClient не зовётся: менеджер сеанса снимает клиента сам,
        // когда его имя уходит с шины, то есть с выходом процесса.
    }

    private void OnSignal(PosixSignalContext context)
    {
        // Обычный выход по сигналу — без снятия регистрации. Отменяем его и
        // выходим сами, когда регистрация снята.
        context.Cancel = true;
        BeginEnding(context.Signal is PosixSignal.SIGTERM ? "SIGTERM" : "SIGINT");
    }

    private void OnPrepareForSleep(Notification<bool> signal)
    {
        // true — засыпаем, false — проснулись.
        if (!signal.HasValue || signal.Value)
        {
            return;
        }

        _log("машина проснулась: связь поднимается заново");
        Resumed?.Invoke();
    }

    private void OnPrepareForShutdown(Notification<bool> signal)
    {
        if (!signal.HasValue)
        {
            return;
        }

        if (signal.Value)
        {
            BeginEnding("выключение машины");
            return;
        }

        // Выключение отменили (`shutdown -c`). Замок нужен снова: следующее
        // выключение тоже должно нас подождать.
        _ = TakeShutdownDelayAsync();
    }

    private void BeginEnding(string reason)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
        }

        _ = Task.Run(async () =>
        {
            _log($"сеанс кончается ({reason}): снимается регистрация");

            if (Ending is { } ending)
            {
                var work = ending(reason);
                if (await Task.WhenAny(work, Task.Delay(EndingBudget)).ConfigureAwait(false) != work)
                {
                    _log($"сеанс: регистрация не снялась за {EndingBudget.TotalSeconds:0} с — выход без неё");
                }
            }

            ReleaseShutdownDelay();
            await AnswerEndSessionAsync().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Регистрирует софтфон клиентом менеджера сеанса.
    /// </summary>
    ///
    /// <remarks>
    /// <c>DESKTOP_AUTOSTART_ID</c> — то, чем менеджер сеанса узнаёт свою же
    /// запись автозапуска; запущенный не им (из меню, службой) передаёт пустую
    /// строку и регистрируется как сторонний. Нет менеджера (KDE со своим
    /// протоколом, минимальные рабочие столы) — выход из сеанса придёт
    /// <c>SIGTERM</c> или обрывом X.
    /// </remarks>
    private async Task RegisterSessionClientAsync()
    {
        try
        {
            var connection = DBusConnection.Session;
            var startupId = Environment.GetEnvironmentVariable("DESKTOP_AUTOSTART_ID") ?? string.Empty;

            // Переменная — одноразовая: передать её дальше по наследству
            // значило бы, что дочерний процесс зарегистрируется под нашим именем.
            Environment.SetEnvironmentVariable("DESKTOP_AUTOSTART_ID", null);

            var client = await connection.CallMethodAsync(
                    RegisterMessage(connection, startupId),
                    static (message, _) => message.GetBodyReader().ReadObjectPathAsString(),
                    null)
                .ConfigureAwait(false);

            _sessionClient = client;

            foreach (var (signal, handler) in new (string, Action)[]
            {
                // QueryEndSession — «можно ли выйти». Отвечаем «да» сразу:
                // держать выход оператора софтфон не вправе, он вправе только
                // успеть сказать АТС, что уходит, — это EndSession.
                ("QueryEndSession", () => _ = RespondAsync(true)),
                ("EndSession", () =>
                {
                    _endSessionPending = true;
                    BeginEnding("выход из сеанса");
                }),
                ("Stop", () => BeginEnding("менеджер сеанса просит выйти")),
            })
            {
                _subscriptions.Add(await connection.WatchSignalAsync(
                        SessionManager, client, ClientInterface, signal,
                        notification =>
                        {
                            if (notification.Type is NotificationType.Value)
                            {
                                handler();
                            }
                        },
                        ObserverFlags.None, false, null)
                    .ConfigureAwait(false));
            }
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or System.Net.Sockets.SocketException)
        {
            _log($"сеанс: менеджера сеанса нет ({error.Message}) — выход из сеанса придёт сигналом");
        }
    }

    private async Task AnswerEndSessionAsync()
    {
        if (_endSessionPending)
        {
            _endSessionPending = false;
            await RespondAsync(true).ConfigureAwait(false);
        }
    }

    private async Task RespondAsync(bool isOk)
    {
        if (_sessionClient is not { } client)
        {
            return;
        }

        try
        {
            var connection = DBusConnection.Session;
            await connection.CallMethodAsync(ClientResponse(connection, client, isOk)).ConfigureAwait(false);
        }
        catch (DBusExceptionBase error)
        {
            _log($"сеанс: ответ менеджеру сеанса не ушёл ({error.Message})");
        }
    }

    private static MessageBuffer ClientResponse(DBusConnection connection, string client, bool isOk)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(SessionManager, client, ClientInterface, "EndSessionResponse", "bs", MessageFlags.None);
        writer.WriteBool(isOk);
        writer.WriteString(string.Empty);

        return writer.CreateMessage();
    }

    // Тело пишется здесь же, а не делегатом: MessageWriter — структура, и в
    // делегат ушла бы её копия. Сообщение с заголовком без тела шина считает
    // испорченным и рвёт соединение («Connection closed by peer»).
    private static MessageBuffer RegisterMessage(DBusConnection connection, string startupId)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(SessionManager, SessionManagerPath, SessionManager, "RegisterClient", "ss", MessageFlags.None);
        writer.WriteString("elitesip");
        writer.WriteString(startupId);

        return writer.CreateMessage();
    }


    private async Task TakeShutdownDelayAsync()
    {
        if (_system is null)
        {
            return;
        }

        try
        {
            var handle = await _system.CallMethodAsync(
                    InhibitMessage(_system),
                    static (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>(),
                    null)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _shutdownDelay?.Dispose();
                _shutdownDelay = handle;
            }
        }
        catch (DBusExceptionBase error)
        {
            // Политика polkit может запретить и задерживающий замок. Тогда на
            // выключении регистрация снимается, только если успеет до SIGTERM.
            _log($"сеанс: замок выключения не дан ({error.Message})");
        }
    }

    private static MessageBuffer InhibitMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Logind, ManagerPath, ManagerInterface, "Inhibit", "ssss", MessageFlags.None);
        writer.WriteString("shutdown");
        writer.WriteString("EliteSIP");
        writer.WriteString("снятие регистрации SIP");
        writer.WriteString("delay");

        return writer.CreateMessage();
    }

    private void ReleaseShutdownDelay()
    {
        lock (_gate)
        {
            // Замок — это открытый дескриптор. Закрыли — logind пошёл дальше.
            _shutdownDelay?.Dispose();
            _shutdownDelay = null;
        }
    }
}
