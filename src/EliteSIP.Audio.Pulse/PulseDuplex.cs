using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteSIP.Audio.Pulse.Native;

namespace EliteSIP.Audio.Pulse;

/// <summary>Наполняет кадр вывода моно-отсчётами. Обязан заполнить его целиком.</summary>
internal delegate void MonoRender(Span<float> destination);

/// <summary>
/// Отказ звукового сервера на сборке тракта.
/// </summary>
/// <param name="message">Что именно не вышло — для журнала.</param>
/// <param name="transient">
/// Может пройти само через секунду: сервер перезапускается, поток не успел
/// встать. «Устройства нет» сюда не относится — повторять бессмысленно.
/// </param>
internal sealed class PulseFailure(string message, bool transient) : Exception(message)
{
    internal bool Transient { get; } = transient;
}

/// <summary>
/// Два потока libpulse одной сборки тракта: микрофон и вывод.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Здесь только ввод-вывод, и это граница с потоком цикла.</b> Обратные
/// вызовы libpulse приходят на поток цикла и держат его замок; всё, что они
/// делают, — копируют отсчёты: захват — в очередь для потока обработки, вывод
/// — из того, что даёт <see cref="MonoRender"/>. Обработка голоса, кодек и
/// подача живут на своих потоках в <see cref="PulseVoiceAudioEngine"/>, как на
/// Windows: медленный кадр APM не должен съедать такт вывода.
/// </para>
/// <para>
/// <b>Форматы просим сами, сервер приводит.</b> Захват — <c>float32</c> моно
/// на 48 кГц, вывод — <c>float32</c> на 48 кГц в раскладке самого устройства.
/// Отказа «формат не поддержан», который на Windows разбирал <c>LayoutOf</c>,
/// здесь не бывает: пересчёт частоты и разрядности делает сервер. Раскладка
/// вывода — устройства, а не стерео: иначе сервер сам разложит стерео на все
/// каналы 7.1-гарнитуры, и вернётся «робот» (см.
/// <see cref="PulseDeviceRules.VoicedChannels"/>).
/// </para>
/// <para>
/// <b>Такт 5 мс, цель вывода 10 мс</b> — настройки, на которых прошла
/// получасовая выдержка W-L0А: 23 мс на обе стороны вместе, ноль провалов.
/// При такте 10 мс одни буферы устройств дают 45 мс — больше всей цели W0.
/// </para>
/// </remarks>
internal sealed unsafe class PulseDuplex : IDisposable
{
    /// <summary>Частота обоих потоков. Сервер пересчитывает к устройству сам.</summary>
    internal const int Rate = 48000;

    /// <summary>Такт захвата и запроса вывода, мс. См. описание типа.</summary>
    internal const double TickMilliseconds = 5;

    /// <summary>Сколько держать в буфере сервера на выводе, мс.</summary>
    internal const double PlaybackTargetMilliseconds = 10;

    /// <summary>Сколько ждать, пока поток встанет. Исправный встаёт за десятки миллисекунд.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);

    private readonly PulseConnection _connection;
    private readonly MonoRender _render;
    private readonly Action<string> _failed;
    private readonly bool[] _voiced;
    private readonly int _outputChannels;
    private GCHandle _self;
    private nint _capture;
    private nint _playback;
    private bool _closing;

    // Очередь захвата: пишет поток цикла, забирает поток обработки.
    private readonly Lock _queueLock = new();
    private readonly float[] _queue = new float[Rate];
    private int _queueCount;
    private int _queueOverruns;

    /// <summary>Моно-кадр для вывода — переиспользуется на каждом запросе.</summary>
    private readonly float[] _mono = new float[Rate];

    private long _captureLatencyMicroseconds;
    private long _playbackLatencyMicroseconds;
    private int _holes;
    private int _underflows;
    private int _overflows;

    private PulseDuplex(PulseConnection connection, MonoRender render, Action<string> failed, IReadOnlyList<int> outputPositions)
    {
        _connection = connection;
        _render = render;
        _failed = failed;
        _outputChannels = Math.Max(1, outputPositions.Count);
        _voiced = outputPositions.Count == 0 ? [true] : PulseDeviceRules.VoicedChannels(outputPositions);
    }

    /// <summary>Микрофон отдал отсчёты — потоку обработки пора проснуться.</summary>
    internal AutoResetEvent CaptureReady { get; } = new(false);

    /// <summary>Что сервер дал на захвате: <c>fragsize</c>, мс.</summary>
    internal double CaptureFragmentMilliseconds { get; private set; }

    /// <summary>Что сервер дал на выводе: <c>tlength</c>, мс.</summary>
    internal double PlaybackLengthMilliseconds { get; private set; }

