using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteSIP.Audio.Pulse;
using EliteSIP.Audio.Pulse.Native;

namespace PulseProbe;

/// <summary>
/// Захват и вывод одновременно — как тракт разговора. Вывод играет тишину
/// (или тон, если попросили): слышно ничего не должно быть.
/// </summary>
///
/// <remarks>
/// Что меряется и зачем — вопросы W0 Windows-версии, заданные заново для
/// libpulse:
/// <list type="bullet">
/// <item>такт обратных вызовов захвата: держит ли сервер 10–20 мс без провалов;</item>
/// <item>задержка обеих сторон, которую сервер дал, против той, что просили:
/// квант PipeWire по умолчанию — 1024 отсчёта, 21 мс только на одной стороне;</item>
/// <item>недоборы вывода и переполнения захвата;</item>
/// <item>уход часов каждой карты против часов машины — то, что на Windows
/// пришлось компенсировать (W4, «Компенсация ухода часов»).</item>
/// </list>
/// </remarks>
internal sealed unsafe class DuplexProbe
{
    private const int Rate = 48000;

    private readonly Options _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Всё ниже пишется на потоке цикла libpulse, читается под его замком.
    private readonly List<double> _readIntervals = new(100_000);
    private readonly List<int> _readSizes = new(100_000);
    private double _lastRead = double.NaN;
    private long _capturedFrames;
    private long _writtenFrames;
    private int _holes;
    private int _underflows;
    private int _overflows;
    private float _peak;
    private float _tonePhase;

    internal DuplexProbe(Options options) => _options = options;

    internal sealed record Options(
        string? Source,
        string? Sink,
        int Seconds,
        double CaptureFragmentMs,
        double PlaybackTargetMs,
        bool Tone,
        string Role);

    internal int Run()
    {
        using PulseConnection? connection = PulseConnection.Connect("EliteSIP PulseProbe", out string? failure);
        if (connection is null)
        {
            Console.WriteLine($"сервер недоступен: {failure}");
            return 2;
        }

        GCHandle self = GCHandle.Alloc(this);
        nint userdata = GCHandle.ToIntPtr(self);
        nint capture;
        nint playback;
        try
        {
            using (connection.Lock())
            {
                capture = Open(connection, "захват", userdata, record: true);
                playback = Open(connection, "вывод", userdata, record: false);
                WaitReady(connection, capture, playback);
            }

            Report? report = Measure(connection, capture, playback);

            using (connection.Lock())
            {
                _ = LibPulse.StreamDisconnect(capture);
                _ = LibPulse.StreamDisconnect(playback);
                LibPulse.StreamUnref(capture);
                LibPulse.StreamUnref(playback);
            }

            report?.Print();
            return report is null ? 3 : 0;
        }
        finally
        {
            self.Free();
        }
    }

    private nint Open(PulseConnection connection, string name, nint userdata, bool record)
    {
        var spec = new LibPulse.SampleSpec { Format = LibPulse.SampleFloat32Le, Rate = Rate, Channels = 1 };

        // Роль потока решает маршрут и поведение сервера: «phone» приглушает
        // музыку на время разговора, а WirePlumber по ней переводит
        // Bluetooth-гарнитуру в режим связи. Так же продукт.
        nint properties = LibPulse.ProplistNew();
        _ = LibPulse.ProplistSets(properties, "media.role", _options.Role);
        _ = LibPulse.ProplistSets(properties, "media.name", "EliteSIP " + name);
        nint stream = LibPulse.StreamNewWithProplist(connection.Context, "EliteSIP " + name, &spec, 0, properties);
        LibPulse.ProplistFree(properties);
        if (stream == 0)
        {
            throw new InvalidOperationException($"pa_stream_new ({name}): {LibPulse.StrError(LibPulse.ContextErrno(connection.Context))}");
        }

        LibPulse.StreamSetStateCallback(stream, &OnStreamState, connection.Loop);

        uint bytesPerMs = Rate * sizeof(float) / 1000;
        var attr = new LibPulse.BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = record ? uint.MaxValue : (uint)(_options.PlaybackTargetMs * bytesPerMs),
            PreBuf = uint.MaxValue,
            MinReq = record ? uint.MaxValue : (uint)(_options.CaptureFragmentMs * bytesPerMs),
            FragSize = record ? (uint)(_options.CaptureFragmentMs * bytesPerMs) : uint.MaxValue,
        };

