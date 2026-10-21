using EliteSIP.Platform.Linux.Native;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// «Мигание кнопки» панели на входящем — то, что на Windows делал
/// <c>FlashWindowEx</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// На X11 окно просит внимания состоянием <c>_NET_WM_STATE_DEMANDS_ATTENTION</c>
/// по EWMH. Ставит его не само окно, а оконный менеджер по просьбе:
/// клиентское сообщение <c>_NET_WM_STATE</c> корневому окну. Muffin
/// (Cinnamon) подсвечивает кнопку окна в панели задач, пока окно не
/// активируют или пока состояние не снимут, — ровно как
/// <c>FLASHW_TRAY | FLASHW_TIMERNOFG</c> на Windows.
/// </para>
/// <para>
/// Просит внимания <b>панель</b>, а не окно входящего, по той же причине, что
/// на Windows: у карточки вызова своей кнопки в панели задач нет. И снимается
/// оно явно — на отбой, приём в другом месте или конец вызова, — иначе
/// кнопка горела бы, как будто вызов ещё ждёт.
/// </para>
/// <para>
/// Своё соединение с X-сервером, а не соединение Avalonia: событие корневому
/// окну доходит одинаково от любого клиента, а чужое соединение трогать с
/// другого потока нельзя.
/// </para>
/// </remarks>
internal static class WindowAttention
{
    private const int Remove = 0;
    private const int Add = 1;

    /// <summary>Просить внимания к окну <paramref name="window"/> (XID).</summary>
    internal static bool Request(nuint window) => Send(window, Add);

    /// <summary>Снять просьбу.</summary>
    internal static bool Cancel(nuint window) => Send(window, Remove);

    private static unsafe bool Send(nuint window, int action)
    {
        if (window == 0)
        {
            return false;
        }

        var display = X11.XOpenDisplay(0);
        if (display == 0)
        {
            return false;
        }

        try
        {
            X11.XEvent message = default;
            message.ClientMessage = new X11.XClientMessageEvent
            {
                Type = X11.ClientMessage,
                SendEvent = 1,
                Display = display,
                Window = window,
                MessageType = X11.XInternAtom(display, "_NET_WM_STATE", 0),
                Format = 32,
                Data0 = action,
                Data1 = (nint)X11.XInternAtom(display, "_NET_WM_STATE_DEMANDS_ATTENTION", 0),
                Data2 = 0,

                // Источник просьбы: 1 — обычное приложение (EWMH, «source
                // indication»). Менеджер вправе по-разному верить приложению и
                // пейджеру.
                Data3 = 1,
            };

            var sent = X11.XSendEvent(
                display,
                X11.XDefaultRootWindow(display),
                propagate: 0,
                X11.SubstructureRedirectMask | X11.SubstructureNotifyMask,
                &message);

            return sent != 0 && X11.XFlush(display) >= 0;
        }
        finally
        {
            _ = X11.XCloseDisplay(display);
        }
    }
}