    /// <summary>Задержка захвата по серверу: что лежит у него и в устройстве, мс.</summary>
    internal double CaptureLatencyMilliseconds => Volatile.Read(ref _captureLatencyMicroseconds) / 1000.0;

    /// <summary>Задержка вывода по серверу: всё записанное, что ещё не прозвучало, мс.</summary>
    internal double PlaybackLatencyMilliseconds => Volatile.Read(ref _playbackLatencyMicroseconds) / 1000.0;

    /// <summary>Каналов у вывода — в раскладке устройства.</summary>
    internal int OutputChannels => _outputChannels;

    /// <summary>Дыр в захвате (сервер сообщил о пропаже без данных).</summary>
    internal int Holes => Volatile.Read(ref _holes);

    /// <summary>Недоборов вывода по серверу.</summary>
    internal int Underflows => Volatile.Read(ref _underflows);

    /// <summary>Переполнений захвата по серверу и по нашей очереди.</summary>
    internal int Overflows => Volatile.Read(ref _overflows) + Volatile.Read(ref _queueOverruns);

    /// <summary>
    /// Открывает оба потока и ждёт, пока они встанут. Бросает
    /// <see cref="PulseFailure"/>; недооткрытое закрывает сам.
    /// </summary>
    /// <param name="failed">
    /// Сервер закрыл поток посреди работы: устройство исчезло, сервер ушёл.
    /// Зовётся на потоке цикла — только передать повод дальше.
    /// </param>
    internal static PulseDuplex Open(
        PulseConnection connection,
        PulseDevice input,
        PulseDevice output,
        MonoRender render,
        Action<string> failed)
    {
        var duplex = new PulseDuplex(connection, render, failed, output.ChannelPositions);
        duplex._self = GCHandle.Alloc(duplex);
        try
        {
            using (connection.Lock())
            {
                duplex._capture = duplex.Connect(input.Name, record: true, positions: []);
                duplex._playback = duplex.Connect(output.Name, record: false, output.ChannelPositions);
            }

            duplex.WaitReady();

            using (connection.Lock())
            {
                const double bytesPerMs = Rate * sizeof(float) / 1000.0;
                LibPulse.BufferAttr* captureAttr = LibPulse.StreamGetBufferAttr(duplex._capture);
                LibPulse.BufferAttr* playbackAttr = LibPulse.StreamGetBufferAttr(duplex._playback);
                duplex.CaptureFragmentMilliseconds = captureAttr is null ? 0 : captureAttr->FragSize / bytesPerMs;
                duplex.PlaybackLengthMilliseconds = playbackAttr is null
                    ? 0
                    : playbackAttr->TLength / (bytesPerMs * duplex._outputChannels);
            }

            return duplex;
        }
        catch
        {
            duplex.Dispose();
            throw;
        }
    }

    private nint Connect(string device, bool record, IReadOnlyList<int> positions)
    {
        string role = record ? "захват" : "вывод";
        int channels = record ? 1 : Math.Max(1, positions.Count);
        var spec = new LibPulse.SampleSpec { Format = LibPulse.SampleFloat32Le, Rate = Rate, Channels = (byte)channels };

        LibPulse.ChannelMap map = default;
        map.Channels = (byte)channels;
        if (record || positions.Count == 0)
        {
            map.Map[0] = LibPulse.PositionMono;
        }
        else
        {
            for (int i = 0; i < positions.Count; i++)
            {
                map.Map[i] = positions[i];
            }
        }

        // Роль «phone»: по ней сервер приглушает музыку на время разговора, а
        // WirePlumber переводит Bluetooth-гарнитуру в режим связи. Эхоподавитель
        // сервера не просим (filter.want): обработка своя, и двойная — это
        // ровно ДВОЙНАЯ обработка, которую на Windows ловили в журнале.
        nint properties = LibPulse.ProplistNew();
        _ = LibPulse.ProplistSets(properties, "media.role", "phone");
        _ = LibPulse.ProplistSets(properties, "media.name", "EliteSIP " + role);
        nint stream = LibPulse.StreamNewWithProplist(_connection.Context, "EliteSIP " + role, &spec, (nint)(&map), properties);
        LibPulse.ProplistFree(properties);

        if (stream == 0)
        {
            throw new PulseFailure($"{role}: {Error()}", transient: true);
        }

        nint userdata = GCHandle.ToIntPtr(_self);
        LibPulse.StreamSetStateCallback(stream, &OnState, userdata);

        uint bytesPerMs = (uint)(Rate * sizeof(float) * channels / 1000);
        var attr = new LibPulse.BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = record ? uint.MaxValue : (uint)(PlaybackTargetMilliseconds * bytesPerMs),
            PreBuf = uint.MaxValue,
            MinReq = record ? uint.MaxValue : (uint)(TickMilliseconds * bytesPerMs),
            FragSize = record ? (uint)(TickMilliseconds * bytesPerMs) : uint.MaxValue,
        };

