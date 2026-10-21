using EliteSIP.Platform.Linux.Native;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Горит ли Caps Lock — для подсказки под полем пароля «Управления», как
/// <c>Keyboard.IsKeyToggled(Key.CapsLock)</c> на Windows.
/// </summary>
///
/// <remarks>
/// Avalonia состояние фиксирующих клавиш не отдаёт. На X11 оно — индикатор
/// XKB: бит 0 у <c>XkbGetIndicatorState</c> основной клавиатуры — Caps Lock
/// почти во всех раскладках (индикатор «Caps Lock» первый в списке xkb).
/// Нет X-сервера — «не горит»: подсказка просто не покажется.
/// </remarks>
internal static class CapsLock
{
    internal static unsafe bool IsOn()
    {
        var display = X11.XOpenDisplay(0);
        if (display == 0)
        {
            return false;
        }

        try
        {
            uint state = 0;
            return X11.XkbGetIndicatorState(display, X11.XkbUseCoreKbd, &state) == 0 && (state & 1) != 0;
        }
        finally
        {
            _ = X11.XCloseDisplay(display);
        }
    }
}
