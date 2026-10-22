using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EliteSIP.Platform.Linux.Native;

namespace EliteSIP.Platform.Linux;

/// <summary>Шаг курсора в пикселях корневого окна и то, откуда он пришёл.</summary>
internal readonly record struct PointerMotion(int X, int Y, bool FromXTest);

/// <summary>Нажатие левой кнопкой по окну входящего.</summary>
///
/// <param name="SourceId">Ведомое устройство XInput2, от которого пришло нажатие.</param>
/// <param name="Device">Его имя — для журнала.</param>
/// <param name="FromXTest">Пришло от «Virtual core XTEST pointer»: <c>xdotool</c>,
/// <c>XTestFakeButtonEvent</c>, большинство автокликеров и x11vnc.</param>
/// <param name="At">Когда разобрано, по <see cref="Stopwatch.GetTimestamp"/>.</param>
internal readonly record struct PointerPress(int SourceId, string Device, bool FromXTest, long At);

/// <summary>
/// Слежение за мышью на время одного входящего вызова — Linux-половина
/// <c>Incoming/PointerWatch</c> Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// Сбор фактов, а не решение: разбирает их общая <c>CallGuardSession</c>.
/// Вместо низкоуровневого хука <c>WH_MOUSE_LL</c> — XInput2 на своём
/// соединении с X-сервером (W-L0В):
/// </para>
/// <list type="bullet">
/// <item><b><c>XI_RawMotion</c> на корневом окне</b> — путь курсора над
/// <i>любым</i> окном. Сырое событие координат не несёт, поэтому за каждым
/// идёт <c>XQueryPointer</c>. Обычное <c>XI_Motion</c> на корень не годится:
/// оно до корня не доходит, если окно под курсором само слушает движения, а
/// слушает почти каждое;</item>
/// <item><b><c>XI_ButtonPress</c> на окне входящего</b> — нажатие с
/// настоящим <c>sourceid</c>.</item>
/// </list>
/// <para>
/// <b>Здесь Linux сильнее Windows.</b> <c>LLMHF_INJECTED</c> помечал только
/// нажатие, а X-сервер ставит источник и у пути: ввод через XTest приходит
/// от «Virtual core XTEST pointer», и подделать это можно только устройством
/// ядра (<c>uinput</c>, <c>ydotool</c>) — так же, как драйверным кликером на
/// Windows. Пометку ставит сервер, а не программа.
/// </para>
/// <para>
/// Своё соединение, а не соединение Avalonia: чужое соединение трогать с
/// другого потока нельзя, а события XI2 одному клиенту не мешают другому.
/// Все вызовы Xlib на нём — под одним замком: фоновый поток разбирает
/// очередь, а <see cref="Sync"/> из потока окна дочитывает её перед
/// решением.
/// </para>
/// </remarks>
internal sealed unsafe class PointerWatch : IDisposable
{
    private readonly Lock _gate = new();
    private readonly nint _display;
    private readonly nuint _root;
    private readonly int _opcode;
    private readonly HashSet<int> _xtest;
    private readonly Dictionary<int, string> _names;
    private readonly ConcurrentQueue<PointerMotion> _motions = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private nuint _window;
    private PointerPress? _press;
    private bool _closed;

    private PointerWatch(nint display, int opcode, Dictionary<int, string> names)
    {
        _display = display;
        _root = X11.XDefaultRootWindow(display);
        _opcode = opcode;
        _names = names;
        _xtest = [.. names.Where(device => IsXTest(device.Value)).Select(device => device.Key)];
        _thread = new Thread(Loop) { IsBackground = true, Name = "pointer-watch" };
    }

    /// <summary>Устройства XTest нашлись — без них признак синтетики слеп.</summary>
    internal bool KnowsXTest => _xtest.Count > 0;