        // Устройство — явно и без переезда (DONT_MOVE). Сервер умеет сам
        // переносить поток на другое устройство, когда это исчезло, но тогда
        // тракт работал бы на выходе, о котором ничего не знает: эхоподавитель
        // остался бы выключенным для гарнитуры, которой уже нет. Исчезло —
        // поток закрывается, и тракт пересобирается, как на Windows.
        int flags = LibPulse.StreamAdjustLatency
            | LibPulse.StreamAutoTimingUpdate
            | LibPulse.StreamInterpolateTiming
            | LibPulse.StreamDontMove;

        int result;
        if (record)
        {
            LibPulse.StreamSetReadCallback(stream, &OnRead, userdata);
            LibPulse.StreamSetOverflowCallback(stream, &OnOverflow, userdata);
            result = LibPulse.StreamConnectRecord(stream, device, &attr, flags);
        }
        else
        {
            LibPulse.StreamSetWriteCallback(stream, &OnWrite, userdata);
            LibPulse.StreamSetUnderflowCallback(stream, &OnUnderflow, userdata);
            result = LibPulse.StreamConnectPlayback(stream, device, &attr, flags, 0, 0);
        }

        if (result < 0)
        {
            string error = Error();
            LibPulse.StreamUnref(stream);
            throw new PulseFailure($"{role} на «{device}»: {error}", transient: true);
        }

