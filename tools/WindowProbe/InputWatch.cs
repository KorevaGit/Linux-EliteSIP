using System.Collections.Concurrent;

namespace WindowProbe;

/// <summary>
/// Слушатель XInput2 на своём соединении с X-сервером.
/// </summary>
///
/// <remarks>
/// Два вида событий, и оба нужны защите приёма:
/// <list type="bullet">
/// <item><b>сырые на корневом окне</b> (<c>XI_RawMotion</c>,
/// <c>XI_RawButtonPress</c>) — путь курсора над <i>любым</i> окном, как
/// низкоуровневый хук <c>WH_MOUSE_LL</c> на Windows: оператор в этот момент
/// работает в CRM, и путь над чужим окном нужен не меньше, чем над своим;</item>
/// <item><b>обычные на своём окне</b> (<c>XI_ButtonPress</c>) — у них есть
/// настоящий <c>sourceid</c>: ведомое устройство, от которого пришло нажатие.
/// Нажатие XTest приходит от «Virtual core XTEST pointer» — это и есть аналог
/// <c>LLMHF_INJECTED</c>.</item>
/// </list>
/// </remarks>
internal sealed unsafe class InputWatch : IDisposable
{
    private readonly nint _display;
    private readonly int _opcode;
    private readonly Thread _thread;
    private volatile bool _stop;

    internal ConcurrentQueue<InputEvent> Events { get; } = new();

    internal readonly record struct InputEvent(string Kind, int DeviceId, int SourceId, double X, double Y, DateTime At);

    private InputWatch(nint display, int opcode)
    {
        _display = display;
        _opcode = opcode;
        _thread = new Thread(Loop) { IsBackground = true, Name = "xi2" };
    }

    internal static InputWatch? Start(out string? failure)
    {
        failure = null;
        nint display = Xlib.XOpenDisplay(0);
        if (display == 0)
        {
            failure = "нет X-дисплея (DISPLAY не задан или это Wayland без XWayland)";
            return null;
        }

        int opcode, ev, err;
        if (Xlib.XQueryExtension(display, "XInputExtension", &opcode, &ev, &err) == 0)
        {
            failure = "нет XInputExtension";
            return null;
        }

        int major = 2, minor = 2;
        if (Xlib.XIQueryVersion(display, &major, &minor) != 0)
        {
            failure = $"XInput {major}.{minor} — нужен 2.2";
            return null;
        }

        var watch = new InputWatch(display, opcode);
        byte* mask = stackalloc byte[4];
        new Span<byte>(mask, 4).Clear();
        mask[Xlib.XI_RawMotion >> 3] |= (byte)(1 << (Xlib.XI_RawMotion & 7));
        mask[Xlib.XI_RawButtonPress >> 3] |= (byte)(1 << (Xlib.XI_RawButtonPress & 7));
        var selection = new Xlib.XIEventMask { DeviceId = Xlib.XIAllDevices, MaskLen = 4, Mask = mask };
        _ = Xlib.XISelectEvents(display, Xlib.XDefaultRootWindow(display), &selection, 1);
        _ = Xlib.XFlush(display);
        watch._thread.Start();
        return watch;
    }

    /// <summary>Слушать нажатия на своём окне — с настоящим источником.</summary>
    internal void WatchWindow(nuint window)
    {
        byte* mask = stackalloc byte[4];
        new Span<byte>(mask, 4).Clear();
        mask[Xlib.XI_ButtonPress >> 3] |= (byte)(1 << (Xlib.XI_ButtonPress & 7));
        var selection = new Xlib.XIEventMask { DeviceId = Xlib.XIAllMasterDevices, MaskLen = 4, Mask = mask };
        _ = Xlib.XISelectEvents(_display, window, &selection, 1);
        _ = Xlib.XFlush(_display);
    }

    private void Loop()
    {
        byte* buffer = stackalloc byte[192];
        while (!_stop)
        {
            if (Xlib.XPending(_display) == 0)
            {
                Thread.Sleep(2);
                continue;
            }

            _ = Xlib.XNextEvent(_display, buffer);
            var cookie = (Xlib.XGenericEventCookie*)buffer;
            if (cookie->Type != Xlib.GenericEvent || cookie->Extension != _opcode || Xlib.XGetEventData(_display, cookie) == 0)
            {
                continue;
            }

            try
            {
                switch (cookie->EvType)
                {
                    case Xlib.XI_RawMotion:
                    case Xlib.XI_RawButtonPress:
                        var raw = (Xlib.XIRawEventHead*)cookie->Data;
                        Events.Enqueue(new InputEvent(
                            cookie->EvType == Xlib.XI_RawMotion ? "raw-motion" : "raw-press",
                            raw->DeviceId, raw->SourceId, double.NaN, double.NaN, DateTime.UtcNow));
                        break;
                    case Xlib.XI_ButtonPress:
                        var device = (Xlib.XIDeviceEventHead*)cookie->Data;
                        Events.Enqueue(new InputEvent("press", device->DeviceId, device->SourceId, device->RootX, device->RootY, DateTime.UtcNow));
                        break;
                }
            }
            finally
            {
                Xlib.XFreeEventData(_display, cookie);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
        _ = Xlib.XCloseDisplay(_display);
    }
}
