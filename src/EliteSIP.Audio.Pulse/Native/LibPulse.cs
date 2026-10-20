using System.Runtime.InteropServices;

namespace EliteSIP.Audio.Pulse.Native;

/// <summary>
/// Объявления libpulse — ровно те, что нужны тракту.
/// </summary>
///
/// <remarks>
/// <para>
/// Сверены 19 октября 2026 с заголовками <c>libpulse-dev</c> 16.1 (Ubuntu
/// noble, его ставит Mint 22): структуры, числа перечислений и флаги — оттуда,
/// а не по памяти. Ошибка в раскладке структуры здесь не падает, а тихо даёт
/// неверный буфер или неверную задержку, и искать её потом по звуку.
/// </para>
/// <para>
/// Готовой поддерживаемой обёртки под .NET нет, поэтому свой P/Invoke — та же
/// роль, что у NAudio на Windows. Разделяемая библиотека — <c>libpulse.so.0</c>:
/// она есть и на PulseAudio-системах, и на PipeWire (пакет <c>libpulse0</c>
/// стоит рядом с <c>pipewire-pulse</c>).
/// </para>
/// </remarks>
internal static unsafe partial class LibPulse
{
    private const string Library = "libpulse.so.0";

    // pa_sample_format_t (sample.h): U8, ALAW, ULAW, S16LE, S16BE, FLOAT32LE...
    internal const int SampleS16Le = 3;
    internal const int SampleFloat32Le = 5;

    // pa_context_state_t (def.h).
    internal const int ContextReady = 4;
    internal const int ContextFailed = 5;
    internal const int ContextTerminated = 6;

    // pa_stream_state_t (def.h).
    internal const int StreamReady = 2;
    internal const int StreamFailed = 3;
    internal const int StreamTerminated = 4;

    // pa_context_flags_t.
    internal const int ContextNoAutospawn = 0x1;

    // pa_stream_flags_t.
    internal const int StreamStartCorked = 0x1;
    internal const int StreamInterpolateTiming = 0x2;
    internal const int StreamAutoTimingUpdate = 0x8;
    internal const int StreamAdjustLatency = 0x2000;
    internal const int StreamDontMove = 0x200;

