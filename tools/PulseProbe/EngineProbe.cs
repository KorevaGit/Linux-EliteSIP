using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using EliteSIP.Audio;
using EliteSIP.Audio.Pulse;
using EliteSIP.MediaCore;

namespace PulseProbe;

/// <summary>
/// Продуктовый тракт целиком на живом сервере — без SIP, с подставной «сетью».
/// </summary>
///
/// <remarks>
/// <para>
/// То же, что делал стенд W4 Windows-версии: поднять настоящий движок, отдать
/// ему кадры по такту отправителя (20 мс по часам машины, а не по запросу —
/// иначе регулятору темпа нечего ловить) и забрать итог. Что он отвечает:
/// поднимается ли тракт, сходится ли баланс отсчётов, сколько недоборов у
/// сервера, какую задержку тракт объявляет эхоподавителю и как ведёт себя
/// темп приёма.
/// </para>
/// <para>
/// По умолчанию «собеседник» молчит (кадры тишины): стенд можно гонять в общем
/// кабинете. <c>--tone</c> — тон 440 Гц на −20 dBFS, <c>--loopback</c> —
/// микрофон обратно в наушники с задержкой сети (только в гарнитуре!).
/// </para>
/// </remarks>
internal sealed class EngineProbe(EngineProbe.Options options)
{
    internal sealed record Options(int Seconds, AudioCodec Codec, string? Input, string? Output, bool Tone, bool Loopback);

    private readonly ConcurrentQueue<byte[]> _network = new();
    private readonly ConcurrentQueue<string> _events = new();
    private long _sent;
    private long _played;

    internal int Run()
    {
        var configuration = new VoiceAudioConfiguration
        {
            Codec = options.Codec,
            InputDeviceId = options.Input,
            OutputDeviceId = options.Output,
        };

        using var engine = new PulseVoiceAudioEngine(configuration);
        var clock = Stopwatch.StartNew();

        engine.Handlers = new VoiceAudioHandlers
        {
            Diagnostic = text => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F2}] {text}"),
            Event = e => _events.Enqueue($"[{clock.Elapsed.TotalSeconds,6:F2}] событие: {e}"),
            EncodedFrame = payload =>
            {
                Interlocked.Increment(ref _sent);
                if (options.Loopback)
                {
                    _network.Enqueue(payload.ToArray());
                }
            },
            NeedsFrame = NextFrame,
            Backlog = () => _network.IsEmpty && _sent == 0
                ? null
                : new PlaybackBacklog(_network.Count, configuration.TargetPlaybackFrames),
        };

        try
        {
            engine.Start();
        }
        catch (VoiceAudioException e)
        {
            Console.WriteLine($"тракт не поднялся: {e.Message}");
            return 2;
        }

        // «Отправитель» — свой поток по часам машины: кадры приходят в темпе
        // 20 мс, как из сети, а не тогда, когда тракт попросит.
        using var stop = new CancellationTokenSource();
        Thread sender = options.Loopback ? new Thread(() => { }) : new Thread(() => Send(configuration, stop.Token)) { IsBackground = true };
        sender.Start();

        double nextReport = 5;
        while (clock.Elapsed.TotalSeconds < options.Seconds)
        {
            Thread.Sleep(250);
            while (_events.TryDequeue(out string? line))
            {
                Console.WriteLine(line);
            }

            if (clock.Elapsed.TotalSeconds >= nextReport)
            {
                nextReport += 5;
                AudioLatencySnapshot latency = engine.Latency;
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{clock.Elapsed.TotalSeconds,6:F2}] ушло {_sent} кадров, сыграно {_played}, "
                        + $"задержка захват {latency.CaptureMilliseconds:F1} + вывод {latency.PlaybackMilliseconds:F1} мс, "
                        + $"эхоподавителю {engine.DeclaredDelayMilliseconds} мс, темп {engine.RateController?.CorrectionPpm:+0;-0;0} ppm"));
            }
        }

        stop.Cancel();
        Console.WriteLine();
        Console.WriteLine(engine.Summary());

        double expected = (clock.Elapsed.TotalSeconds - (PulseVoiceAudioEngine.StartDiscardMilliseconds / 1000.0))
            * 1000 / configuration.PacketTimeMilliseconds;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"кадров в сеть: {_sent} из ожидаемых ~{expected:F0} ({_sent / expected * 100:F1} %)"));

        engine.Stop();
        return engine.Balance?.IsBalanced == false ? 3 : 0;
    }

    private PlaybackFrame? NextFrame()
    {
        if (!_network.TryDequeue(out byte[]? payload))
        {
            return null;
        }

        Interlocked.Increment(ref _played);
        return new PlaybackFrame(payload, IsConcealment: false);
    }

    private void Send(VoiceAudioConfiguration configuration, CancellationToken token)
    {
        var encoder = new AudioFrameEncoder(configuration.Codec);
        int samples = configuration.SamplesPerFrame;
        int rate = (int)configuration.Codec.SampleRate();
        short[] frame = new short[samples];
        double phase = 0;
        var clock = Stopwatch.StartNew();
        long sent = 0;

        while (!token.IsCancellationRequested)
        {
            if (options.Tone)
            {
                for (int i = 0; i < samples; i++)
                {
                    frame[i] = (short)(3277 * Math.Sin(phase)); // −20 dBFS
                    phase += 2 * Math.PI * 440 / rate;
                }
            }

            // Джиттер-буфер стенда — очередь до пяти кадров; больше — отбрасываем,
            // как переполненный буфер.
            if (_network.Count < 5)
            {
                _network.Enqueue(encoder.Encode(frame));
            }

            sent++;
            double due = sent * configuration.PacketTimeMilliseconds;
            int wait = (int)(due - clock.Elapsed.TotalMilliseconds);
            if (wait > 0)
            {
                Thread.Sleep(wait);
            }
        }
    }
}
