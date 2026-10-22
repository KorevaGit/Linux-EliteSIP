using System.Runtime.InteropServices;

namespace EliteSIP.Platform.Linux.Native;

/// <summary>
/// XInput2 и XScreenSaver — то, на чём стоит слежение за мышью на входящем.
/// Структуры сверены с <c>X11/extensions/XInput2.h</c> (libxi-dev) и
/// <c>X11/extensions/scrnsaver.h</c> (libxss-dev) Ubuntu noble (Mint 22);
/// стенд W-L0В (<c>tools/WindowProbe</c>) снимал с них же.
/// </summary>
internal static unsafe partial class XInput
{
    private const string Xi = "libXi.so.6";
    private const string Xss = "libXss.so.1";

    internal const int ButtonPress = 4;
    internal const int RawButtonPress = 15;
    internal const int RawMotion = 17;
    internal const int AllDevices = 0;
    internal const int AllMasterDevices = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct XIEventMask
    {
        public int DeviceId;
        public int MaskLen;
        public byte* Mask;
    }

    /// <summary>Начало <c>XIRawEvent</c> — до <c>flags</c> включительно.</summary>
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

    /// <summary>Начало <c>XIDeviceEvent</c> — до <c>flags</c> включительно.</summary>
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

    /// <summary><c>XIDeviceInfo</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XIDeviceInfo
    {
        public int DeviceId;
        public nint Name;
        public int Use;
        public int Attachment;
        public int Enabled;
        public int NumClasses;
        public nint Classes;
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

    [LibraryImport(Xi, EntryPoint = "XIQueryVersion")]
    internal static partial int XIQueryVersion(nint display, int* major, int* minor);

    [LibraryImport(Xi, EntryPoint = "XISelectEvents")]
    internal static partial int XISelectEvents(nint display, nuint window, XIEventMask* masks, int count);

    [LibraryImport(Xi, EntryPoint = "XIQueryDevice")]
    internal static partial XIDeviceInfo* XIQueryDevice(nint display, int device, int* count);

    [LibraryImport(Xi, EntryPoint = "XIFreeDeviceInfo")]
    internal static partial void XIFreeDeviceInfo(XIDeviceInfo* info);

    [LibraryImport(Xss, EntryPoint = "XScreenSaverQueryExtension")]
    internal static partial int XScreenSaverQueryExtension(nint display, int* firstEvent, int* firstError);

    [LibraryImport(Xss, EntryPoint = "XScreenSaverAllocInfo")]
    internal static partial XScreenSaverInfo* XScreenSaverAllocInfo();

    [LibraryImport(Xss, EntryPoint = "XScreenSaverQueryInfo")]
    internal static partial int XScreenSaverQueryInfo(nint display, nuint drawable, XScreenSaverInfo* info);

    /// <summary>Маска одного или нескольких событий XI2 на четыре байта.</summary>
    internal static void Set(Span<byte> mask, int eventType) => mask[eventType >> 3] |= (byte)(1 << (eventType & 7));
}
