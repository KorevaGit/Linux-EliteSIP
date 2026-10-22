using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EliteSIP.AdminAccess;
using EliteSIP.App.History;
using EliteSIP.App.Incoming;
using EliteSIP.App.Panel;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Platform;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Shell;
using EliteSIP.App.Theme;
using EliteSIP.CallHistory;
using EliteSIP.Diagnostics;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App;

/// <summary>
/// Приложение Linux-версии — перевод <c>App.xaml.cs</c> Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// Порядок запуска и связи между частями — те же, и почему они такие, сказано
/// там: настройки до языка, язык до палитры, палитра до первого окна; телефон
/// после панели; линия Spark после телефона; регистрация поднимается сама.
/// </para>
/// <para>
/// Отличается то, что на Windows давала система, — оболочка W-L4:
/// </para>
/// <list type="bullet">
/// <item>одна копия и передача запуска службе — в <see cref="Program"/>, до
/// Avalonia;</item>
/// <item>конец сеанса — <see cref="SessionLifetime"/> (менеджер сеанса,
/// замок logind, <c>SIGTERM</c>) вместо <c>SessionEnding</c>;</item>
/// <item>сон и сеть — <see cref="EliteSIP.Platform.Linux.NetworkWatch"/>;</item>
/// <item>можно ли прятать панель в значок — <see cref="TrayAvailability"/>;</item>
/// <item>стук — системным <c>ping</c> (<see cref="PingUtilityEcho"/>).</item>
/// </list>
/// </remarks>
public sealed partial class App : Application, IDisposable
{
    private readonly Stopwatch _startup = Stopwatch.StartNew();
    private readonly List<string> _marks = [];
    private long _lastMark;

    private readonly CrashGuard _crashGuard = new();
    private readonly AvaloniaUiThread _ui = new();
    private readonly PulseAudioPlatform _audioPlatform = new();
    private readonly XdgAutoStart _autoStart = new();
    private readonly UpdateExchange _updateHandoff = new();

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private AppSettings? _settings;
    private AdminAccessState? _access;
    private AppearanceService? _appearance;
    private DesktopAppearance? _desktopAppearance;
    private CallHistoryStore? _history;
    private PanelViewModel? _panel;
    private PanelWindow? _panelWindow;
    private IncomingCallPresenter? _incoming;
    private PhoneService? _phone;
    private PanelLineHost? _panelLine;
    private UpdateService? _updates;
    private Tray? _tray;
    private TrayAvailability? _trayAvailability;
    private SessionLifetime? _session;
    private global::EliteSIP.Platform.Linux.NetworkWatch? _networkWatch;
    private LogFile? _logFile;
    private string? _reconnectAfterCall;
    private bool _quitting;

    /// <summary>Одна копия — её занимает <see cref="Program"/> до Avalonia, а держит приложение.</summary>
    internal SingleInstance? Instance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Exit += (_, _) => Dispose();

        // Исключение в потоке окна по возможности переживается — что стоит
        // падения, решает общий CrashGuard (0.1.67). Падение поднимет служба.
        Dispatcher.UIThread.UnhandledException += (_, failure) =>
        {
            failure.Handled = _crashGuard.ShouldSurvive(failure.Exception);
            Record(failure.Exception, failure.Handled);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, failure) =>
        {
            if (failure.ExceptionObject is Exception error)
            {
                Record(error);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, failure) =>
        {
            Record(failure.Exception);
            failure.SetObserved();
        };

        Start(desktop.Args ?? []);
    }

    private void Mark(string stage)
    {
        var now = _startup.ElapsedMilliseconds;
        _marks.Add($"{stage} {now - _lastMark}");
        _lastMark = now;
    }