        return stream;
    }

    /// <summary>
    /// Ждёт готовности обоих потоков опросом, а не ожиданием на цикле: у
    /// <c>pa_threaded_mainloop_wait</c> нет предела, а сборка, застрявшая
    /// навсегда, — это зависший отбой. Это сборка, не звуковой контур; пауза
    /// в пару миллисекунд здесь законна.
    /// </summary>
    private void WaitReady()
    {
        var since = Stopwatch.StartNew();
        while (true)
        {
            int capture;
            int playback;
            using (_connection.Lock())
            {
                capture = LibPulse.StreamGetState(_capture);
                playback = LibPulse.StreamGetState(_playback);
            }

            if (capture == LibPulse.StreamReady && playback == LibPulse.StreamReady)
            {
                return;
            }

            if (capture is LibPulse.StreamFailed or LibPulse.StreamTerminated
                || playback is LibPulse.StreamFailed or LibPulse.StreamTerminated)
            {
                string error;
                using (_connection.Lock())
                {
                    error = Error();
                }

                throw new PulseFailure(
                    (capture == LibPulse.StreamReady ? "вывод" : "захват") + " не поднялся: " + error,
                    transient: true);
            }

            if (since.Elapsed > ReadyTimeout)
            {
                throw new PulseFailure(
                    $"потоки не встали за {ReadyTimeout.TotalSeconds:0} с", transient: true);
            }

            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// Забирает накопленный захват. Возвращает число отсчётов.
    /// </summary>
    internal int TakeCaptured(Span<float> destination)
    {
        lock (_queueLock)
        {
            int count = Math.Min(_queueCount, destination.Length);
            _queue.AsSpan(0, count).CopyTo(destination);
            if (count < _queueCount)
            {
                Array.Copy(_queue, count, _queue, 0, _queueCount - count);
            }

            _queueCount -= count;
            return count;
        }
    }

    /// <summary>
    /// Часы обоих устройств одним снимком, в секундах. <c>null</c> — сервер
    /// ещё не прислал данных синхронизации.
    /// </summary>
    ///
    /// <remarks>
    /// Время потока у libpulse — позиция по часам устройства, приведённая
    /// сервером к времени: ровно то, что на Windows давал <c>IAudioClock</c>.
    /// Оба читаются под одним замком, чтобы между ними не прошёл такт.
    /// </remarks>
    internal (double Capture, double Playback)? ReadClocks()
    {
        using (_connection.Lock())
        {
            if (_closing)
            {
                return null;
            }

            ulong capture = 0;
            ulong playback = 0;
            if (LibPulse.StreamGetTime(_capture, &capture) != 0 || LibPulse.StreamGetTime(_playback, &playback) != 0)
            {
                return null;
            }

            return (capture / 1e6, playback / 1e6);
        }
    }

    public void Dispose()
    {
        if (_connection is null)
        {
            return;
        }

        using (_connection.Lock())
        {
            _closing = true;
            Close(ref _capture);
            Close(ref _playback);
        }

        // Обратные вызовы сняты под замком цикла — новых не будет, и ссылку
        // на себя можно отпускать.
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        CaptureReady.Dispose();
    }

    private static void Close(ref nint stream)
    {
        if (stream == 0)
        {
            return;
        }

        LibPulse.StreamSetStateCallback(stream, null, 0);
        LibPulse.StreamSetReadCallback(stream, null, 0);
        LibPulse.StreamSetWriteCallback(stream, null, 0);
        LibPulse.StreamSetUnderflowCallback(stream, null, 0);
        LibPulse.StreamSetOverflowCallback(stream, null, 0);
        _ = LibPulse.StreamDisconnect(stream);
        LibPulse.StreamUnref(stream);
        stream = 0;
    }

    private string Error() => LibPulse.StrError(LibPulse.ContextErrno(_connection.Context));

    // MARK: - Обратные вызовы: поток цикла, под его замком

    private void Read(nint stream)
    {
        void* data;
        nuint bytes;
        bool delivered = false;

        while (LibPulse.StreamPeek(stream, &data, &bytes) == 0 && bytes > 0)
        {
            int count = (int)bytes / sizeof(float);
            lock (_queueLock)
            {
                // Поток обработки не успевает забирать — он застрял. Старое
                // выбрасывается: опоздавший звук в разговоре хуже потерянного.
                int free = _queue.Length - _queueCount;
                if (count > free)
                {
                    int drop = Math.Min(_queueCount, count - free);
                    Array.Copy(_queue, drop, _queue, 0, _queueCount - drop);
                    _queueCount -= drop;
                    _queueOverruns++;
                }

                int take = Math.Min(count, _queue.Length - _queueCount);
                Span<float> target = _queue.AsSpan(_queueCount, take);
                if (data is null)
                {
                    // Дыра: сервер сообщает, сколько пропало, без данных.
                    // Тишина на её месте держит такт и баланс отсчётов.
                    target.Clear();
                    _holes++;
                }
                else
                {
                    new ReadOnlySpan<float>(data, take).CopyTo(target);
                }

                _queueCount += take;
            }

            delivered = true;
            _ = LibPulse.StreamDrop(stream);
        }

        ulong latency = 0;
        int negative = 0;
        if (LibPulse.StreamGetLatency(stream, &latency, &negative) == 0)
        {
            Volatile.Write(ref _captureLatencyMicroseconds, negative != 0 ? 0 : (long)latency);
        }

        if (delivered)
        {
            CaptureReady.Set();
        }
    }

    private void Write(nint stream, nuint requested)
    {
        int frameBytes = sizeof(float) * _outputChannels;
        int frames = Math.Min((int)requested / frameBytes, _mono.Length);
        if (frames <= 0)
        {
            return;
        }

        void* data;
        nuint bytes = (nuint)(frames * frameBytes);
        if (LibPulse.StreamBeginWrite(stream, &data, &bytes) != 0 || data is null)
        {
            return;
        }

        frames = Math.Min(frames, (int)bytes / frameBytes);
        Span<float> mono = _mono.AsSpan(0, frames);
        _render(mono);

        var destination = new Span<float>(data, frames * _outputChannels);
        for (int i = 0; i < frames; i++)
        {
            float sample = mono[i];
            int offset = i * _outputChannels;
            for (int c = 0; c < _outputChannels; c++)
            {
                destination[offset + c] = _voiced[c] ? sample : 0f;
            }
        }

        _ = LibPulse.StreamWrite(stream, data, (nuint)(frames * frameBytes), 0, 0, LibPulse.SeekRelative);

        ulong latency = 0;
        int negative = 0;
        if (LibPulse.StreamGetLatency(stream, &latency, &negative) == 0)
        {
            Volatile.Write(ref _playbackLatencyMicroseconds, negative != 0 ? 0 : (long)latency);
        }
    }

    private void StateChanged(nint stream)
    {
        LibPulse.ThreadedMainloopSignal(_connection.Loop, 0);

        if (_closing)
        {
            return;
        }

        int state = LibPulse.StreamGetState(stream);
        if (state is LibPulse.StreamFailed or LibPulse.StreamTerminated)
        {
            string role = stream == _capture ? "захват" : "вывод";
            _failed($"{role}: сервер закрыл поток ({Error()})");
        }
    }

    private static PulseDuplex? Of(nint userdata) =>
        userdata == 0 ? null : GCHandle.FromIntPtr(userdata).Target as PulseDuplex;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRead(nint stream, nuint bytes, nint userdata) => Of(userdata)?.Read(stream);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWrite(nint stream, nuint bytes, nint userdata) => Of(userdata)?.Write(stream, bytes);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnState(nint stream, nint userdata) => Of(userdata)?.StateChanged(stream);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUnderflow(nint stream, nint userdata)
    {
        if (Of(userdata) is PulseDuplex duplex)
        {
            Interlocked.Increment(ref duplex._underflows);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOverflow(nint stream, nint userdata)
    {
        if (Of(userdata) is PulseDuplex duplex)
        {
            Interlocked.Increment(ref duplex._overflows);
        }
    }
}
