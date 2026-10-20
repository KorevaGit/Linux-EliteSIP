using System.Diagnostics;
using System.Globalization;
using EliteSIP.MediaCore;

namespace EliteSIP.Audio.Pulse;

/// <summary>
/// Тракт разговора на PulseAudio API: микрофон, обработка голоса, кодек,
/// воспроизведение. Linux-двойник <c>WasapiVoiceAudioEngine</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Переносятся решения, а не код.</b> Из Windows-движка взято всё, что не
/// про WASAPI: один замок на управление и отдельный на обработку, пересборка
/// с отпусканием замка между попытками, остановка, не ждущая застрявшей
/// сборки, подмена молчащего виртуального микрофона, подача по такту вывода,
/// регулятор темпа по запасу приёма, уход часов по времени устройства, мягкое
/// ограничение и затухание недобора. Каждое из этих решений оплачено жалобой
/// или прогоном, их причины записаны в Windows-движке и здесь не повторяются.
/// </para>
/// <para>
/// <b>Потоки.</b> Такт задаёт сервер: обратные вызовы libpulse приходят на его
/// поток цикла (<see cref="PulseDuplex"/>) и только копируют отсчёты. Своих
/// потоков два. <b>Обработка</b> просыпается, когда микрофон отдал отсчёты, и
/// гонит весь путь до сети; ей же достаётся опорный сигнал эхоподавителю — она
/// скармливает APM всё, что ушло на вывод, прежде чем обработать кадр
/// микрофона. <b>Подача</b> спрашивает у приложения кадры и наполняет кольцо,
/// просыпаясь по запросу вывода. Роль потока вывода Windows-движка здесь у
/// обратного вызова записи: ничего не решает и никого не ждёт.
/// </para>
/// <para>
/// <b>Опорный сигнал — не на потоке цикла, и это отличие от Windows
/// намеренное.</b> APM считает кадр за доли миллисекунды, но держит замок
/// обработки, а его в это же время держит поток обработки. На Windows это
/// ждал поток вывода; здесь ждал бы поток цикла libpulse, и вместе с выводом
/// стоял бы захват — у них один поток.
/// </para>
/// <para>
/// <b>Задержка эхоподавителю</b> считается на каждом кадре, как на Windows, но
/// из других слагаемых: опорный сигнал попадает в APM уже после того, как
/// ушёл серверу, поэтому кольцо в неё не входит, а время, которое опорный
/// пролежал в очереди, вычитается. Остаются задержки обоих потоков по
/// серверу и не разобранный ещё захват.
/// </para>
/// <para>
/// <b>Начало захвата выбрасывается.</b> Находка W-L0А: у микрофона ноутбука
/// громкость на 100 % — это +66 дБ к базовой, и первая секунда после открытия
/// целиком в потолке. Щелчок включения, отданный эхоподавителю и АРУ, сбивает
/// их на первые секунды разговора, а оценку «микрофон молчит» обманывает в
/// обратную сторону.
/// </para>
/// </remarks>
public sealed class PulseVoiceAudioEngine : IVoiceAudioEngine
{
    /// <summary>Частота обработки и обоих потоков: APM на 48 кГц, сервер пересчитывает сам.</summary>
    private const int DeviceRate = PulseDuplex.Rate;

    /// <summary>Сколько захвата выбросить после открытия. См. описание типа.</summary>
    internal const int StartDiscardMilliseconds = 150;

    /// <summary>Во сколько раз кольцо больше целевого запаса. Причина — в Windows-движке.</summary>
    private const int RingHeadroomFactor = 4;

    /// <summary>Затухание недобора на отсчёт: около 4 мс до тишины на 48 кГц.</summary>
    private const float TailDecay = 0.995f;

    /// <summary>Ниже этого пика (−100 дБ) микрофон считается цифровым нулём.</summary>
    private const float SilentPeak = 1e-5f;

    private static readonly TimeSpan SilenceWatch = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan VirtualSilenceWatch = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopLockWait = TimeSpan.FromSeconds(2);
    private const int StartAttempts = 3;
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Замолчавшие микрофоны и замены виртуальных — общие на все тракты, как на Windows.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> SilentInputs = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> VirtualSubstitutes = new();

    private readonly Lock _control = new();
    private readonly Lock _ring = new();
    private readonly Lock _processing = new();
    private readonly Lock _reverseLock = new();
    private readonly AutoResetEvent _feedWake = new(false);
    private readonly ClockDriftEstimator _drift = new();
    private readonly Stopwatch _uptime = new();

    private VoiceAudioConfiguration _configuration;
    private VoiceAudioHandlers _handlers = VoiceAudioHandlers.None;
    private AudioRouteBinding _binding = AudioRouteBinding.None;

    private PulseConnection? _connection;
    private PulseDuplex? _duplex;
    private PulseDevice? _input;
    private PulseDevice? _output;
    private CancellationTokenSource? _stop;
    private Thread? _captureThread;
    private Thread? _feedThread;
    private PulseDeviceWatch? _watch;
    private RestartSupervisor? _supervisor;

    private VoiceProcessor? _processor;
    private Resampler? _processingToCodec;
    private Resampler? _codecToRender;
    private SampleRing? _playbackRing;
    private AudioFrameEncoder? _encoder;
    private AudioFrameDecoder? _decoder;
    private SampleBalance? _balance;
    private DeviceActivityWatch? _captureActivity;
    private PlaybackRateController? _rateController;
    private SpeechGainControl? _receiveGain;

    // Очередь опорного сигнала: пишет поток цикла (вывод), забирает обработка.
    private readonly float[] _reverseQueue = new float[DeviceRate];
    private int _reverseCount;