    private void Start(string[] args)
    {
        Instance?.Watch(() => Dispatcher.UIThread.Post(ShowPanel), Log);
        Mark("экземпляр");

        _settings = AppSettings.Load();
        _settings.AutoSave();
        Strings.Apply(Chosen(_settings.Appearance.Language));
        _access = new AdminAccessState(_settings.Admin.ToCredential());

        // Тема — до первого окна. Портал отвечает за доли миллисекунды; ждать
        // его здесь дешевле, чем перекрашивать панель на глазах. Но не дольше
        // DesktopAppearance.AnswerLimit: на входе в сеанс портала может ещё не
        // быть, и тогда тема приходит позже событием.
        //
        // Ждём вне контекста окна (Task.Run): продолжения Tmds.DBus иначе
        // просятся в поток окна, который как раз и ждёт, — взаимная
        // блокировка, на которой запуск висел 21 октября 2026.
        _desktopAppearance = new DesktopAppearance(Log);
        var appearance = _desktopAppearance;
        Task.Run(appearance.StartAsync).GetAwaiter().GetResult();
        _appearance = new AppearanceService(this, _desktopAppearance) { Appearance = _settings.Appearance.Theme };
        Mark("настройки и палитра");

        Log(SupportFacts.StartupLine(SettingsViewModel.Version, Environment.GetEnvironmentVariable));
        Log($"запуск: настройки {AppSettings.DefaultPath}"
            + $", файл {(File.Exists(AppSettings.DefaultPath) ? "есть" : "НЕТ")}"
            + $", настроена={_settings.Setup.IsCompleted}"
            + $", ключи запуска=[{string.Join(' ', args)}]"
            + $", служба={UserService.IsCurrentProcess}");

        if (!_settings.Setup.IsCompleted || args.Contains("--first-run"))
        {
            if (!RunFirstRun())
            {
                _ = QuitAsync(TimeSpan.Zero);
                return;
            }
        }

        _history = new CallHistoryStore(new CallHistoryStore.Settings(
            Path.Combine(
                Path.GetDirectoryName(AppSettings.DefaultPath)!,
                args.Contains("--demo") ? "history-demo.db" : "history.db")));
        Mark("история");

        _panel = new PanelViewModel
        {
            ShowSettings = new RelayCommand(_ => ShowSettings()),
            ShowHistory = new RelayCommand(_ => ShowHistory()),
        };

        _incoming = new IncomingCallPresenter(Log, () => _panelWindow);

        _phone = new PhoneService(
            _settings, _panel, _history, _incoming, _ui, _audioPlatform, Log, () => new PingUtilityEcho());
        Mark("телефон");

        WireRingtone(_settings, _phone, _incoming);
        WireCommands(_settings, _panel, _phone);

        // Wayland v1 не поддерживает (GraphicsSession): софтфон работает, но
        // защита окна входящего там слабее — оператор и старший смены должны
        // это видеть, а не узнать по жалобе.
        if (GraphicsSession.Current is GraphicsSessionKind.Wayland)
        {
            _panel.Notice = LinuxStrings.Get("PanelWaylandNotice");
        }

        PanelDemo.Apply(_panel, _settings, args, _ui);
        if (args.Contains("--demo"))
        {
            HistoryDemo.Seed(_history, _settings.Account.ProfileId);
        }

        _panelWindow = new PanelWindow(_panel)
        {
            RestorePlacement = () => _settings.Placement is { Left: { } left, Top: { } top } ? (left, top) : null,
            SavePlacement = (left, top) =>
            {
                _settings.Placement.Left = left;
                _settings.Placement.Top = top;
            },
            RestoreSize = () => _settings.Placement is { Width: { } width, Height: { } height } ? (width, height) : null,
            SaveSize = (width, height) =>
            {
                _settings.Placement.Width = width;
                _settings.Placement.Height = height;
            },
        };

        _panelWindow.Opened += (_, _) => Dispatcher.UIThread.Post(
            () => Log($"запуск: панель на экране через {(int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds} мс от старта процесса"),
            DispatcherPriority.Background);

        _panelWindow.Show();
        Mark("панель");

        _tray = new Tray(this, _panel, new Tray.TrayActions(
            IsPanelVisible: () => _panelWindow?.IsVisible is true,
            TogglePanel: TogglePanel,
            ShowHistory: ShowHistory,
            ShowSettings: ShowSettings,
            ToggleOffline: () => _panel.IsOfflineByChoice = !_panel.IsOfflineByChoice,
            Quit: Quit));

        // Прятать панель за значок — только если его есть кому нарисовать.
        // Иначе (GNOME без AppIndicator) закрытие сворачивает: запущенный,
        // невидимый и без выхода софтфон хуже открытой панели.
        _trayAvailability = new TrayAvailability(Log);
        _trayAvailability.Changed += () => Dispatcher.UIThread.Post(ApplyTrayAvailability);
        _ = Task.Run(_trayAvailability.StartAsync);
        Mark("значок");

        StartPanelLine(_settings, _access, _panel);
        WireAccount(_settings, _panel);
        StartSessionWatch();
        Mark("сеть");

        var beforeConnect = _startup.ElapsedMilliseconds;
        _ = _phone.ConnectAsync();
        var afterConnect = _startup.ElapsedMilliseconds;

        // Те же ключи, что у Windows: снимки окон сверяют скриптом, а
        // дотянуться до окна, которое открывается из панели, иначе нечем.
        if (args.Contains("--open-history"))
        {
            ShowHistory();
        }

        if (args.Contains("--open-admin"))
        {
            ShowAdministration();
        }

        var opened = Array.IndexOf(args, "--open-settings");
        if (opened >= 0)
        {
            ShowSettings();
            if (opened + 1 < args.Length
                && Enum.TryParse<SettingsSectionKind>(args[opened + 1], ignoreCase: true, out var section)
                && _settingsWindow?.Model is { } settingsModel)
            {
                settingsModel.Section = section;
            }
        }

        // «Выпуск жив» — не сразу, а через минуту: падение в первые секунды
        // после подъёма панели тоже означает выпуск, который не работает.
        // Отметку ждёт аварийный путь обновляльщика.
        var alive = _ui.CreateTimer(TimeSpan.FromMinutes(1));
        alive.Tick += (_, _) =>
        {
            alive.Stop();
            _updateHandoff.MarkStarted(ProductVersion.Current, Log);
        };
        alive.Start();

        var sinceProcess = (int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        Log($"запуск: панель готова через {sinceProcess} мс от старта процесса "
            + $"(рантайм до Avalonia {sinceProcess - (int)_startup.ElapsedMilliseconds} мс, "
            + $"наша подготовка {beforeConnect} мс, подъём регистрации {afterConnect - beforeConnect} мс; "
            + $"этапы: {string.Join(", ", _marks)})");
    }

    private static void WireRingtone(AppSettings settings, PhoneService phone, IncomingCallPresenter incoming)
    {
        incoming.StartRingtone = () =>
        {
            if (!settings.Ringtone.IsEnabled)
            {
                return;
            }

            phone.Sounds.StartRingtone(
                settings.Ringtone.CustomSoundPath,
                settings.Ringtone.Volume,
                settings.Ringtone.Output is RingtoneOutput.CallDevice ? settings.Audio.OutputDeviceId : null);
        };

        incoming.StopRingtone = phone.Sounds.StopRingtone;
    }

    private void WireCommands(AppSettings settings, PanelViewModel panel, PhoneService phone)
    {
        panel.CallOrHangUp = new RelayCommand(async _ =>
        {
            if (panel.IsInCall)
            {
                await phone.HangUpAsync();
            }
            else
            {
                await phone.PlaceCallAsync(panel.DialedNumber);
            }
        });

        panel.ToggleHold = new RelayCommand(async _ => await phone.ToggleHoldAsync());
        panel.ToggleMicrophone = new RelayCommand(_ => phone.ToggleMicrophone());
        panel.KeyPressed = key => phone.Sounds.PlayKeyTone(key, settings.Audio.OutputDeviceId);
        panel.StartConference = new RelayCommand(async _ => await phone.StartConferenceAsync());
        panel.Transfer = new RelayCommand(async _ => await phone.TransferAsync(panel.TransferNumber));
        panel.SendMacro = new RelayCommand(async parameter =>
        {
            if (parameter is MacroViewModel macro)
            {
                await phone.SendMacroAsync(macro);
            }
        });

        // «Не беспокоить» снимает регистрацию до конца сеанса и в настройки не пишется.
        panel.PropertyChanged += async (_, change) =>
        {
            if (change.PropertyName is nameof(PanelViewModel.IsOfflineByChoice))
            {
                if (panel.IsOfflineByChoice)
                {
                    await phone.DisconnectAsync();
                }
                else
                {
                    await phone.ConnectAsync();
                }
            }
        };

        SyncMacros();

        // Клавиши меняет и предустановка из Spark — правки склеиваются в одну.
        var macrosPending = false;
        void MacrosChanged()
        {
            if (macrosPending)
            {
                return;
            }

            macrosPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                macrosPending = false;
                SyncMacros();
            });
        }