    internal const int SeekRelative = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    /// <summary>
    /// <c>pa_buffer_attr</c>. <see cref="uint.MaxValue"/> в поле — «на усмотрение
    /// сервера» (<c>(uint32_t) -1</c> в заголовке).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    // --- threaded mainloop -------------------------------------------------

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_new")]
    internal static partial nint ThreadedMainloopNew();

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_free")]
    internal static partial void ThreadedMainloopFree(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_start")]
    internal static partial int ThreadedMainloopStart(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_stop")]
    internal static partial void ThreadedMainloopStop(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_lock")]
    internal static partial void ThreadedMainloopLock(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_unlock")]
    internal static partial void ThreadedMainloopUnlock(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_wait")]
    internal static partial void ThreadedMainloopWait(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_signal")]
    internal static partial void ThreadedMainloopSignal(nint loop, int waitForAccept);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_get_api")]
    internal static partial nint ThreadedMainloopGetApi(nint loop);

    [LibraryImport(Library, EntryPoint = "pa_threaded_mainloop_set_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void ThreadedMainloopSetName(nint loop, string name);

    // --- proplist -----------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "pa_proplist_new")]
    internal static partial nint ProplistNew();

    [LibraryImport(Library, EntryPoint = "pa_proplist_free")]
    internal static partial void ProplistFree(nint list);

    [LibraryImport(Library, EntryPoint = "pa_proplist_sets", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ProplistSets(nint list, string key, string value);

    // --- context ------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "pa_context_new_with_proplist", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ContextNewWithProplist(nint api, string name, nint proplist);

    [LibraryImport(Library, EntryPoint = "pa_context_set_state_callback")]
    internal static partial void ContextSetStateCallback(nint context, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_context_connect")]
    internal static partial int ContextConnect(nint context, nint server, int flags, nint spawnApi);

    [LibraryImport(Library, EntryPoint = "pa_context_get_state")]
    internal static partial int ContextGetState(nint context);

    [LibraryImport(Library, EntryPoint = "pa_context_errno")]
    internal static partial int ContextErrno(nint context);

    [LibraryImport(Library, EntryPoint = "pa_context_disconnect")]
    internal static partial void ContextDisconnect(nint context);

    [LibraryImport(Library, EntryPoint = "pa_context_unref")]
    internal static partial void ContextUnref(nint context);

    [LibraryImport(Library, EntryPoint = "pa_strerror")]
    internal static partial nint StrErrorRaw(int error);

    internal static string StrError(int error) => Marshal.PtrToStringUTF8(StrErrorRaw(error)) ?? $"ошибка {error}";

    // --- stream -------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "pa_stream_new_with_proplist", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint StreamNewWithProplist(nint context, string name, SampleSpec* spec, nint channelMap, nint proplist);

    [LibraryImport(Library, EntryPoint = "pa_stream_set_state_callback")]
    internal static partial void StreamSetStateCallback(nint stream, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_stream_set_read_callback")]
    internal static partial void StreamSetReadCallback(nint stream, delegate* unmanaged[Cdecl]<nint, nuint, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_stream_set_write_callback")]
    internal static partial void StreamSetWriteCallback(nint stream, delegate* unmanaged[Cdecl]<nint, nuint, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_stream_set_underflow_callback")]
    internal static partial void StreamSetUnderflowCallback(nint stream, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_stream_set_overflow_callback")]
    internal static partial void StreamSetOverflowCallback(nint stream, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint userdata);

    [LibraryImport(Library, EntryPoint = "pa_stream_connect_record", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StreamConnectRecord(nint stream, string? device, BufferAttr* attr, int flags);

    [LibraryImport(Library, EntryPoint = "pa_stream_connect_playback", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StreamConnectPlayback(nint stream, string? device, BufferAttr* attr, int flags, nint volume, nint syncStream);

    [LibraryImport(Library, EntryPoint = "pa_stream_get_state")]
    internal static partial int StreamGetState(nint stream);

    [LibraryImport(Library, EntryPoint = "pa_stream_peek")]
    internal static partial int StreamPeek(nint stream, void** data, nuint* bytes);

    [LibraryImport(Library, EntryPoint = "pa_stream_drop")]
    internal static partial int StreamDrop(nint stream);

    [LibraryImport(Library, EntryPoint = "pa_stream_begin_write")]
    internal static partial int StreamBeginWrite(nint stream, void** data, nuint* bytes);

    [LibraryImport(Library, EntryPoint = "pa_stream_write")]
    internal static partial int StreamWrite(nint stream, void* data, nuint bytes, nint freeCallback, long offset, int seek);

    [LibraryImport(Library, EntryPoint = "pa_stream_get_latency")]
    internal static partial int StreamGetLatency(nint stream, ulong* microseconds, int* negative);

    [LibraryImport(Library, EntryPoint = "pa_stream_get_time")]
    internal static partial int StreamGetTime(nint stream, ulong* microseconds);

    [LibraryImport(Library, EntryPoint = "pa_stream_get_buffer_attr")]
    internal static partial BufferAttr* StreamGetBufferAttr(nint stream);

    [LibraryImport(Library, EntryPoint = "pa_stream_get_sample_spec")]
    internal static partial SampleSpec* StreamGetSampleSpec(nint stream);

    [LibraryImport(Library, EntryPoint = "pa_stream_get_device_name")]
    internal static partial nint StreamGetDeviceNameRaw(nint stream);

    [LibraryImport(Library, EntryPoint = "pa_stream_disconnect")]
    internal static partial int StreamDisconnect(nint stream);

    [LibraryImport(Library, EntryPoint = "pa_stream_unref")]
    internal static partial void StreamUnref(nint stream);
}
