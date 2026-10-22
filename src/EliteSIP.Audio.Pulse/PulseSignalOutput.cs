using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteSIP.Audio.Pulse.Native;
using NAudio.Wave;

namespace EliteSIP.Audio.Pulse;

/// <summary>
/// Служебные звуки Linux-версии: вывод PulseAudio и разбор MP3 через NLayer.
/// </summary>
///
/// <remarks>
/// Проигрыватель общий (<see cref="SignalSoundPlayer"/>, shared/), а здесь —
/// ровно то, чем Windows отличалась: устройство и декодер. NAudio разбирает
/// MP3 системным ACM, которого на Linux нет; NLayer — управляемый декодер, и
/// рингтон MP3, который оператор выбрал на Windows, звучит так же.
/// </remarks>
public static class PulseSignalSounds
{
    /// <summary>
    /// Потолок файла рингтона в памяти. Рингтон — секунды, редко минуты:
    /// MP3 на пять минут весит около 10 МБ. Больше — не рингтон.
    /// </summary>
    internal const long MaximumFileBytes = 64L * 1024 * 1024;

    public static SignalOutputs Outputs { get; } = OutputsFor(null);

    public static SignalSoundPlayer Create(Action<string>? log = null) => new(OutputsFor(log), log);

    private static SignalOutputs OutputsFor(Action<string>? log) => new(
        deviceId => PulseSignalOutput.Open(deviceId, log),
        OpenFile);

    /// <summary>Читатель WAV или MP3 над копией файла в памяти.</summary>
    ///
    /// <remarks>
    /// Файл читается целиком сразу, а не по ходу звучания: отсчёты потом берёт
    /// поток цикла libpulse (<see cref="PulseSignalOutput"/>), и чтение диска
    /// там — это задержка на звуковом потоке, а файл на отмонтированном
    /// ресурсе — исключение в нём. Ошибка открытия здесь доходит до общего
    /// проигрывателя, а он её разбирает: «рингтон не играет», звонок живёт.
    /// </remarks>
    internal static WaveStream OpenFile(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > MaximumFileBytes)
        {
            throw new IOException(
                $"файл рингтона {length / (1024 * 1024)} МБ — больше {MaximumFileBytes / (1024 * 1024)} МБ");
        }

        var memory = new MemoryStream(File.ReadAllBytes(path), writable: false);

        return string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)
            ? new Mp3FileReaderBase(memory, format => new NLayer.NAudioSupport.Mp3FrameDecompressor(format))
            : new WaveFileReader(memory);
    }
}

/// <summary>
/// Один поток вывода PulseAudio под один служебный звук.
/// </summary>
///
/// <remarks>
/// <para>
/// Своё соединение на поток, как у Windows своё устройство WASAPI на звук:
/// гудки и рингтон живут секунды, а держать ради них общее соединение — значит
/// связать их жизнь с трактом разговора, которому они не нужны.
/// </para>
/// <para>
/// Роль <c>event</c>, а не <c>phone</c>: по <c>phone</c> WirePlumber переводит
/// Bluetooth-гарнитуру в режим связи, а рингтону это не нужно — он звучит
/// до разговора, и переключение профиля под ним дало бы щелчок и полсекунды
/// тишины. Частота и число каналов — источника: пересчитывает сервер.
/// </para>
/// </remarks>
internal sealed unsafe class PulseSignalOutput : ISignalOutput
{
    /// <summary>Запас сервера: служебному звуку задержка безразлична, как и на Windows (100 мс).</summary>
    private const double TargetMilliseconds = 100;

    private readonly PulseConnection _connection;
    private GCHandle _self;
    private nint _stream;
    private ISampleProvider? _source;
    private float[] _buffer = [];
    private readonly string? _device;
    private readonly Action<string>? _log;

    private PulseSignalOutput(PulseConnection connection, string? device, Action<string>? log)
    {
        _connection = connection;
        _device = device;
        _log = log;
    }

    internal static PulseSignalOutput Open(string? deviceId, Action<string>? log = null)
    {
        var connection = PulseConnection.Connect("EliteSIP", out var failure)
            ?? throw new InvalidOperationException($"звуковой сервер недоступен: {failure}");

        return new PulseSignalOutput(connection, deviceId is { Length: > 0 } ? deviceId : null, log);
    }

    public void Play(ISampleProvider source)
    {
        _source = source;
        _self = GCHandle.Alloc(this);

        // Выбранного устройства может уже не быть — гарнитуру выдернули между
        // настройкой и звонком. Звонить системным лучше, чем не звонить.
        if (!TryConnect(_device) && (_device is null || !TryConnect(null)))
        {
            throw new InvalidOperationException("поток вывода не открылся: " + Error());
        }
    }

    public void Stop()
    {
        using (_connection.Lock())
        {
            Close();
        }
    }

