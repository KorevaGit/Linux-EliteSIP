using System.Runtime.InteropServices;

namespace EliteSIP.Platform.Linux.Native;

/// <summary>
/// Ровно то из Xlib, что нужно подсказкам оконному менеджеру. Структура
/// сверена с <c>X11/Xlib.h</c> из libx11-dev Ubuntu noble (Mint 22).
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
}