    // Состояние вывода — только поток цикла.
    private int _fadePosition;
    private bool _starving;
    private float _tail;

    private bool _echoFree;
    private bool _inputIsHeadset;
    private bool _inputIsVirtual;
    private bool _inputIsSubstitute;
    private string? _inputOverrideFor;
    private double _inputBoostDb;

    private int _capturePendingSamples;
    private int _discardRemaining;
    private int _lastDelayMilliseconds;
    private double _lastClockSample;

    private float _capturePeak;
    private float _sentPeak;
    private float _playedPeak;
    private float _sentMeter;
    private float _playedMeter;
    private int _captureOverloads;

    private bool _running;
    private bool _disposed;
    private bool _stopRequested;

    public PulseVoiceAudioEngine(VoiceAudioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public VoiceAudioHandlers Handlers
    {
        get => Volatile.Read(ref _handlers);
        set => Volatile.Write(ref _handlers, value ?? VoiceAudioHandlers.None);
    }

    /// <summary>Сходится ли баланс отсчётов. <c>null</c> — тракт не запускался.</summary>
    public SampleBalance? Balance => _balance;

    /// <summary>Что происходит с микрофоном на самом деле.</summary>
    public DeviceActivity CaptureActivity =>
        _captureActivity is null ? DeviceActivity.Warmup : _captureActivity.Assess(_uptime.Elapsed);

    /// <summary>Задержка, объявленная эхоподавителю на последнем кадре.</summary>
    public int DeclaredDelayMilliseconds => Volatile.Read(ref _lastDelayMilliseconds);

    /// <summary>Оценка ухода часов захвата против вывода.</summary>
    public ClockDriftEstimator Drift => _drift;

    /// <summary>Регулятор темпа приёма. <c>null</c> — тракт не запускался.</summary>
    public PlaybackRateController? RateController => _rateController;

    /// <inheritdoc/>
    public AudioLatencySnapshot Latency
    {
        get
        {
            PulseDuplex? duplex = _duplex;
            if (duplex is null)
            {
                return default;
            }

            double ring = (_playbackRing?.Available ?? 0) * 1000.0 / DeviceRate;
            double pending = Volatile.Read(ref _capturePendingSamples) * 1000.0 / DeviceRate;
            return new AudioLatencySnapshot(
                duplex.CaptureLatencyMilliseconds + pending,
                duplex.PlaybackLatencyMilliseconds + ring);
        }
    }

    /// <inheritdoc/>
    ///
    /// <remarks>
    /// Как на Windows, с одним отличием: переключатель обработки Windows
    /// (<see cref="VoiceAudioConfiguration.WindowsProcessing"/>) здесь ничего не
    /// значит, и пересборку тракта он не вызывает.
    /// </remarks>
    public void Apply(VoiceAudioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? restartReason = null;
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            VoiceAudioConfiguration previous = _configuration;
            Volatile.Write(ref _configuration, configuration);

            if (!_running)
            {
                return;
            }

            if (previous.InputDeviceId != configuration.InputDeviceId
                || previous.OutputDeviceId != configuration.OutputDeviceId
                || previous.Codec != configuration.Codec
                || previous.PacketTimeMilliseconds != configuration.PacketTimeMilliseconds)
            {
                restartReason = "в настройках выбрано другое устройство";
            }
            else if (previous.AutomaticGainControl != configuration.AutomaticGainControl
                || previous.NoiseSuppression != configuration.NoiseSuppression
                || previous.BackgroundVoiceSuppression != configuration.BackgroundVoiceSuppression)
            {
                ReplaceProcessor();
                Diagnostic(ProcessingSummary());
            }
        }

        if (restartReason is not null)
        {
            Restart(restartReason);
        }
    }

    private void ReplaceProcessor()
    {
        if (_processor is not VoiceProcessor current)
        {
            return;
        }

        VoiceProcessor fresh = new(
            current.SampleRate,
            _configuration.AutomaticGainControl,
            _configuration.NoiseSuppression,
            echoCancellation: !_echoFree,
            backgroundVoiceSuppression: _configuration.BackgroundVoiceSuppression);

        lock (_processing)
        {
            _processor = fresh;
        }

        current.Dispose();
    }

    private string ProcessingSummary() =>
        "обработка: "
        + (_echoFree ? "эхоподавление выкл. (вывод в наушники), " : "эхоподавление вкл., ")
        + (_configuration.NoiseSuppression ? (_echoFree ? "шумодав умеренный, " : "шумодав сильный, ") : "шумодав выкл., ")
        + (_configuration.BackgroundVoiceSuppression ? "голоса вокруг приглушаются, " : "голоса вокруг не приглушаются, ")
        + (_configuration.AutomaticGainControl ? "АРУ вкл." : "АРУ выкл.");

    /// <inheritdoc/>
    public void Reconfigure(VoiceAudioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                throw new InvalidOperationException(
                    "перестраивать работающий тракт нельзя: сначала остановка");
            }

