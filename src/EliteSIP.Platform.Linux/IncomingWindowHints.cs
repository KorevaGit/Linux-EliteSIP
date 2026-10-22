using EliteSIP.Platform.Linux.Native;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Подсказки оконному менеджеру для окна входящего — то, что на Windows
/// давали <c>WS_EX_NOACTIVATE</c>, <c>MA_NOACTIVATE</c> и
/// <c>WS_EX_TOOLWINDOW</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// <b><c>WM_HINTS.input = False</c></b> — главное. Muffin, как и большинство
/// менеджеров, отдаёт фокус окну, по которому щёлкнули, если окно не сказало
/// «ввод с клавиатуры мне не нужен» (W-L0Б). Avalonia этого не говорит, а
/// оператор в этот момент печатает в CRM: активация чужого окна посреди
/// набора недопустима. Побочный плюс — окно без фокуса не получает и
/// клавиатуры, так что клавиатурного пути к «Ответить» нет и у X-сервера.
/// </para>
/// <para>
/// <b><c>_NET_WM_STATE_SKIP_PAGER</c></b> — вместе с <c>SKIP_TASKBAR</c>,
/// который ставит сама Avalonia (<c>ShowInTaskbar=False</c>), убирает
/// карточку из Alt+Tab и из обзора рабочих столов, как <c>WS_EX_TOOLWINDOW</c>.
/// Тип окна остаётся обычным: служебный тип менеджер вправе держать над
/// «хозяином» и прятать вместе с ним, а хозяина у карточки нет.
/// </para>
/// <para>
/// <b><c>_NET_WM_STATE_STICKY</c></b> — нового на Linux: у Cinnamon несколько
/// рабочих столов, и карточка, показанная на одном, пропадала бы, стоит
/// оператору перейти на другой. Вызов виден на любом.
/// </para>
/// <para>
/// Ставятся дважды: свойствами до показа (их менеджер читает при отображении
/// окна) и сообщением после — Avalonia при показе пишет свои
/// <c>_NET_WM_STATE</c> и <c>WM_HINTS</c>, и порядок между нами не
/// обещан. Итог читается обратно и уходит в журнал: подсказку, которую
/// кто-то переписал, иначе не заметить.
/// </para>
/// </remarks>
internal static unsafe class IncomingWindowHints
{
    private const nuint InputHint = 1;
    private const int Add = 1;
    private const int HintsLength = 9;

    private static readonly string[] States = ["_NET_WM_STATE_SKIP_PAGER", "_NET_WM_STATE_STICKY"];

    /// <summary>До показа: свойствами окна.</summary>
    internal static void BeforeShow(nuint window) => With(window, display =>
    {
        SetNoInput(display, window);

        var state = X11.XInternAtom(display, "_NET_WM_STATE", 0);
        var atom = X11.XInternAtom(display, "ATOM", 0);
        var current = X11.Property(display, window, "_NET_WM_STATE").ToList();
        foreach (var name in States)
        {
            var value = X11.XInternAtom(display, name, 0);
            if (!current.Contains(value))
            {
                current.Add(value);
            }
        }

        var values = current.ToArray();
        fixed (nuint* data = values)
        {
            _ = X11.XChangeProperty(display, window, state, atom, 32, X11.PropModeReplace, (byte*)data, values.Length);
        }

        return true;
    });

    /// <summary>После показа: ещё раз <c>WM_HINTS</c> и просьба менеджеру по EWMH.</summary>
    internal static void AfterShow(nuint window) => With(window, display =>
    {
        SetNoInput(display, window);

        foreach (var name in States)
        {
            Request(display, window, name);
        }

        return true;
    });

    /// <summary>Что стоит на окне сейчас — строка для журнала.</summary>
    // не переводится: журнал.
    internal static string Describe(nuint window)
    {
        string? text = null;
        With(window, display =>
        {
            var hints = X11.Property(display, window, "WM_HINTS");
            var input = hints.Length > 1 && (hints[0] & InputHint) != 0 ? (hints[1] == 0 ? "False" : "True") : "не задан";
            var state = X11.Property(display, window, "_NET_WM_STATE").Select(atom => Short(AtomName(display, atom)));
            text = $"WM_HINTS.input {input}, _NET_WM_STATE {string.Join(" ", state)}";
            return true;
        });

        return text ?? "окна нет";
    }

    /// <summary>
    /// Ввод с клавиатуры окну не нужен. Остальные поля <c>WM_HINTS</c>
    /// (значок, группа, начальное состояние) остаются как есть.
    /// </summary>
    private static void SetNoInput(nint display, nuint window)
    {
        var current = X11.Property(display, window, "WM_HINTS");
        var values = new nuint[HintsLength];
        Array.Copy(current, values, Math.Min(current.Length, HintsLength));
        values[0] |= InputHint;
        values[1] = 0;

        var atom = X11.XInternAtom(display, "WM_HINTS", 0);
        fixed (nuint* data = values)
        {
            _ = X11.XChangeProperty(display, window, atom, atom, 32, X11.PropModeReplace, (byte*)data, HintsLength);
        }
    }

    private static void Request(nint display, nuint window, string state)
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
            Data0 = Add,
            Data1 = (nint)X11.XInternAtom(display, state, 0),
            Data2 = 0,
            Data3 = 1,
        };

        _ = X11.XSendEvent(
            display,
            X11.XDefaultRootWindow(display),
            propagate: 0,
            X11.SubstructureRedirectMask | X11.SubstructureNotifyMask,
            &message);
    }

    private static string Short(string atom) => atom.StartsWith("_NET_WM_STATE_", StringComparison.Ordinal) ? atom[14..] : atom;

    private static string AtomName(nint display, nuint atom)
    {
        var name = X11.XGetAtomName(display, atom);
        if (name == 0)
        {
            return $"#{atom}";
        }

        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(name) ?? "?";
        }
        finally
        {
            _ = X11.XFree((void*)name);
        }
    }

    private static void With(nuint window, Func<nint, bool> action)
    {
        if (window == 0)
        {
            return;
        }

        var display = X11.XOpenDisplay(0);
        if (display == 0)
        {
            return;
        }

        try
        {
            _ = action(display);
            _ = X11.XSync(display, 0);
        }
        finally
        {
            _ = X11.XCloseDisplay(display);
        }
    }
}
