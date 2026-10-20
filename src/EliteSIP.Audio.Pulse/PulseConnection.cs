using EliteSIP.Audio.Pulse.Native;

namespace EliteSIP.Audio.Pulse;

/// <summary>
/// Подключение к звуковому серверу: свой поток цикла libpulse и контекст.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Threaded mainloop, а не свой цикл.</b> libpulse сам держит поток, на
/// котором приходят обратные вызовы захвата и вывода, — и тактирует их сервер
/// по устройству. Это то же правило, что на Windows держал event-driven WASAPI:
/// никакого <c>Thread.Sleep</c> в звуковом контуре, такт задаёт карта.
/// </para>
/// <para>
/// Всё, что трогает контекст или поток извне обратного вызова, делается под
/// замком цикла (<see cref="Lock"/>): так требует libpulse, и это не
/// перестраховка — без замка чтение задержки посреди обновления данных
/// синхронизации отдаёт мусор.
/// </para>
/// </remarks>
internal sealed unsafe class PulseConnection : IDisposable
{
    private readonly nint _loop;
    private readonly nint _context;
    private bool _disposed;

    private PulseConnection(nint loop, nint context)
    {
        _loop = loop;
        _context = context;
    }

    internal nint Loop => _loop;

    internal nint Context => _context;

    /// <summary>
    /// Подключается к серверу и ждёт готовности. <c>null</c> — сервера нет или
    /// он отказал; причина — в <paramref name="failure"/>.
    /// </summary>
    internal static PulseConnection? Connect(string applicationName, out string? failure)
    {
        failure = null;
        nint loop = LibPulse.ThreadedMainloopNew();
        if (loop == 0)
        {
            failure = "pa_threaded_mainloop_new вернул NULL";
            return null;
        }

        LibPulse.ThreadedMainloopSetName(loop, "EliteSIP-pulse");

        nint properties = LibPulse.ProplistNew();
        _ = LibPulse.ProplistSets(properties, "application.name", applicationName);
        _ = LibPulse.ProplistSets(properties, "application.id", "vip.elitesip.EliteSIP");
        _ = LibPulse.ProplistSets(properties, "application.icon_name", "elitesip");
        nint context = LibPulse.ContextNewWithProplist(LibPulse.ThreadedMainloopGetApi(loop), applicationName, properties);
        LibPulse.ProplistFree(properties);

        if (context == 0)
        {
            LibPulse.ThreadedMainloopFree(loop);
            failure = "pa_context_new вернул NULL";
            return null;
        }

        var connection = new PulseConnection(loop, context);
        LibPulse.ContextSetStateCallback(context, &OnContextState, loop);

        LibPulse.ThreadedMainloopLock(loop);
        try
        {
            // Без автозапуска сервера: софтфон, поднявший себе собственный
            // PulseAudio рядом с PipeWire, получил бы второй звук в системе.
            if (LibPulse.ContextConnect(context, 0, LibPulse.ContextNoAutospawn, 0) < 0
                || LibPulse.ThreadedMainloopStart(loop) < 0)
            {
                failure = LibPulse.StrError(LibPulse.ContextErrno(context));
                LibPulse.ThreadedMainloopUnlock(loop);
                connection.Dispose();
                return null;
            }

            while (true)
            {
                int state = LibPulse.ContextGetState(context);
                if (state == LibPulse.ContextReady)
                {
                    break;
                }

                if (state is LibPulse.ContextFailed or LibPulse.ContextTerminated)
                {
                    failure = LibPulse.StrError(LibPulse.ContextErrno(context));
                    LibPulse.ThreadedMainloopUnlock(loop);
                    connection.Dispose();
                    return null;
                }

                LibPulse.ThreadedMainloopWait(loop);
            }
        }
        finally
        {
            if (failure is null)
            {
                LibPulse.ThreadedMainloopUnlock(loop);
            }
        }

        return connection;
    }

    /// <summary>Замок цикла libpulse. Отпускается через <see cref="IDisposable.Dispose"/>.</summary>
    internal LoopLock Lock()
    {
        LibPulse.ThreadedMainloopLock(_loop);
        return new LoopLock(_loop);
    }

    internal void Wait() => LibPulse.ThreadedMainloopWait(_loop);

    internal void Signal() => LibPulse.ThreadedMainloopSignal(_loop, 0);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Цикл останавливается до освобождения контекста: иначе последний
        // обратный вызов пришёл бы в уже освобождённое.
        LibPulse.ThreadedMainloopStop(_loop);
        LibPulse.ContextDisconnect(_context);
        LibPulse.ContextUnref(_context);
        LibPulse.ThreadedMainloopFree(_loop);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnContextState(nint context, nint loop) => LibPulse.ThreadedMainloopSignal(loop, 0);

    internal readonly struct LoopLock(nint loop) : IDisposable
    {
        public void Dispose() => LibPulse.ThreadedMainloopUnlock(loop);
    }
}