            _configuration = configuration;
        }
    }

    /// <inheritdoc/>
    public void Start()
    {
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                return;
            }

            Volatile.Write(ref _stopRequested, false);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Build();
                    break;
                }
                catch (Exception e) when (IsBuildFailure(e))
                {
                    Teardown();

                    if (attempt >= StartAttempts || !IsTransient(e))
                    {
                        throw e as VoiceAudioException ?? new VoiceAudioException("тракт не поднялся: " + e.Message, e);
                    }

                    Diagnostic($"запуск не удался ({e.Message}), попытка {attempt + 1}");
                    Thread.Sleep(StartRetryDelay);
                }
            }

            _running = true;
            StartWatching();
        }
    }

    /// <summary>
    /// Отказы сборки, которые разбираются, а не роняют приложение.
    /// </summary>
    ///
    /// <remarks>
    /// <see cref="VoiceAudioException"/> здесь же. На Windows её — «нет
    /// микрофона» — сборка бросает мимо фильтра, и она уходит из запуска без
    /// разбора недособранного, а из пересборки — на поток надзирателя.
    /// <see cref="DllNotFoundException"/> — нет <c>libpulse.so.0</c> (пакет
    /// <c>libpulse0</c> снесли): звонок отказывает честно, а не падением.
    /// </remarks>
    private static bool IsBuildFailure(Exception e) =>
        e is PulseFailure or VoiceAudioException or InvalidOperationException or ArgumentException
            or DllNotFoundException;

    private static bool IsTransient(Exception e) => e is PulseFailure { Transient: true };

    /// <inheritdoc/>
    public void Stop()
    {
        Volatile.Write(ref _stopRequested, true);
        StopWatching();

        if (!_control.TryEnter(StopLockWait))
        {
            Diagnostic("остановка: пересборка держит тракт, устройство закроется по её выходе");
            return;
        }

        try
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            Teardown();
        }
        finally
        {
            _control.Exit();
        }
    }

    /// <inheritdoc/>
    public void Restart(string reason)
    {
        AudioRestartPolicy policy;
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_running)
            {
                return;
            }

            policy = _configuration.RestartSettings.CreatePolicy();
            Report(new VoiceAudioEvent.Restarting(reason, 1));
            Teardown();
        }

        Stopwatch since = Stopwatch.StartNew();

        while (true)
        {
            TimeSpan wait;

            lock (_control)
            {
                if (_disposed || !_running || Volatile.Read(ref _stopRequested))
                {
                    if (_running)
                    {
                        _running = false;
                        Teardown();
                    }

                    return;
                }

                try
                {
                    Build();

                    if (Volatile.Read(ref _stopRequested))
                    {
                        _running = false;
                        Teardown();
                        return;
                    }

                    policy.RecordSuccess();
                    Report(new VoiceAudioEvent.Restarted(reason));
                    return;
                }
                catch (Exception e) when (IsBuildFailure(e))
                {
                    Teardown();

                    RestartDecision decision = policy.RecordFailure(since.Elapsed);
                    if (decision.Action == RestartAction.GiveUp)
                    {
                        _running = false;
                        Report(new VoiceAudioEvent.Broken($"{reason}: {e.Message}"));
                        return;
                    }

                    Diagnostic(
                        $"пересборка не удалась ({e.Message}), попытка {decision.Attempt} "
                        + $"через {decision.Delay.TotalMilliseconds:F0} мс");
                    Report(new VoiceAudioEvent.Restarting(reason, decision.Attempt + 1));
                    wait = decision.Delay;
                }
            }

            Thread.Sleep(wait);
        }
    }

    private void StartWatching()
    {
        _supervisor = new RestartSupervisor(reason => Restart(reason));

        try
        {
            _watch = PulseDeviceWatch.Start(OnDeviceChanged);
        }
        catch (InvalidOperationException e)
        {
            // Разговор от этого не отменяется — он просто не переживёт смену
            // устройства. Сказать в журнал, а не падать.
            Diagnostic($"следить за сменой устройств не удалось: {e.Message}");
        }
    }

    private void StopWatching()
    {
        _watch?.Dispose();
        _watch = null;

        _supervisor?.Dispose();
        _supervisor = null;
    }

    /// <summary>Изменение в устройствах. Приходит на потоке пула — только фильтр и повод.</summary>
    private void OnDeviceChanged(AudioDeviceChange change)
    {
        SilentInputs.Clear();

        if (!DeviceChangeRelevance.AffectsRoute(change, Volatile.Read(ref _binding)))
        {
            return;
        }

        _supervisor?.Notify(Describe(change));
    }

    private static string Describe(AudioDeviceChange change) => change.Kind switch
    {
        AudioDeviceChangeKind.DeviceAdded => "устройство вернулось",
        AudioDeviceChangeKind.DeviceRemoved => "устройство исчезло",
        AudioDeviceChangeKind.DefaultChanged => change.Direction == AudioDeviceDirection.Capture
            ? "сменился микрофон по умолчанию"
            : "сменился выход по умолчанию",
        _ => change.Availability == AudioDeviceAvailability.Active
            ? "в гнездо воткнули"
            : "из гнезда выдернули",
    };

    public void Dispose()
    {
        Volatile.Write(ref _stopRequested, true);
        StopWatching();

        lock (_control)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            Teardown();
            _feedWake.Dispose();
        }
    }

    /// <summary>Строка для журнала: всё, чем тракт может отчитаться о себе.</summary>
    public string Summary()
    {
        if (_balance is null || _captureActivity is null || _rateController is null)
        {
            return "тракт не запущен";
        }

        PulseDuplex? duplex = _duplex;
        string server = duplex is null
            ? "потоки закрыты"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"сервер: дыр {duplex.Holes}, переполнений захвата {duplex.Overflows}, недоборов вывода {duplex.Underflows}");

        return string.Join(
            Environment.NewLine,
            $"захват и вывод {DeviceRate} Гц, обработка {_processor?.SampleRate} Гц, кодек {_configuration.Codec}",
            _captureActivity.Summary(_uptime.Elapsed),
            _balance.Summary,
            _rateController.Summary,
            _drift.Summary,
            server,
            $"задержка эхоподавителю: {DeclaredDelayMilliseconds} мс",
            LevelSummary);
    }

    /// <inheritdoc/>
    public string ProcessingReport => _processor?.Summary ?? string.Empty;

    /// <inheritdoc/>
    public string LevelSummary
    {
        get
        {
            int overloads = Volatile.Read(ref _captureOverloads);
            string overload = overloads > 0
                ? $", микрофон у потолка {overloads} отсч. — убавьте громкость микрофона в настройках звука"
                : string.Empty;

            string boost = _inputBoostDb > PulseDeviceRules.OverdrivenBoostDb
                ? string.Create(CultureInfo.InvariantCulture, $", громкость микрофона {_inputBoostDb:+0} дБ к базовой")
                : string.Empty;

            string rate = _rateController is PlaybackRateController controller
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $", темп приёма {controller.CorrectionPpm:+0;-0;0} ppm{(controller.IsSaturated ? " (В УПОРЕ)" : string.Empty)}")
                : string.Empty;

            string processing = _processor is VoiceProcessor processor ? "; " + processor.Summary : string.Empty;

            string receive = _receiveGain is SpeechGainControl leveling
                ? string.Create(CultureInfo.InvariantCulture, $", выравнивание приёма {leveling.GainDb:+0;-0;0} дБ")
                : string.Empty;

            return $"пики: микрофон {Decibels(_capturePeak)}, в линию {Decibels(_sentPeak)}, "
                + $"в наушники {Decibels(_playedPeak)}{overload}{boost}{rate}{receive}{processing}";
        }
    }

    /// <inheritdoc/>
    public AudioLevels TakeLevels() => new(
        Interlocked.Exchange(ref _sentMeter, 0),
        Interlocked.Exchange(ref _playedMeter, 0));

    private static string Decibels(float peak) => peak <= SilentPeak
        ? "тишина"
        : string.Create(CultureInfo.InvariantCulture, $"{20 * Math.Log10(peak):F0} дБ");

    private static float PeakOf(ReadOnlySpan<float> samples, float peak)
    {
        foreach (float sample in samples)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak;
    }

    private static int CountOverloads(ReadOnlySpan<float> samples)
    {
        int count = 0;
        foreach (float sample in samples)
        {
            if (Math.Abs(sample) >= 0.999f)
            {
                count++;
            }
        }

        return count;
    }

    // MARK: - Сборка и разбор

    private void Build()
    {
        _connection = PulseConnection.Connect("EliteSIP", out string? failure)
            ?? throw new PulseFailure("сервер звука недоступен: " + failure, transient: true);

        PulseSnapshot snapshot;
        try
        {
            snapshot = PulseIntrospection.Snapshot(_connection);
        }
        catch (InvalidOperationException e)
        {
            throw new PulseFailure(e.Message, transient: true);
        }

        // Вывод первым: по нему выбирается запасной микрофон — той же карты.
        _output = OpenDevice(_configuration.OutputDeviceId, AudioDeviceDirection.Render, snapshot);
        _input = OpenInput(snapshot);

        if (_input is null || _output is null)
        {
            throw new VoiceAudioException(
                _input is null && _output is null
                    ? "в системе нет ни микрофона, ни выхода"
                    : _input is null ? "нет микрофона" : "нет устройства воспроизведения");
        }

        PulseDevice input = _input;
        PulseDevice output = _output;

        Volatile.Write(
            ref _binding,
            new AudioRouteBinding(_configuration.InputDeviceId, _configuration.OutputDeviceId, input.Name, output.Name));

        _echoFree = PulseDeviceRules.IsEchoFree(output);
        _inputIsHeadset = PulseDeviceRules.IsHeadsetPair(input, output);
        _inputBoostDb = PulseDeviceRules.BoostAboveBaseDb(input.Volume, input.BaseVolume);

        int codecRate = (int)_configuration.Codec.SampleRate();

        _processor = new VoiceProcessor(
            DeviceRate,
            _configuration.AutomaticGainControl,
            _configuration.NoiseSuppression,
            echoCancellation: !_echoFree,
            backgroundVoiceSuppression: _configuration.BackgroundVoiceSuppression);

        _processingToCodec = new Resampler(DeviceRate, codecRate);
        _codecToRender = new Resampler(codecRate, DeviceRate, adjustable: true);

        int targetFill = _configuration.TargetPlaybackFrames * _configuration.PacketTimeMilliseconds * DeviceRate / 1000;
        int capacity = Math.Max(targetFill * RingHeadroomFactor, DeviceRate / 2);
        _playbackRing = new SampleRing(capacity, targetFill);

        _encoder = new AudioFrameEncoder(_configuration.Codec);
        _decoder = new AudioFrameDecoder(_configuration.Codec);

        // Законно висящее в пути между устройством и кодером — два кадра
        // обработки плюс задержка пересчёта в частоту кодека. Пересчёта до
        // обработки здесь нет: захват приходит сразу на её частоте.
        double toCodec = (double)codecRate / DeviceRate;
        int inFlight = (int)Math.Ceiling(((2 * _processor.FrameSamples) + _processingToCodec.LatencySamples) * toCodec) + 2;

        _balance = new SampleBalance(toCodec, _configuration.SamplesPerFrame, inFlight);
        _captureActivity = new DeviceActivityWatch(DeviceRate);
        _rateController = new PlaybackRateController(targetFill, DeviceRate);
        _drift.Reset();

        _lastClockSample = 0;
        _capturePeak = _sentPeak = _playedPeak = 0;
        _captureOverloads = 0;
        _capturePendingSamples = 0;
        _discardRemaining = StartDiscardMilliseconds * DeviceRate / 1000;
        _fadePosition = int.MaxValue;
        _starving = false;
        _tail = 0;
        lock (_reverseLock)
        {
            _reverseCount = 0;
        }

        _stop = new CancellationTokenSource();
        CancellationToken token = _stop.Token;
        _uptime.Restart();

        // Потоки сервера открываются последними: обратный вызов записи
        // приходит сразу, и всё, что он читает, уже должно стоять.
        _duplex = PulseDuplex.Open(_connection, input, output, Render, reason => FailFromThread(reason));

        _captureThread = StartThread("elitesip-capture", () => CaptureLoop(token));
        _feedThread = StartThread("elitesip-feed", () => FeedLoop(token));

        Diagnostic(
            $"тракт поднят: микрофон «{input.DisplayName}» ({InputOrigin(input)}), "
            + $"наушники «{output.DisplayName}» ({OutputOrigin(output)})");

        Diagnostic(ProcessingSummary());
        Diagnostic("микрофон: " + input.Summary);
        Diagnostic("выход: " + output.Summary);

        if (_inputBoostDb > PulseDeviceRules.OverdrivenBoostDb)
        {
            // Предупредить, а не поправить: громкость системы — настройка
            // машины, её ставит администратор, и софтфон, двигающий системный
            // ползунок, — сюрприз для всех остальных программ (W-L0А).
            Diagnostic(string.Create(
                CultureInfo.InvariantCulture,
                $"микрофон усилен на {_inputBoostDb:F0} дБ сверх базовой громкости — будет перегруз и щелчки; убавьте громкость микрофона в настройках звука до 30–50 %"));
        }

        if (input.Transport == AudioTransport.Virtual)
        {
            // Шумодав или эхоподавитель сервера стоит перед нами: обработка
            // пойдёт дважды. Не запрещено — человек поставил его сам, — но
            // разбор жалобы на «глухой голос» начинается отсюда.
            Diagnostic("микрофон — программный фильтр сервера: обработка голоса пойдёт дважды");
        }

        Diagnostic(string.Create(
            CultureInfo.InvariantCulture,
            $"буферы сервера: захват {_duplex.CaptureFragmentMilliseconds:F1} мс, вывод {_duplex.PlaybackLengthMilliseconds:F1} мс "
                + $"({_duplex.OutputChannels} кан., голос в передней паре); кодек {_configuration.Codec} {codecRate} Гц, "
                + $"запас кольца {targetFill} отсчётов"));
    }

    private static Thread StartThread(string name, Action body)
    {
        Thread thread = new(() => body())
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = name,
        };

        thread.Start();
        return thread;
    }

    private void Teardown()
    {
        _stop?.Cancel();

        JoinThread(_captureThread);
        JoinThread(_feedThread);
        _captureThread = _feedThread = null;

        // Потоки сервера — после своих: поток обработки ждёт на событии
        // захвата, и закрыть его раньше значит уронить поток на закрытом
        // событии.
        _duplex?.Dispose();
        _duplex = null;

        _connection?.Dispose();
        _connection = null;

        _stop?.Dispose();
        _stop = null;

        _processor?.Dispose();
        _processor = null;

        _input = _output = null;
        _uptime.Stop();
    }

    private static void JoinThread(Thread? thread)
    {
        if (thread is not null && !thread.Join(TimeSpan.FromSeconds(1)))
        {
            Debug.Fail($"поток {thread.Name} не остановился за секунду");
        }
    }

    /// <summary>
    /// Устройство из настроек, а если его нет — системное: правило перенесено
    /// из оригинала дословно, цена ошибки — несостоявшийся разговор.
    /// </summary>
    private PulseDevice? OpenDevice(string? id, AudioDeviceDirection direction, PulseSnapshot snapshot)
    {
        if (!string.IsNullOrEmpty(id))
        {
            PulseDevice? saved = snapshot.Find(id, direction);
            if (saved is not null && (direction == AudioDeviceDirection.Render || PulseDeviceRules.IsUsableInput(saved)))
            {
                return saved;
            }

            Diagnostic($"устройство из настроек не найдено, берём системное: {id}");
        }

        PulseDevice? fallback = snapshot.Default(direction);

        // Системным входом у сервера может стоять монитор выхода — так бывает,
        // когда микрофонов нет вовсе. Говорить в него нельзя.
        if (direction == AudioDeviceDirection.Capture && fallback is not null && !PulseDeviceRules.IsUsableInput(fallback))
        {
            fallback = snapshot.UsableInputs.FirstOrDefault();
        }

        return fallback ?? snapshot.Of(direction).FirstOrDefault(device =>
            direction == AudioDeviceDirection.Render || PulseDeviceRules.IsUsableInput(device));
    }

    /// <summary>Микрофон с поправкой на молчащий виртуальный системный — как на Windows с 0.1.76.</summary>
    private PulseDevice? OpenInput(PulseSnapshot snapshot)
    {
        _inputIsSubstitute = false;
        _inputIsVirtual = false;
        _inputOverrideFor = null;

        PulseDevice? chosen = OpenDevice(_configuration.InputDeviceId, AudioDeviceDirection.Capture, snapshot);
        if (chosen is null)
        {
            return null;
        }

        bool isVirtual = chosen.Transport is AudioTransport.Virtual;
        if (_configuration.InputDeviceId is not null || !isVirtual)
        {
            _inputIsVirtual = isVirtual;
            return chosen;
        }

        if (VirtualSubstitutes.TryGetValue(chosen.Name, out string? forced)
            && !SilentInputs.ContainsKey(forced)
            && snapshot.Find(forced, AudioDeviceDirection.Capture) is PulseDevice overridden)
        {
            Diagnostic($"системный микрофон «{chosen.DisplayName}» программный и уже молчал — берём «{overridden.DisplayName}»");
            _inputOverrideFor = chosen.Name;
            _inputIsSubstitute = true;
            return overridden;
        }

        _inputIsVirtual = true;
        return chosen;
    }

    private string InputOrigin(PulseDevice input)
    {
        if (_inputIsSubstitute)
        {
            return "замена программного системного";
        }

        if (_configuration.InputDeviceId is null)
        {
            return "системный по умолчанию";
        }

        return string.Equals(_configuration.InputDeviceId, input.Name, StringComparison.Ordinal)
            ? "из настроек"
            : "системный: выбранного в настройках нет";
    }

    /// <summary>
    /// Откуда взят выход. «Из настроек» — только если открыт именно он: на
    /// Windows строка говорила «из настроек» и тогда, когда выбранного не
    /// нашлось и открыт системный, — разбор журнала шёл не туда.
    /// </summary>
    private string OutputOrigin(PulseDevice output) =>
        _configuration.OutputDeviceId is null
            ? "системные"
            : string.Equals(_configuration.OutputDeviceId, output.Name, StringComparison.Ordinal)
                ? "из настроек"
                : "системные: выбранных в настройках нет";

    /// <summary>
    /// Микрофон молчит — ни одного ненулевого отсчёта с подъёма тракта. Логика
    /// и причины — те же, что на Windows: настоящий микрофон не подменяется
    /// никогда, программный системный — да.
    /// </summary>
    private void CheckSilentInput()
    {
        PulseDevice? input = _input;
        if (_capturePeak >= SilentPeak || input is null)
        {
            return;
        }

        string current = input.Name;
        string? renderName = _output?.DisplayName;

        if (_inputIsVirtual && _configuration.InputDeviceId is null)
        {
            SilentInputs[current] = 0;
            if (PulseDeviceCatalog.SpareCapture(PulseDeviceCatalog.Snapshot(), [.. SilentInputs.Keys, current], renderName)
                is PulseDevice spare)
            {
                VirtualSubstitutes[current] = spare.Name;
                Diagnostic($"системный микрофон «{input.DisplayName}» программный и молчит — переходим на «{spare.DisplayName}»");
                _supervisor?.Notify($"программный микрофон молчал, переход на «{spare.DisplayName}»");
                return;
            }
        }

        if (_inputIsSubstitute && _inputOverrideFor is string virtualDefault)
        {
            SilentInputs[current] = 0;
            if (PulseDeviceCatalog.SpareCapture(PulseDeviceCatalog.Snapshot(), [.. SilentInputs.Keys, virtualDefault], renderName)
                is PulseDevice spare)
            {
                VirtualSubstitutes[virtualDefault] = spare.Name;
                Diagnostic($"замена программного микрофона «{input.DisplayName}» молчит — пробуем «{spare.DisplayName}»");
                _supervisor?.Notify($"замена микрофона молчала, переход на «{spare.DisplayName}»");
                return;
            }
        }

        string hint = _inputIsHeadset
            ? "проверьте кнопку выключения микрофона и штангу на гарнитуре"
            : _configuration.InputDeviceId is not null
                ? "проверьте, тот ли вход выбран в настройках"
                : "проверьте кнопку на гарнитуре, выключение микрофона в настройках звука и микрофон по умолчанию";

        Diagnostic($"микрофон «{input.DisplayName}» молчит: за {SilenceWindow.TotalSeconds:0.#} с ни одного отсчёта — {hint}");
        Report(new VoiceAudioEvent.InputSilent(input.DisplayName));
    }

    private TimeSpan SilenceWindow => _inputIsVirtual ? VirtualSilenceWatch : SilenceWatch;

    // MARK: - Обработка: микрофон → APM → кодек → сеть

    private void CaptureLoop(CancellationToken token)
    {
        PulseDuplex duplex = _duplex!;
        Resampler toCodec = _processingToCodec!;
        AudioFrameEncoder encoder = _encoder!;
        SampleBalance balance = _balance!;
        DeviceActivityWatch activity = _captureActivity!;
        int frameSize = _processor!.FrameSamples;

        float[] incoming = new float[DeviceRate];
        float[] processingPending = new float[DeviceRate * 2];
        int processingCount = 0;
        float[] reverse = new float[DeviceRate];
        float[] reversePending = new float[DeviceRate * 2];
        int reverseCount = 0;
        float[] processed = new float[frameSize];
        float[] codecPending = new float[DeviceRate];
        int codecCount = 0;
        short[] frame = new short[_configuration.SamplesPerFrame];
        int holesSeen = 0;

        while (!token.IsCancellationRequested)
        {
            if (!duplex.CaptureReady.WaitOne(100))
            {
                continue;
            }

            int holes = duplex.Holes;
            for (; holesSeen < holes; holesSeen++)
            {
                activity.NoteDiscontinuity();
            }

            int count = duplex.TakeCaptured(incoming);
            activity.NoteDelivered(count);

            Span<float> fresh = incoming.AsSpan(0, count);
            if (_discardRemaining > 0)
            {
                int skip = Math.Min(_discardRemaining, fresh.Length);
                _discardRemaining -= skip;
                fresh = fresh[skip..];
            }

            if (fresh.Length > 0)
            {
                _capturePeak = PeakOf(fresh, _capturePeak);
                _captureOverloads += CountOverloads(fresh);
                balance.NoteCaptured(fresh.Length);

                int room = processingPending.Length - processingCount;
                int take = Math.Min(room, fresh.Length);
                fresh[..take].CopyTo(processingPending.AsSpan(processingCount));
                processingCount += take;
            }

            Volatile.Write(ref _capturePendingSamples, processingCount);

            // Опорный сигнал — до кадров микрофона: эхоподавитель должен знать,
            // что прозвучало, прежде чем вычитать это из услышанного.
            int reverseTaken;
            lock (_reverseLock)
            {
                reverseTaken = Math.Min(_reverseCount, Math.Min(reverse.Length, reversePending.Length - reverseCount));
                _reverseQueue.AsSpan(0, reverseTaken).CopyTo(reverse);
                Array.Copy(_reverseQueue, reverseTaken, _reverseQueue, 0, _reverseCount - reverseTaken);
                _reverseCount -= reverseTaken;
            }

            reverse.AsSpan(0, reverseTaken).CopyTo(reversePending.AsSpan(reverseCount));
            reverseCount += reverseTaken;

            int reverseConsumed = 0;
            while (reverseCount - reverseConsumed >= frameSize)
            {
                lock (_processing)
                {
                    _processor!.AnalyzeReverse(reversePending.AsSpan(reverseConsumed, frameSize));
                }

                reverseConsumed += frameSize;
            }

            if (reverseConsumed > 0)
            {
                Array.Copy(reversePending, reverseConsumed, reversePending, 0, reverseCount - reverseConsumed);
                reverseCount -= reverseConsumed;
            }

            int consumed = 0;
            while (processingCount - consumed >= frameSize)
            {
                int delay = ComputeDelayMilliseconds(duplex, processingCount - consumed, reverseCount);

                lock (_processing)
                {
                    _processor!.Process(processingPending.AsSpan(consumed, frameSize), processed, delay);
                }

                consumed += frameSize;

                int produced = toCodec.Process(processed, codecPending.AsSpan(codecCount));
                codecCount += produced;
                balance.NoteConverted(produced);
            }

            if (consumed > 0)
            {
                Array.Copy(processingPending, consumed, processingPending, 0, processingCount - consumed);
                processingCount -= consumed;
            }

            codecCount = EmitFrames(encoder, codecPending, codecCount, frame);
        }
    }

    /// <summary>
    /// Насколько опорный сигнал опережает микрофонный, в миллисекундах. См.
    /// описание типа: кольцо не входит, отложенный опорный вычитается.
    /// </summary>
    private int ComputeDelayMilliseconds(PulseDuplex duplex, int capturePending, int reversePending)
    {
        double milliseconds = duplex.PlaybackLatencyMilliseconds
            + duplex.CaptureLatencyMilliseconds
            + ((capturePending - reversePending) * 1000.0 / DeviceRate);

        int result = Math.Max(0, (int)milliseconds);
        Volatile.Write(ref _lastDelayMilliseconds, result);
        return result;
    }

    /// <summary>Собирает кадры кодека и отдаёт их сети. Усиление и ограничение — как на Windows.</summary>
    private int EmitFrames(AudioFrameEncoder encoder, float[] pending, int count, short[] frame)
    {
        SampleBalance balance = _balance!;
        float gain = Volatile.Read(ref _configuration).MicrophoneGain;
        int size = frame.Length;
        int offset = 0;
        float meter = 0;

        while (count - offset >= size)
        {
            for (int i = 0; i < size; i++)
            {
                float sample = pending[offset + i] * gain;
                meter = Math.Max(meter, Math.Abs(sample));
                frame[i] = ToPcm(SpeechGainControl.Limit(sample));
            }

            offset += size;

            byte[] payload = encoder.Encode(frame);
            balance.NoteEncodedFrame();
            Handlers.EncodedFrame?.Invoke(payload);
        }

        _sentPeak = Math.Max(_sentPeak, meter);
        if (meter > _sentMeter)
        {
            _sentMeter = meter;
        }

        if (offset > 0)
        {
            Array.Copy(pending, offset, pending, 0, count - offset);
        }

        return count - offset;
    }

    // MARK: - Вывод: кольцо → сервер. Поток цикла libpulse

    private void Render(Span<float> destination)
    {
        SampleRing? ring = _playbackRing;
        if (ring is null)
        {
            destination.Clear();
            return;
        }

        int taken;
        lock (_ring)
        {
            taken = ring.Read(destination);
        }

        _feedWake.Set();

        int fadeLength = DeviceRate / 200;
        if (taken > 0 && _starving)
        {
            _fadePosition = 0;
            _starving = false;
        }

        for (int i = 0; i < taken && _fadePosition < fadeLength; i++, _fadePosition++)
        {
            destination[i] *= (float)_fadePosition / fadeLength;
        }

        if (taken < destination.Length)
        {
            float last = taken > 0 ? destination[taken - 1] : _tail;
            for (int i = taken; i < destination.Length; i++)
            {
                last *= TailDecay;
                destination[i] = last;
            }

            _starving = true;
        }

        _tail = destination[^1];

        float volume = Volatile.Read(ref _configuration).PlaybackVolume;
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = SpeechGainControl.Limit(destination[i] * volume);
        }

        float played = PeakOf(destination, 0);
        _playedPeak = Math.Max(_playedPeak, played);
        if (played > _playedMeter)
        {
            _playedMeter = played;
        }

        // Опорный — ровно то, что отдано серверу, после громкости: слышит
        // микрофон то, что прозвучало. Застрявшая обработка не забирает —
        // старое выбрасывается, как и в очереди захвата.
        lock (_reverseLock)
        {
            int free = _reverseQueue.Length - _reverseCount;
            if (destination.Length > free)
            {
                int drop = Math.Min(_reverseCount, destination.Length - free);
                Array.Copy(_reverseQueue, drop, _reverseQueue, 0, _reverseCount - drop);
                _reverseCount -= drop;
            }

            int take = Math.Min(destination.Length, _reverseQueue.Length - _reverseCount);
            destination[..take].CopyTo(_reverseQueue.AsSpan(_reverseCount));
            _reverseCount += take;
        }
    }

    // MARK: - Подача: сеть → кольцо

    private void FeedLoop(CancellationToken token)
    {
        Resampler toRender = _codecToRender!;
        SampleRing ring = _playbackRing!;
        PlaybackRateController controller = _rateController!;
        AudioFrameDecoder decoder = _decoder!;
        PacketLossConcealer concealer = new(_configuration.Codec);
        PulseDuplex duplex = _duplex!;

        float[] decoded = new float[DeviceRate];
        float[] converted = new float[DeviceRate];
        double lastObserved = _uptime.Elapsed.TotalSeconds;
        bool silenceChecked = false;
        WaitHandle[] wake = [token.WaitHandle, _feedWake];

        int receiveChunk = Math.Max(1, (int)_configuration.Codec.SampleRate() / 100);
        _receiveGain = null;

        int frameSamples = ring.TargetFill / Math.Max(_configuration.TargetPlaybackFrames, 1);

        while (!token.IsCancellationRequested)
        {
            int fill;
            lock (_ring)
            {
                fill = ring.Available;
            }

            double now = _uptime.Elapsed.TotalSeconds;
            ObserveBacklog(controller, fill, frameSamples, now - lastObserved);
            lastObserved = now;
            toRender.SetRateCorrection(controller.Correction);

            SampleClocks(duplex, now);

            if (!silenceChecked && now >= SilenceWindow.TotalSeconds)
            {
                silenceChecked = true;

                if (_capturePeak < SilentPeak)
                {
                    ThreadPool.QueueUserWorkItem(_ => CheckSilentInput());
                }
                else if (_input?.Name is string speaking)
                {
                    SilentInputs.TryRemove(speaking, out _);
                }
            }

            bool fed = false;
            int lead = ring.TargetFill
                / Math.Max(_configuration.TargetPlaybackFrames, 1)
                * _configuration.PlaybackLeadFrames;

            while (fill < lead)
            {
                PlaybackFrame? next = Handlers.NeedsFrame?.Invoke();
                if (next is not PlaybackFrame playback)
                {
                    break;
                }

                short[] samples = playback.IsConcealment
                    ? concealer.Conceal(_configuration.SamplesPerFrame)
                    : concealer.Receive(decoder.Decode(playback.Payload.Span));
                Handlers.DecodedSamples?.Invoke(samples);

                int length = Math.Min(samples.Length, decoded.Length);
                for (int i = 0; i < length; i++)
                {
                    decoded[i] = samples[i] / 32768f;
                }

                LevelReceived(decoded.AsSpan(0, length), receiveChunk);

                int produced = toRender.Process(decoded.AsSpan(0, length), converted);

                lock (_ring)
                {
                    ring.Write(converted.AsSpan(0, produced));
                    fill = ring.Available;
                }

                fed = true;
            }

            if (!fed)
            {
                // Ждём, пока вывод заберёт из кольца, — такт сервера, 5 мс.
                WaitHandle.WaitAny(wake, 20);
            }
        }
    }

    private void LevelReceived(Span<float> samples, int chunk)
    {
        if (!Volatile.Read(ref _configuration).ReceiveGainControl)
        {
            _receiveGain = null;
            return;
        }

        SpeechGainControl control = _receiveGain ??= new SpeechGainControl(
            ReceiveTuning.ReceiveTargetDb, ReceiveTuning.ReceiveMaximumBoostDb, ReceiveTuning.ReceiveMaximumCutDb);

        for (int offset = 0; offset < samples.Length; offset += chunk)
        {
            control.Process(samples.Slice(offset, Math.Min(chunk, samples.Length - offset)));
        }
    }

    /// <summary>Весь запас приёма — кольцо плюс джиттер-буфер. Почему так — в Windows-движке.</summary>
    private void ObserveBacklog(PlaybackRateController controller, int ringFill, int frameSamples, double elapsed)
    {
        if (Handlers.Backlog?.Invoke() is not PlaybackBacklog backlog)
        {
            return;
        }

        controller.Retarget(ReceiveTuning.BacklogTarget(backlog, frameSamples));
        controller.Observe(ringFill + (backlog.Frames * frameSamples), elapsed);
    }

    /// <summary>
    /// Проба часов обоих устройств раз в секунду, после разгона в три секунды.
    /// Время потока у сервера — по часам устройства (см. <see cref="PulseDuplex.ReadClocks"/>).
    /// </summary>
    private void SampleClocks(PulseDuplex duplex, double now)
    {
        if (now < 3 || now - _lastClockSample < 1.0)
        {
            return;
        }

        _lastClockSample = now;

        if (duplex.ReadClocks() is (double capture, double playback))
        {
            _drift.Add(now, capture - playback);
        }
    }

    // MARK: - Мелочи

    private static short ToPcm(float sample) =>
        (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);

    /// <summary>
    /// Сервер закрыл поток. Чинить отсюда нельзя — повод уходит надзирателю,
    /// он склеит его с уведомлениями о том же событии и пересоберёт тракт один
    /// раз. Зовётся на потоке цикла libpulse.
    /// </summary>
    private void FailFromThread(string reason)
    {
        Diagnostic(reason);
        _supervisor?.Notify(reason);
    }

    private void Diagnostic(string text) => Handlers.Diagnostic?.Invoke(text);

    private void Report(VoiceAudioEvent value) => Handlers.Event?.Invoke(value);
}
