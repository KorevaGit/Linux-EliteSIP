using System.Runtime.InteropServices;

namespace EliteSIP.Platform.Linux.Native;

/// <summary>
/// Ровно то из Xlib, что нужно подсказкам оконному менеджеру и слежению за
/// мышью на входящем. Структуры сверены с <c>X11/Xlib.h</c> из libx11-dev
/// Ubuntu noble (Mint 22).
/// </summary>
internal static unsafe partial class X11
{
    private const string Library = "libX11.so.6";

    internal const int ClientMessage = 33;
    internal const nint SubstructureNotifyMask = 1 << 19;
    internal const nint SubstructureRedirectMask = 1 << 20;

    /// <summary><c>XClientMessageEvent</c> с данными в 32-битном формате.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XClientMessageEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint MessageType;
        public int Format;
        public nint Data0;
        public nint Data1;
        public nint Data2;
        public nint Data3;
        public nint Data4;
    }

    /// <summary><c>XEvent</c> — объединение на 24 длинных слова.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24 * 8)]
    internal struct XEvent
    {
        [FieldOffset(0)]
        public XClientMessageEvent ClientMessage;
    }

    [LibraryImport(Library, EntryPoint = "XOpenDisplay")]
    internal static partial nint XOpenDisplay(nint name);

    [LibraryImport(Library, EntryPoint = "XCloseDisplay")]
    internal static partial int XCloseDisplay(nint display);

    [LibraryImport(Library, EntryPoint = "XDefaultRootWindow")]
    internal static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(Library, EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nuint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(Library, EntryPoint = "XSendEvent")]
    internal static partial int XSendEvent(nint display, nuint window, int propagate, nint eventMask, XEvent* sent);

    /// <summary>Основная клавиатура для вызовов XKB (<c>XkbUseCoreKbd</c>).</summary>
    internal const uint XkbUseCoreKbd = 0x0100;

    [LibraryImport(Library, EntryPoint = "XkbGetIndicatorState")]
    internal static partial int XkbGetIndicatorState(nint display, uint device, uint* state);

    [LibraryImport(Library, EntryPoint = "XFlush")]
    internal static partial int XFlush(nint display);

    internal const int PropModeReplace = 0;

    /// <summary><c>XGenericEventCookie</c> — так приходят события XInput2.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XGenericEventCookie
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public int Extension;
        public int EvType;
        public uint Cookie;
        public void* Data;
    }

    internal const int GenericEvent = 35;

    /// <summary>Размер <c>XEvent</c> в байтах: 24 длинных слова.</summary>
    internal const int EventSize = 24 * 8;

    [LibraryImport(Library, EntryPoint = "XSync")]
    internal static partial int XSync(nint display, int discard);

    [LibraryImport(Library, EntryPoint = "XPending")]
    internal static partial int XPending(nint display);

    [LibraryImport(Library, EntryPoint = "XNextEvent")]
    internal static partial int XNextEvent(nint display, byte* buffer);

    [LibraryImport(Library, EntryPoint = "XConnectionNumber")]
    internal static partial int XConnectionNumber(nint display);

    [LibraryImport(Library, EntryPoint = "XGetEventData")]
    internal static partial int XGetEventData(nint display, XGenericEventCookie* cookie);

    [LibraryImport(Library, EntryPoint = "XFreeEventData")]
    internal static partial void XFreeEventData(nint display, XGenericEventCookie* cookie);

    [LibraryImport(Library, EntryPoint = "XQueryExtension", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int XQueryExtension(nint display, string name, int* opcode, int* firstEvent, int* firstError);

    [LibraryImport(Library, EntryPoint = "XQueryPointer")]
    internal static partial int XQueryPointer(
        nint display, nuint window, nuint* root, nuint* child, int* rootX, int* rootY, int* x, int* y, uint* mask);

    [LibraryImport(Library, EntryPoint = "XGetWindowProperty")]
    internal static partial int XGetWindowProperty(
        nint display, nuint window, nuint property, nint offset, nint length, int delete, nuint requestedType,
        nuint* actualType, int* actualFormat, nuint* items, nuint* bytesAfter, byte** data);

    [LibraryImport(Library, EntryPoint = "XChangeProperty")]
    internal static partial int XChangeProperty(
        nint display, nuint window, nuint property, nuint type, int format, int mode, byte* data, int count);

    [LibraryImport(Library, EntryPoint = "XGetAtomName")]
    internal static partial nint XGetAtomName(nint display, nuint atom);

    [LibraryImport(Library, EntryPoint = "XFree")]
    internal static partial int XFree(void* data);

    /// <summary>
    /// Значения свойства формата 32. У Xlib это <c>long</c>, то есть 8 байт на
    /// x64, хотя по проводу идёт по 4.
    /// </summary>
    internal static nuint[] Property(nint display, nuint window, string name)
    {
        var atom = XInternAtom(display, name, 0);
        nuint type, items, after;
        int format;
        byte* data;
        if (XGetWindowProperty(display, window, atom, 0, 1024, 0, 0, &type, &format, &items, &after, &data) != 0 || data == null)
        {
            return [];
        }

        try
        {
            if (format != 32)
            {
                return [];
            }

            var values = new nuint[(int)items];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = ((nuint*)data)[i];
            }

            return values;
        }
        finally
        {
            _ = XFree(data);
        }
    }
}