    /// <summary>
    /// Встаёт на корневом окне. <see langword="null"/> и причина — если X-сервера
    /// или XInput 2.2 нет: защита тогда держится на одной случайной позиции, и
    /// знать об этом должен журнал, а не догадка.
    /// </summary>
    internal static PointerWatch? Start(out string? failure)
    {
        failure = null;
        var display = X11.XOpenDisplay(0);
        if (display == 0)
        {
            failure = "нет X-дисплея (DISPLAY не задан или сеанс Wayland)";
            return null;
        }

        int opcode, firstEvent, firstError;
        if (X11.XQueryExtension(display, "XInputExtension", &opcode, &firstEvent, &firstError) == 0)
        {
            _ = X11.XCloseDisplay(display);
            failure = "у X-сервера нет XInputExtension";
            return null;
        }

        int major = 2, minor = 2;
        if (XInput.XIQueryVersion(display, &major, &minor) != 0)
        {
            _ = X11.XCloseDisplay(display);
            failure = $"XInput {major}.{minor}, нужен 2.2";
            return null;
        }

        var watch = new PointerWatch(display, opcode, Devices(display));

        // Только ведущие устройства: ведомое повторяет то же событие ещё раз,
        // а источник у копии ведущего тот же — sourceid.
        Span<byte> mask = stackalloc byte[4];
        mask.Clear();
        XInput.Set(mask, XInput.RawMotion);
        fixed (byte* bits = mask)
        {
            var selection = new XInput.XIEventMask { DeviceId = XInput.AllMasterDevices, MaskLen = 4, Mask = bits };
            _ = XInput.XISelectEvents(display, watch._root, &selection, 1);
        }

        _ = X11.XFlush(display);
        watch._thread.Start();
        return watch;
    }

    /// <summary>Где сейчас курсор, в пикселях корневого окна.</summary>
    internal (int X, int Y)? Cursor()
    {
        lock (_gate)
        {
            return _closed ? null : QueryPointer();
        }
    }

    /// <summary>Слушать нажатия на окне входящего (XID) — с настоящим источником.</summary>
    internal void WatchWindow(nuint window)
    {
        lock (_gate)
        {
            if (_closed || window == 0)
            {
                return;
            }

            _window = window;
            Span<byte> mask = stackalloc byte[4];
            mask.Clear();
            XInput.Set(mask, XInput.ButtonPress);
            fixed (byte* bits = mask)
            {
                var selection = new XInput.XIEventMask { DeviceId = XInput.AllMasterDevices, MaskLen = 4, Mask = bits };
                _ = XInput.XISelectEvents(_display, window, &selection, 1);
            }

            _ = X11.XFlush(_display);
        }
    }