        int flags = LibPulse.StreamAdjustLatency | LibPulse.StreamAutoTimingUpdate | LibPulse.StreamInterpolateTiming;
        int result;
        if (record)
        {
            LibPulse.StreamSetReadCallback(stream, &OnRead, userdata);
            LibPulse.StreamSetOverflowCallback(stream, &OnOverflow, userdata);
            result = LibPulse.StreamConnectRecord(stream, _options.Source, &attr, flags);
        }
        else
        {
            LibPulse.StreamSetWriteCallback(stream, &OnWrite, userdata);
            LibPulse.StreamSetUnderflowCallback(stream, &OnUnderflow, userdata);
            result = LibPulse.StreamConnectPlayback(stream, _options.Sink, &attr, flags, 0, 0);
        }

        if (result < 0)
        {
            throw new InvalidOperationException($"connect ({name}): {LibPulse.StrError(LibPulse.ContextErrno(connection.Context))}");
        }

        return stream;
    }

    private static void WaitReady(PulseConnection connection, nint capture, nint playback)
    {
        while (true)
        {
            int a = LibPulse.StreamGetState(capture);
            int b = LibPulse.StreamGetState(playback);
            if (a == LibPulse.StreamReady && b == LibPulse.StreamReady)
            {
                return;
            }

            if (a is LibPulse.StreamFailed or LibPulse.StreamTerminated || b is LibPulse.StreamFailed or LibPulse.StreamTerminated)
            {
                throw new InvalidOperationException($"поток не поднялся: {LibPulse.StrError(LibPulse.ContextErrno(connection.Context))}");
            }

            connection.Wait();
        }
    }

    private Report? Measure(PulseConnection connection, nint capture, nint playback)
    {
        var samples = new List<Sample>();
        string sourceName;
        string sinkName;
        LibPulse.BufferAttr captureAttr;
        LibPulse.BufferAttr playbackAttr;

        using (connection.Lock())
        {
            sourceName = Marshal.PtrToStringUTF8(LibPulse.StreamGetDeviceNameRaw(capture)) ?? "?";
            sinkName = Marshal.PtrToStringUTF8(LibPulse.StreamGetDeviceNameRaw(playback)) ?? "?";
            captureAttr = *LibPulse.StreamGetBufferAttr(capture);
            playbackAttr = *LibPulse.StreamGetBufferAttr(playback);
        }

        Console.WriteLine($"захват: {sourceName}");
        Console.WriteLine($"вывод:  {sinkName}");
        Console.WriteLine($"прогон {_options.Seconds} с, такт захвата {_options.CaptureFragmentMs} мс, цель вывода {_options.PlaybackTargetMs} мс");

        double start = _clock.Elapsed.TotalSeconds;
        double nextSample = start + 0.25;
        while (_clock.Elapsed.TotalSeconds - start < _options.Seconds)
        {
            // Главный поток стенда спит — это не звуковой контур: такт там
            // задаёт сервер, а здесь только снимаются показания.
            Thread.Sleep(50);
            double now = _clock.Elapsed.TotalSeconds;
            if (now < nextSample)
            {
                continue;
            }

            nextSample += 0.25;
            using (connection.Lock())
            {
                ulong captureLatency = 0, playbackLatency = 0, playbackTime = 0;
                int negative = 0;
                bool okC = LibPulse.StreamGetLatency(capture, &captureLatency, &negative) == 0;
                bool okP = LibPulse.StreamGetLatency(playback, &playbackLatency, &negative) == 0;
                bool okT = LibPulse.StreamGetTime(playback, &playbackTime) == 0;
                samples.Add(new Sample(
                    now - start,
                    okC ? captureLatency / 1000.0 : double.NaN,
                    okP ? playbackLatency / 1000.0 : double.NaN,
                    okT ? playbackTime / 1e6 : double.NaN,
                    _capturedFrames / (double)Rate));
            }
        }

        using (connection.Lock())
        {
            return new Report(
                sourceName,
                sinkName,
                captureAttr,
                playbackAttr,
                [.. _readIntervals],
                [.. _readSizes],
                samples,
                _holes,
                _underflows,
                _overflows,
                _peak,
                _writtenFrames);
        }
    }

    private void Read(nint stream)
    {
        void* data;
        nuint bytes;
        while (LibPulse.StreamPeek(stream, &data, &bytes) == 0 && bytes > 0)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (!double.IsNaN(_lastRead))
            {
                _readIntervals.Add(now - _lastRead);
            }

            _lastRead = now;
            int frames = (int)bytes / sizeof(float);
            _readSizes.Add(frames);
            if (data == null)
            {
                // Дыра в потоке: сервер сообщает, сколько пропало, без данных.
                _holes++;
            }
            else
            {
                var samples = new ReadOnlySpan<float>(data, frames);
                foreach (float s in samples)
                {
                    float a = Math.Abs(s);
                    if (a > _peak)
                    {
                        _peak = a;
                    }
                }
            }

            _capturedFrames += frames;
            _ = LibPulse.StreamDrop(stream);
        }
    }

    private void Write(nint stream, nuint requested)
    {
        void* data;
        nuint bytes = requested;
        if (LibPulse.StreamBeginWrite(stream, &data, &bytes) != 0 || data == null)
        {
            return;
        }

        int frames = (int)bytes / sizeof(float);
        var buffer = new Span<float>(data, frames);
        if (_options.Tone)
        {
            // 440 Гц на −30 dBFS: чтобы человек услышал, что вывод живой, но
            // без риска оглушить в гарнитуре.
            for (int i = 0; i < frames; i++)
            {
                buffer[i] = 0.0316f * MathF.Sin(_tonePhase);
                _tonePhase += 2 * MathF.PI * 440 / Rate;
                if (_tonePhase > 2 * MathF.PI)
                {
                    _tonePhase -= 2 * MathF.PI;
                }
            }
        }
        else
        {
            buffer.Clear();
        }

        _writtenFrames += frames;
        _ = LibPulse.StreamWrite(stream, data, (nuint)(frames * sizeof(float)), 0, 0, LibPulse.SeekRelative);
    }

    private static DuplexProbe Of(nint userdata) => (DuplexProbe)GCHandle.FromIntPtr(userdata).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRead(nint stream, nuint bytes, nint userdata) => Of(userdata).Read(stream);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWrite(nint stream, nuint bytes, nint userdata) => Of(userdata).Write(stream, bytes);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUnderflow(nint stream, nint userdata) => Of(userdata)._underflows++;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOverflow(nint stream, nint userdata) => Of(userdata)._overflows++;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStreamState(nint stream, nint loop) => LibPulse.ThreadedMainloopSignal(loop, 0);

    internal readonly record struct Sample(double Seconds, double CaptureLatencyMs, double PlaybackLatencyMs, double PlaybackClock, double CaptureClock);

    private sealed record Report(
        string Source,
        string Sink,
        LibPulse.BufferAttr CaptureAttr,
        LibPulse.BufferAttr PlaybackAttr,
        double[] Intervals,
        int[] Sizes,
        List<Sample> Samples,
        int Holes,
        int Underflows,
        int Overflows,
        float Peak,
        long WrittenFrames)
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        internal void Print()
        {
            const double bytesPerMs = Rate * sizeof(float) / 1000.0;
            Console.WriteLine();
            Console.WriteLine($"буфер захвата дан: fragsize {CaptureAttr.FragSize / bytesPerMs:F1} мс, maxlength {CaptureAttr.MaxLength / bytesPerMs:F0} мс");
            Console.WriteLine($"буфер вывода дан:  tlength {PlaybackAttr.TLength / bytesPerMs:F1} мс, minreq {PlaybackAttr.MinReq / bytesPerMs:F1} мс, prebuf {PlaybackAttr.PreBuf / bytesPerMs:F1} мс");

            if (Intervals.Length > 0)
            {
                double[] sorted = [.. Intervals.Order()];
                double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
                Console.WriteLine($"такт захвата: обратных вызовов {Intervals.Length + 1}, интервал среднее {Intervals.Average():F2} мс, медиана {P(0.5):F2}, p99 {P(0.99):F2}, максимум {sorted[^1]:F2}");
                Console.WriteLine($"кусок захвата: медиана {Sizes.Order().ElementAt(Sizes.Length / 2)} отсч. ({Sizes.Order().ElementAt(Sizes.Length / 2) * 1000.0 / Rate:F1} мс), максимум {Sizes.Max()} отсч.");
                Console.WriteLine($"провалов такта > 3× медианы: {Intervals.Count(i => i > 3 * P(0.5))}");
            }

            var settled = Samples.Where(s => s.Seconds >= 3).ToList();
            static string Stat(IEnumerable<double> values)
            {
                var v = values.Where(x => !double.IsNaN(x)).ToArray();
                return v.Length == 0 ? "нет данных" : $"среднее {v.Average():F1} мс, мин {v.Min():F1}, макс {v.Max():F1}";
            }

            Console.WriteLine($"задержка захвата (сервер): {Stat(settled.Select(s => s.CaptureLatencyMs))}");
            Console.WriteLine($"задержка вывода (сервер):  {Stat(settled.Select(s => s.PlaybackLatencyMs))}");
            if (settled.Count > 0)
            {
                double c = settled.Where(s => !double.IsNaN(s.CaptureLatencyMs)).Select(s => s.CaptureLatencyMs).DefaultIfEmpty(double.NaN).Average();
                double p = settled.Where(s => !double.IsNaN(s.PlaybackLatencyMs)).Select(s => s.PlaybackLatencyMs).DefaultIfEmpty(double.NaN).Average();
                Console.WriteLine($"сумма двух сторон: {c + p:F1} мс (без кадра кодека и джиттер-буфера)");
            }

            Console.WriteLine($"дыр в захвате: {Holes}, переполнений захвата: {Overflows}, недоборов вывода: {Underflows}");
            Console.WriteLine($"пик захвата: {(Peak > 0 ? 20 * Math.Log10(Peak) : double.NegativeInfinity):F1} dBFS");

            // Уход часов: наклон «время карты» против «время машины». Первые
            // три секунды отбрасываются — там сервер ещё устанавливает буферы.
            Console.WriteLine($"уход часов захвата против машины: {Ppm(settled, s => s.CaptureClock)}");
            Console.WriteLine($"уход часов вывода против машины:  {Ppm(settled, s => s.PlaybackClock)}");
        }

        private static string Ppm(List<Sample> samples, Func<Sample, double> clock)
        {
            var points = samples.Where(s => !double.IsNaN(clock(s))).Select(s => (x: s.Seconds, y: clock(s))).ToList();
            if (points.Count < 8 || points[^1].x - points[0].x < 5)
            {
                return "мало данных";
            }

            double mx = points.Average(p => p.x), my = points.Average(p => p.y);
            double slope = points.Sum(p => (p.x - mx) * (p.y - my)) / points.Sum(p => (p.x - mx) * (p.x - mx));
            return string.Create(Ru, $"{(slope - 1) * 1e6:+0.0;-0.0} ppm на {points[^1].x - points[0].x:F0} с");
        }
    }
}