    public void Dispose()
    {
        Stop();
        _connection.Dispose();

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private bool TryConnect(string? device)
    {
        var format = _source!.WaveFormat;
        var spec = new LibPulse.SampleSpec
        {
            Format = LibPulse.SampleFloat32Le,
            Rate = (uint)format.SampleRate,
            Channels = (byte)Math.Max(1, format.Channels),
        };

        using (_connection.Lock())
        {
            Close();

            nint properties = LibPulse.ProplistNew();
            _ = LibPulse.ProplistSets(properties, "media.role", "event");
            _ = LibPulse.ProplistSets(properties, "media.name", "EliteSIP сигнал");
            _stream = LibPulse.StreamNewWithProplist(_connection.Context, "EliteSIP сигнал", &spec, 0, properties);
            LibPulse.ProplistFree(properties);

            if (_stream == 0)
            {
                return false;
            }

            uint bytes = (uint)(format.SampleRate * spec.Channels * sizeof(float) * TargetMilliseconds / 1000);
            var attr = new LibPulse.BufferAttr
            {
                MaxLength = uint.MaxValue,
                TLength = bytes,
                PreBuf = uint.MaxValue,
                MinReq = uint.MaxValue,
                FragSize = uint.MaxValue,
            };

            LibPulse.StreamSetWriteCallback(_stream, &OnWrite, GCHandle.ToIntPtr(_self));
            if (LibPulse.StreamConnectPlayback(_stream, device, &attr, LibPulse.StreamAdjustLatency, 0, 0) < 0)
            {
                Close();
                return false;
            }
        }

        // Ждём готовности, как тракт: поток, не вставший за секунду, — это
        // устройство, которого нет.
        var deadline = Environment.TickCount64 + 1000;
        while (Environment.TickCount64 < deadline)
        {
            int state;
            using (_connection.Lock())
            {
                state = LibPulse.StreamGetState(_stream);
            }

            if (state == LibPulse.StreamReady)
            {
                return true;
            }

            if (state is LibPulse.StreamFailed or LibPulse.StreamTerminated)
            {
                break;
            }

            Thread.Sleep(2);
        }

        using (_connection.Lock())
        {
            Close();
        }

        return false;
    }

    private void Close()
    {
        if (_stream == 0)
        {
            return;
        }

        LibPulse.StreamSetWriteCallback(_stream, null, 0);
        _ = LibPulse.StreamDisconnect(_stream);
        LibPulse.StreamUnref(_stream);
        _stream = 0;
    }

    private string Error() => LibPulse.StrError(LibPulse.ContextErrno(_connection.Context));

    /// <summary>Поток цикла, под его замком: отдать серверу столько, сколько просит.</summary>
    private void Write(nint stream, nuint requested)
    {
        if (_source is not { } source || requested == 0)
        {
            return;
        }

        void* data;
        nuint bytes = requested;
        if (LibPulse.StreamBeginWrite(stream, &data, &bytes) != 0 || data is null)
        {
            return;
        }

        int channels = Math.Max(1, source.WaveFormat.Channels);
        int samples = (int)bytes / sizeof(float) / channels * channels;
        if (_buffer.Length < samples)
        {
            _buffer = new float[samples];
        }

        var span = _buffer.AsSpan(0, samples);
        if (!Fill(source, span, out var failure))
        {
            // Источник сломался посреди звучания — дальше он молчит, а поток
            // закроет проигрыватель, как у кончившегося звука.
            _source = null;
            Report(failure!);
        }

        span.CopyTo(new Span<float>(data, samples));
        _ = LibPulse.StreamWrite(stream, data, (nuint)(samples * sizeof(float)), 0, 0, LibPulse.SeekRelative);
    }

    /// <summary>
    /// Отсчёты источника в <paramref name="target"/>; недостающее — тишина.
    /// <see langword="false"/> — источник бросил исключение, и вместо него
    /// тишина целиком.
    /// </summary>
    ///
    /// <remarks>
    /// Зовётся из обратного вызова libpulse (<c>UnmanagedCallersOnly</c>), а
    /// исключение оттуда .NET не пропускает: процесс кончается сразу, мимо
    /// <c>CrashGuard</c>. Источник здесь — разбор файла рингтона (NLayer,
    /// NVorbis, WAV), и испорченный в середине файл ронял бы софтфон на
    /// каждом входящем, а служба после трёх падений перестаёт его поднимать.
    /// Поэтому ловится всё: молчащий рингтон лучше мёртвого телефона.
    /// </remarks>
    internal static bool Fill(ISampleProvider source, Span<float> target, out Exception? failure)
    {
        try
        {
            // Источник кончился (тон клавиши, рингтон без повтора) — дописываем
            // тишину: поток закроет проигрыватель, а дыра в потоке — это щелчок.
            int read = source.Read(target);
            target[Math.Clamp(read, 0, target.Length)..].Clear();
            failure = null;
            return true;
        }
#pragma warning disable CA1031 // см. remarks: здесь ловится всё
        catch (Exception error)
#pragma warning restore CA1031
        {
            target.Clear();
            failure = error;
            return false;
        }
    }

    /// <summary>В журнал — не с потока цикла: запись журнала его задержала бы.</summary>
    private void Report(Exception failure)
    {
        if (_log is { } log)
        {
            ThreadPool.QueueUserWorkItem(
                static state => state.log($"служебный звук оборван: источник отказал ({state.failure.GetType().Name}: {state.failure.Message})"),
                (log, failure),
                preferLocal: false);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWrite(nint stream, nuint bytes, nint userdata)
    {
        if (userdata != 0 && GCHandle.FromIntPtr(userdata).Target is PulseSignalOutput output)
        {
            output.Write(stream, bytes);
        }
    }
}