    /// <summary>
    /// Дочитывает всё, что X-сервер успел прислать к этому мгновению.
    /// </summary>
    ///
    /// <remarks>
    /// Зовётся перед разбором попытки. Нажатие и отпускание приходят Avalonia
    /// по её соединению, а нам — по своему, и <c>xdotool click</c> шлёт их
    /// подряд без паузы: щелчок кнопки может сработать раньше, чем фоновый
    /// поток разобрал нажатие. После <c>XSync</c> всё, что сервер разослал до
    /// отпускания, уже в нашей очереди.
    /// </remarks>
    internal void Sync()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _ = X11.XSync(_display, 0);
            DrainLocked();
        }
    }

    /// <summary>Отдаёт накопленный путь по порядку.</summary>
    internal void TakeMotions(Action<PointerMotion> each)
    {
        while (_motions.TryDequeue(out var motion))
        {
            each(motion);
        }
    }

    /// <summary>
    /// Последнее нажатие по окну, не старше <paramref name="maximumAge"/>, —
    /// и забыть его.
    /// </summary>
    ///
    /// <remarks>
    /// Забывается, чтобы одно настоящее нажатие не засчиталось второй
    /// попытке, пришедшей без мыши (AT-SPI, клавиатура).
    /// </remarks>
    internal PointerPress? TakePress(TimeSpan maximumAge)
    {
        lock (_gate)
        {
            var press = _press;
            _press = null;
            return press is { } found && Stopwatch.GetElapsedTime(found.At) <= maximumAge ? found : null;
        }
    }

    /// <summary>
    /// Сколько X-сервер не видел ввода вообще — ни мыши, ни клавиатуры.
    /// Аналог <c>GetLastInputInfo</c>; <see langword="null"/> — расширения
    /// XScreenSaver нет.
    /// </summary>
    ///
    /// <remarks>
    /// Барьером не служит: XTest-ввод его сбрасывает так же, как
    /// <c>SendInput</c> на Windows (W-L0В), а оператор вправе смотреть в экран
    /// не шевелясь. Пишется в журнал рядом с путём.
    /// </remarks>
    internal TimeSpan? SystemIdleTime()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            int firstEvent, firstError;
            if (XInput.XScreenSaverQueryExtension(_display, &firstEvent, &firstError) == 0)
            {
                return null;
            }

            var info = XInput.XScreenSaverAllocInfo();
            if (info == null)
            {
                return null;
            }

            try
            {
                return XInput.XScreenSaverQueryInfo(_display, _root, info) == 0
                    ? null
                    : TimeSpan.FromMilliseconds(info->Idle);
            }
            finally
            {
                _ = X11.XFree(info);
            }
        }
    }

    internal string DeviceName(int id) => _names.TryGetValue(id, out var name) ? name : $"#{id}";

    /// <summary>Устройство ввода XTest — по имени, которое ему даёт сам сервер.</summary>
    internal static bool IsXTest(string name) => name.Contains("XTEST", StringComparison.Ordinal);

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(500);

        lock (_gate)
        {
            if (!_closed)
            {
                _closed = true;
                _ = X11.XCloseDisplay(_display);
            }
        }
    }

    private static Dictionary<int, string> Devices(nint display)
    {
        var names = new Dictionary<int, string>();
        int count;
        var devices = XInput.XIQueryDevice(display, XInput.AllDevices, &count);
        if (devices == null)
        {
            return names;
        }

        for (var i = 0; i < count; i++)
        {
            names[devices[i].DeviceId] = Marshal.PtrToStringUTF8(devices[i].Name) ?? string.Empty;
        }

        XInput.XIFreeDeviceInfo(devices);
        return names;
    }

    private void Loop()
    {
        var descriptor = X11.XConnectionNumber(_display);
        while (!_stopping)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                DrainLocked();
            }

            // Ждём сокет, а не спим: путь курсора — до тысячи событий в
            // секунду у игровой мыши, и пауза в цикле съела бы точки пути.
            // Предел — чтобы Dispose не ждал следующего движения.
            var poll = new Libc.PollFd { Fd = descriptor, Events = Libc.PollIn };
            _ = Libc.Poll(&poll, 1, 100);
        }
    }

    private void DrainLocked()
    {
        var buffer = stackalloc byte[X11.EventSize];
        while (X11.XPending(_display) > 0)
        {
            _ = X11.XNextEvent(_display, buffer);
            var cookie = (X11.XGenericEventCookie*)buffer;
            if (cookie->Type != X11.GenericEvent || cookie->Extension != _opcode || X11.XGetEventData(_display, cookie) == 0)
            {
                continue;
            }

            try
            {
                switch (cookie->EvType)
                {
                    case XInput.RawMotion:
                        var raw = (XInput.XIRawEventHead*)cookie->Data;
                        if (QueryPointer() is var (x, y))
                        {
                            _motions.Enqueue(new PointerMotion(x, y, _xtest.Contains(raw->SourceId)));
                        }

                        break;

                    case XInput.ButtonPress:
                        var device = (XInput.XIDeviceEventHead*)cookie->Data;
                        if (device->Detail == 1 && device->Event == _window)
                        {
                            _press = new PointerPress(
                                device->SourceId,
                                DeviceName(device->SourceId),
                                _xtest.Contains(device->SourceId),
                                Stopwatch.GetTimestamp());
                        }

                        break;

                    default:
                        break;
                }
            }
            finally
            {
                X11.XFreeEventData(_display, cookie);
            }
        }
    }

    private (int X, int Y)? QueryPointer()
    {
        nuint root, child;
        int rootX, rootY, x, y;
        uint mask;
        return X11.XQueryPointer(_display, _root, &root, &child, &rootX, &rootY, &x, &y, &mask) != 0
            ? (rootX, rootY)
            : null;
    }
}
