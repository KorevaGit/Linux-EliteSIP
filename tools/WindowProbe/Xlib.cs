using System.Runtime.InteropServices;

namespace WindowProbe;

/// <summary>
/// Xlib, XInput2, XTest и XScreenSaver — ровно то, что нужно стенду.
/// Структуры сверены 19 октября 2026 с заголовками libx11-dev, libxi-dev,
/// libxss-dev и libxtst-dev из Ubuntu noble (Mint 22).
/// </summary>
internal static unsafe partial class Xlib
{
    private const string X11 = "libX11.so.6";
    private const string Xi = "libXi.so.6";
    private const string Xtst = "libXtst.so.6";
    private const string Xss = "libXss.so.1";

    internal const int GenericEvent = 35;
    internal const int XI_ButtonPress = 4;
    internal const int XI_Motion = 6;
    internal const int XI_RawButtonPress = 15;
    internal const int XI_RawMotion = 17;
    internal const int XIAllDevices = 0;
    internal const int XIAllMasterDevices = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct XIEventMask
    {
        public int DeviceId;
        public int MaskLen;
        public byte* Mask;
    }

    /// <summary><c>XGenericEventCookie</c>.</summary>
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

    /// <summary>Начало <c>XIRawEvent</c> — до <c>detail</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XIRawEventHead
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public int Extension;
        public int EvType;
        public nuint Time;
        public int DeviceId;
        public int SourceId;
        public int Detail;
        public int Flags;
    }

    /// <summary>Начало <c>XIDeviceEvent</c> — до координат.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XIDeviceEventHead
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public int Extension;
        public int EvType;
        public nuint Time;
        public int DeviceId;
        public int SourceId;
        public int Detail;
        public nuint Root;
        public nuint Event;
        public nuint Child;
        public double RootX;
        public double RootY;
        public double EventX;
        public double EventY;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XScreenSaverInfo
    {
        public nuint Window;
        public int State;
        public int Kind;
        public nuint TilOrSince;
        public nuint Idle;
        public nuint EventMask;
    }

    [LibraryImport(X11, EntryPoint = "XInitThreads")]
    internal static partial int XInitThreads();

    [LibraryImport(X11, EntryPoint = "XOpenDisplay")]
    internal static partial nint XOpenDisplay(nint name);

    [LibraryImport(X11, EntryPoint = "XCloseDisplay")]
    internal static partial int XCloseDisplay(nint display);

    [LibraryImport(X11, EntryPoint = "XDefaultRootWindow")]
    internal static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(X11, EntryPoint = "XFlush")]
    internal static partial int XFlush(nint display);

    [LibraryImport(X11, EntryPoint = "XPending")]
    internal static partial int XPending(nint display);

    [LibraryImport(X11, EntryPoint = "XNextEvent")]
    internal static partial int XNextEvent(nint display, byte* eventBuffer);

    [LibraryImport(X11, EntryPoint = "XGetEventData")]
    internal static partial int XGetEventData(nint display, XGenericEventCookie* cookie);

    [LibraryImport(X11, EntryPoint = "XFreeEventData")]
    internal static partial void XFreeEventData(nint display, XGenericEventCookie* cookie);

    [LibraryImport(X11, EntryPoint = "XQueryExtension", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int XQueryExtension(nint display, string name, int* opcode, int* firstEvent, int* firstError);

    [LibraryImport(X11, EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nuint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(X11, EntryPoint = "XGetAtomName")]
    internal static partial nint XGetAtomName(nint display, nuint atom);

    [LibraryImport(X11, EntryPoint = "XFree")]
    internal static partial int XFree(void* data);

    [LibraryImport(X11, EntryPoint = "XGetWindowProperty")]
    internal static partial int XGetWindowProperty(
        nint display, nuint window, nuint property, nint offset, nint length, int delete, nuint requestedType,
        nuint* actualType, int* actualFormat, nuint* items, nuint* bytesAfter, byte** data);

    [LibraryImport(X11, EntryPoint = "XGetInputFocus")]
    internal static partial int XGetInputFocus(nint display, nuint* focus, int* revert);

    [LibraryImport(X11, EntryPoint = "XTranslateCoordinates")]
    internal static partial int XTranslateCoordinates(nint display, nuint source, nuint destination, int x, int y, int* rx, int* ry, nuint* child);

    [LibraryImport(X11, EntryPoint = "XQueryPointer")]
    internal static partial int XQueryPointer(nint display, nuint window, nuint* root, nuint* child, int* rootX, int* rootY, int* x, int* y, uint* mask);

    [LibraryImport(Xi, EntryPoint = "XIQueryVersion")]
    internal static partial int XIQueryVersion(nint display, int* major, int* minor);

    [LibraryImport(Xi, EntryPoint = "XISelectEvents")]
    internal static partial int XISelectEvents(nint display, nuint window, XIEventMask* masks, int count);

    [LibraryImport(Xtst, EntryPoint = "XTestFakeMotionEvent")]
    internal static partial int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);

    [LibraryImport(Xtst, EntryPoint = "XTestFakeButtonEvent")]
    internal static partial int XTestFakeButtonEvent(nint display, uint button, int isPress, nuint delay);

    [LibraryImport(Xss, EntryPoint = "XScreenSaverAllocInfo")]
    internal static partial XScreenSaverInfo* XScreenSaverAllocInfo();

    [LibraryImport(Xss, EntryPoint = "XScreenSaverQueryInfo")]
    internal static partial int XScreenSaverQueryInfo(nint display, nuint drawable, XScreenSaverInfo* info);

    internal static string AtomName(nint display, nuint atom)
    {
        nint name = XGetAtomName(display, atom);
        if (name == 0)
        {
            return $"#{atom}";
        }

        string text = Marshal.PtrToStringUTF8(name) ?? "?";
        _ = XFree((void*)name);
        return text;
    }

    /// <summary>Значения свойства формата 32 — у Xlib это <c>long</c>, то есть 8 байт на x64.</summary>
    internal static nuint[] Property(nint display, nuint window, string name)
    {
        nuint atom = XInternAtom(display, name, 0);
        nuint type, items, after;
        int format;
        byte* data;
        if (XGetWindowProperty(display, window, atom, 0, 1024, 0, 0, &type, &format, &items, &after, &data) != 0 || data == null)
        {
            return [];
        }

        var values = new nuint[(int)items];
        if (format == 32)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ((nuint*)data)[i];
            }
        }

        _ = XFree(data);
        return values;
    }
}