        settings.Dtmf.Macros.CollectionChanged += (_, _) => MacrosChanged();
        settings.Dtmf.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(DtmfSettings.MacroColumns)
                or nameof(DtmfSettings.MacroHeight)
                or nameof(DtmfSettings.MacroHeightIsManual))
            {
                MacrosChanged();
            }
        };
    }

    private void StartPanelLine(AppSettings settings, AdminAccessState access, PanelViewModel panel)
    {
        _panelLine = new PanelLineHost(
            settings,
            access,
            isBlocked: () => panel.IsInCall,
            reset: ResetMachine,
            ui: _ui,
            log: Log)
        {
            Reregister = Reconnect,
            AnnounceNumber = number => panel.Notice = Strings.Format("PanelNumberChanged", number),
        };

        _panelLine.Start();
        Mark("линия панели");

        ClientEventLog.Shared.Start(() => _settings!, Log);

        _updates = new UpdateService(
            isBusy: () => panel.IsInCall,
            announce: version => Log(version is null
                ? "обновление больше не ждёт"
                : $"обновление {version} ждёт решения оператора"),
            alsoCheckPresets: () => _ = _panelLine?.CheckAsync(),
            ui: _ui,
            handoff: _updateHandoff,
            log: Log)
        {
            AskToInstall = AskToInstallUpdate,
            PrepareForRestart = () =>
            {
                ClientEventLog.Shared.NoteShutdown("установка обновления");
                Quit();
            },
        };

        _updates.Start();
        Mark("обновления");

        panel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(PanelViewModel.IsInCall) && !panel.IsInCall)
            {
                _panelLine?.HostBecameIdle();

                if (_reconnectAfterCall is { } pending)
                {
                    _reconnectAfterCall = null;
                    Reconnect(pending);
                }

                _updates?.Offer();
            }
        };
    }

    private void WireAccount(AppSettings settings, PanelViewModel panel)
    {
        settings.Account.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(AccountSettings.DisplayName) or nameof(AccountSettings.Username))
            {
                panel.StatusTitle = settings.Account.Username.Length > 0 ? settings.Account.Username : "—";
                panel.StatusLabel = PhoneService.ProfileLabel(settings);
            }
        };

        panel.StatusLabel = PhoneService.ProfileLabel(settings);

        panel.ReadProfiles = () =>
        {
            static string Title(string number, string label) =>
                label.Length > 0 && label != number ? $"{number} · {label}" : number;

            var active = new ProfileChoice(
                settings.Account.ProfileId,
                Title(settings.Account.Username, settings.Account.DisplayName),
                IsActive: true);

            return [active, .. settings.Profiles.Select(profile =>
                new ProfileChoice(profile.ProfileId, Title(profile.Username, profile.DisplayName), IsActive: false))];
        };

        panel.SwitchProfile = profileId =>
        {
            if (panel.IsInCall || !settings.SwitchProfile(profileId))
            {
                return;
            }

            Log($"профиль переключён на {settings.Account.Username}");
            Reconnect("смена профиля");
        };

        // Смена площадки: пересчитать адрес АТС и переподключиться.
        settings.Account.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is not nameof(AccountSettings.Site))
            {
                return;
            }

            var wanted = settings.Account.Site is WorkplaceSite.Remote
                ? settings.Pbx.RemoteAddress
                : settings.Pbx.OfficeAddress;

            if (wanted.Length > 0)
            {
                settings.Account.Domain = wanted;
            }

            Reconnect(settings.Account.Site is WorkplaceSite.Remote
                ? "переключение на работу из дома"
                : "переключение на работу из офиса");
        };
    }

    /// <summary>
    /// Конец сеанса, сон и сеть — оболочка W-L4 вместо <c>SessionEnding</c>,
    /// <c>PowerModeChanged</c> и <c>NetworkChange</c> Windows.
    /// </summary>
    private void StartSessionWatch()
    {
        _session = new SessionLifetime(Log)
        {
            // Снять регистрацию и выйти. Приходит не в потоке окна; замок
            // logind отпускается, когда задача кончится (не дольше 4 с).
            Ending = async reason =>
            {
                ClientEventLog.Shared.NoteShutdown(reason);
                await Dispatcher.UIThread.InvokeAsync(() => QuitAsync(TimeSpan.FromSeconds(3)));
            },
        };

        // Без ожидания: сигналы logind и менеджера сеанса подпишутся за
        // миллисекунды, а запуск панели ждать их не должен.
        _ = Task.Run(_session.StartAsync);

        _networkWatch = new global::EliteSIP.Platform.Linux.NetworkWatch(
            _ui,
            _session,
            onNetworkChanged: () => Reconnect("сеть сменилась"),
            onResume: () => Reconnect("машина проснулась"));
    }

    private void ApplyTrayAvailability()
    {
        if (_panelWindow is null || _trayAvailability is null)
        {
            return;
        }

        _panelWindow.HidesToTray = _trayAvailability.IsAvailable;
    }

    private static CultureInfo? Chosen(LanguageSetting language) => language switch
    {
        LanguageSetting.Russian => CultureInfo.GetCultureInfo("ru"),
        LanguageSetting.English => CultureInfo.InvariantCulture,
        _ => null,
    };

    private void Reconnect(string reason)
    {
        if (_phone is null || _panel is null)
        {
            return;
        }

        if (_panel.IsInCall)
        {
            _reconnectAfterCall = reason;
            Log($"{reason}: связь поднимется после разговора");
            return;
        }

        if (_panel.IsOfflineByChoice)
        {
            return;
        }

        Log($"{reason}: поднимаем регистрацию заново");
        _ = _phone.ConnectAsync(ReconnectStatus(reason));
    }

    private static string ReconnectStatus(string reason) => reason switch
    {
        "сеть сменилась" or "исправление сети" => "StatusFixingNetwork",
        "смена профиля" or "учётная запись изменена в «Управлении»" => "StatusSwitchingNumber",
        _ => "StatusReconnecting",
    };

    private void ShowPanel()
    {
        if (_panelWindow is null)
        {
            return;
        }

        if (!_panelWindow.IsVisible)
        {
            _panelWindow.Show();
        }

        if (_panelWindow.WindowState is WindowState.Minimized)
        {
            _panelWindow.WindowState = WindowState.Normal;
        }

        _panelWindow.Activate();
        _tray?.Refresh();
    }

    private void TogglePanel()
    {
        if (_panelWindow is null)
        {
            return;
        }

        if (_panelWindow.IsVisible)
        {
            _panelWindow.Hide();
        }
        else
        {
            _panelWindow.Show();
            _panelWindow.Activate();
        }

        _tray?.Refresh();
    }

    private void SyncMacros()
    {
        if (_panel is null || _settings is null)
        {
            return;
        }

        _panel.Macros.Clear();
        foreach (var macro in _settings.Dtmf.Macros)
        {
            _panel.Macros.Add(new MacroViewModel(macro.Title, macro.Sequence));
        }

        _panel.MacroColumns = _settings.Dtmf.MacroColumns;
        _panel.MacroHeight = _settings.Dtmf.MacroHeightIsManual
            ? _settings.Dtmf.MacroHeight
            : DtmfSettings.DefaultMacroHeight;
    }

    private void ResetMachine()
    {
        ClientEventLog.Shared.NoteShutdown("сброс машины");
        _updates?.Dispose();
        _updates = null;
        _panelLine?.Dispose();
        _panelLine = null;
        _phone?.Dispose();
        _phone = null;
        _history?.Dispose();
        _history = null;
        _logFile?.Dispose();
        _logFile = null;
        ClientEventLog.Shared.Forget();

        var failures = MachineReset.Wipe();
        if (failures.Count > 0)
        {
            Record(new IOException("чистка по отзыву не убрала: " + string.Join(", ", failures)));
        }

        // Новый экземпляр поднимет служба: выход с кодом, отличным от 0 и 1,
        // она считает падением и перезапускает. Без службы — запускаем сами.
        if (!UserService.IsCurrentProcess)
        {
            StartNewCopy();
        }

        _ = QuitAsync(TimeSpan.FromSeconds(2), exitCode: UserService.IsCurrentProcess ? 3 : 0);
    }

    private void Quit()
    {
        ClientEventLog.Shared.NoteShutdown("выход из приложения");
        _ = QuitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Снять регистрацию — не дольше <paramref name="limit"/> — и выйти.
    /// </summary>
    ///
    /// <remarks>
    /// На Windows это было синхронное ожидание с прокачкой очереди окна
    /// (<c>LeaveTheAir</c>); здесь то же асинхронно: поток окна свободен, пока
    /// REGISTER <c>expires=0</c> в пути.
    /// </remarks>
    private async Task QuitAsync(TimeSpan limit, int exitCode = 0)
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;

        if (_phone is not null)
        {
            var leaving = _phone.DisconnectAsync();
            await Task.WhenAny(leaving, Task.Delay(limit));
        }

        ClientEventLog.Shared.Dispose();

        if (_panelWindow is not null)
        {
            _panelWindow.AllowsClosing = true;
        }

        // Журнал — до выхода: строки телефона приходят в поток окна очередью,
        // а файл пишется фоном. Последние из них («регистрация снята») и есть
        // то, ради чего журнал читают после выхода, — даём им дойти.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        _logFile?.Flush();
        _desktop?.Shutdown(exitCode);
    }

    private void Log(string message)
    {
        if (_settings?.Maintenance.LogToFile is not true)
        {
            return;
        }

        if (Volatile.Read(ref _logFile) is not { } log)
        {
            var fresh = new LogFile(new LogFile.Settings(Path.GetDirectoryName(AppSettings.DefaultPath)!));
            log = Interlocked.CompareExchange(ref _logFile, fresh, null) ?? fresh;
            if (!ReferenceEquals(log, fresh))
            {
                fresh.Dispose();
            }
        }

        log.Write(message, "i");
    }

    internal static void Record(Exception failure, bool? survived = null)
    {
        try
        {
            var outcome = survived switch
            {
                true => " — пережито, софтфон работает дальше",
                false => " — софтфон закрывается",
                null => string.Empty,
            };

            var directory = Path.GetDirectoryName(AppSettings.DefaultPath)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + outcome
                    + Environment.NewLine + failure + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _appearance?.Dispose();
        _appearance = null;
        _desktopAppearance?.Dispose();
        _networkWatch?.Dispose();
        _networkWatch = null;
        _session?.Dispose();
        _session = null;
        _trayAvailability?.Dispose();
        _tray?.Dispose();
        _tray = null;
        _panelLine?.Dispose();
        _panelLine = null;
        _phone?.Dispose();
        _incoming?.Dispose();
        _phone = null;
        _history?.Dispose();
        _history = null;
        Instance?.Dispose();
        Instance = null;
        _logFile?.Dispose();
        GC.SuppressFinalize(this);
    }
}
