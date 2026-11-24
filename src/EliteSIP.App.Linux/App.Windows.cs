using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EliteSIP.App.Admin;
using EliteSIP.App.FirstRun;
using EliteSIP.App.History;
using EliteSIP.App.Incoming;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Platform;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Shell;
using EliteSIP.App.Theme;
using EliteSIP.Diagnostics;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App;

// Окна приложения — перевод ShowSettings, ShowAdministration, ShowHistory,
// RunFirstRun и их помощников из App.xaml.cs Windows-версии. Почему каждое
// окно одно на приложение, почему «Управление» запирается с закрытием и
// почему настройки закрываются вместе с его открытием — сказано там.
public sealed partial class App
{
    private SettingsWindow? _settingsWindow;
    private CallHistoryWindow? _historyWindow;
    private AdministrationWindow? _administrationWindow;

    /// <summary>
    /// «Поверх всех окон» — для всех окон программы сразу: иначе панель поверх
    /// закрывала бы настройки. Окна, которые поверх всегда (входящий, меню
    /// значка, заставка), правило не трогает.
    /// </summary>
    private void KeepWindowsOnTop(AppSettings settings)
    {
        static bool Governed(Window window) => window is not (IncomingCallWindow or TrayMenuWindow or StartupSplash);

        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (Governed(window))
            {
                window.Topmost = settings.Appearance.AlwaysOnTop;
            }
        });

        settings.Appearance.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(AppearanceSettings.AlwaysOnTop) || _desktop is null)
            {
                return;
            }

            foreach (var window in _desktop.Windows.Where(Governed))
            {
                window.Topmost = settings.Appearance.AlwaysOnTop;
            }
        };
    }

    private static void RestoreAndActivate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            RestoreAndActivate(_settingsWindow);
            return;
        }

        SettingsViewModel? model = null;
        model = new SettingsViewModel(_settings!, _ui, _audioPlatform, _autoStart)
        {
            Access = _access,
            OnWorkplaceChanged = () => _ = _panelLine?.CheckMachineAsync(),
            OnWorkplaceWentOffline = () => Log("формат работы изменён поверх закрепления: машина в ручном режиме"),
            Updates = _updates is null ? null : new UpdatesViewModel(_updates),
            OnRepairNetwork = () =>
            {
                Reconnect("исправление сети");
                model!.ReportSupport(Strings.Get("SupportNetworkRepaired"), SupportArea.Network);
            },
            OnCheckPresets = () =>
            {
                if (_panelLine is not null)
                {
                    _ = _panelLine.CheckMachineAsync();
                }

                _ = _panelLine?.CheckAsync();
                model!.ReportSupport(Strings.Get("SupportPresetsAsked"), SupportArea.Presets);
            },
            OnCollectLogs = () => CollectSupportArchive(model!),
            OnRunSelfTest = () => _ = RunAudioSelfTestAsync(model!),
            OnStopSelfTest = () => _phone?.SelfTest.Abort(),
            OnSaveRecording = () => _ = SaveSelfTestRecordingAsync(model!),
            OnReplayRecording = () => ReplaySelfTest(model!),
            ReadMeters = () => _phone is null
                ? default
                : new AudioMeterReading(
                    _phone.SelfTest.IsRunning,
                    _phone.IsCallActive,
                    _phone.SelfTest.TakeLevels() ?? _phone.TakeCallLevels()),
            OnReturnOnline = () => _panelLine?.ReturnOnline() ?? Task.CompletedTask,
        };

        _settingsWindow = new SettingsWindow(model, _appearance!, _phone!.Sounds)
        {
            RestartRequested = RestartApplication,
            TestOutputRequested = () => TestSpeakers(model),
            UnlockWorkplaceRequested = () => new AdminUnlockWindow(_settings!, _access!).Ask(_settingsWindow),
        };
        _settingsWindow.AdministrationRequested += ShowAdministration;
        _settingsWindow.Closed += (_, _) => _recordingPreview?.Cancel();

        Action refreshLink = model.RefreshLink;
        if (_panelLine is not null)
        {
            _panelLine.StateChanged += refreshLink;
        }

        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_panelLine is not null)
            {
                _panelLine.StateChanged -= refreshLink;
            }
        };

        _settingsWindow.Show();
    }

    private void ShowAdministration()
    {
        if (_administrationWindow is { IsVisible: true })
        {
            RestoreAndActivate(_administrationWindow);
            return;
        }

        if (!_access!.IsUnlocked && !new AdminUnlockWindow(_settings!, _access).Ask(_settingsWindow))
        {
            return;
        }

        var support = _panelLine is null
            ? null
            : new SupportViewModel(
                _settings!,
                checkNow: async () =>
                {
                    await _panelLine.CheckMachineAsync().ConfigureAwait(true);
                    await _panelLine.CheckAsync().ConfigureAwait(true);
                },
                returnOnline: () => _panelLine.ReturnOnline());

        if (support is not null)
        {
            _panelLine!.Report = support.ReportFromLine;
            _panelLine.StateChanged += support.Refresh;
        }

        var administration = new AdministrationViewModel(_settings!, _access, PreviewIncomingCall)
        {
            Support = support,
            PostToUi = _ui.Post,
            Updates = _updates is null ? null : new UpdatesViewModel(_updates),
            LastGuardReport = _incoming?.LastReport?.Summary(),
            ResetMachine = ResetMachine,
            IsInCall = () => _panel?.IsInCall is true,
            OnConnectionChanged = () => Reconnect("учётная запись изменена в «Управлении»"),
            WentOffline = () =>
            {
                Log("правки в «Управлении» сохранены — машина в оффлайне, Spark её настроек не трогает");
                support?.Refresh();
                _settingsWindow?.Model?.RefreshLink();
            },
        };

        _administrationWindow = new AdministrationWindow(administration);
        _administrationWindow.Closed += (_, _) =>
        {
            _administrationWindow = null;

            if (support is not null && _panelLine is not null)
            {
                _panelLine.StateChanged -= support.Refresh;
            }

            if (_panelLine is not null)
            {
                _panelLine.Report = null;
            }

            _panelLine?.HostBecameIdle();

            // Запирается вместе с закрытием, а не к концу смены (17 августа 2026).
            _access!.Lock();
            SyncMacros();
        };

        _administrationWindow.Show();
        _settingsWindow?.Close();
    }

    /// <summary>«Показать окно входящего» из «Управления» — без звонка.</summary>
    private void PreviewIncomingCall(bool asDealCall)
    {
        if (_incoming is null || _settings is null)
        {
            return;
        }

        var own = _settings.Account.Username;
        var masks = _settings.IncomingCall.MasksMobileNumbers;
        var subject = asDealCall
            ? IncomingCallSubject.Classify(own, callerName: null, requestsAutoAnswer: false, ownNumber: own, masksMobileNumbers: masks)
            : IncomingCallSubject.Classify("712", "Call_Center", requestsAutoAnswer: true, ownNumber: own, masksMobileNumbers: masks);

        _incoming.Show(subject, _settings.IncomingCall.ToPolicy(), onAnswer: () => { }, onDecline: () => { });
    }

    private void ShowHistory()
    {
        if (_historyWindow is { IsVisible: true })
        {
            RestoreAndActivate(_historyWindow);
            return;
        }

        var model = new CallHistoryViewModel(
            _history!,
            _settings!.Account.ProfileId,
            _settings.Account.Username,
            _settings.IncomingCall.MasksMobileNumbers,
            () => _settings.History.MaximumAgeInDays);

        _historyWindow = new CallHistoryWindow(model);
        _historyWindow.RedialRequested += number => _panel!.DialedNumber = number;
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    /// <summary>Мастер до панели. <see langword="false"/> — оператор его бросил: выйти.</summary>
    private bool RunFirstRun()
    {
        var model = new FirstRunViewModel(_settings!, _access!) { Log = Log };
        var window = new FirstRunWindow(model);
        _startupSplash?.DismissWhenShown(window);
        if (!window.Run())
        {
            return false;
        }

        if (model.LanguageChanges)
        {
            RestartApplication();
            return false;
        }

        _appearance!.Appearance = _settings!.Appearance.Theme;
        return true;
    }

    private bool AskToInstallUpdate(Version version)
    {
        ShowPanel();

        return new UpdateOffer(EliteSIP.App.Platform.ProductVersion.Current, version, _updates?.ReadyNotes ?? string.Empty).Ask(_panelWindow);
    }

    private long _selfTestGeneration;
    private async Task RunAudioSelfTestAsync(SettingsViewModel model)
    {
        if (_phone is not { } phone || phone.IsCallActive)
        {
            return;
        }

        var test = phone.SelfTest;
        var generation=++_selfTestGeneration;
        void OnProgress(string value) => Dispatcher.UIThread.Post(() => { if (generation==_selfTestGeneration) { model.ReportSelfTest(value); } });

        test.Progress += OnProgress;
        try
        {
            model.ReportSelfTestRunning(true);
            model.ReportSelfTest("готовлю тракт");
            var outcome = await test.RunAsync(phone.AudioConfiguration()).ConfigureAwait(true);
            ++_selfTestGeneration;
            model.ReportSelfTestRunning(false);
            model.ReportSelfTest(outcome.Summary);
            model.ReportRecording(test.HasRecording);
            Log($"самопроверка звука: {outcome.Summary}");
        }
        finally
        {
            test.Progress -= OnProgress;
            model.ReportSelfTestRunning(false);
        }
    }

    /// <summary>«Сохранить запись» проверки звука — диалог сохранения портала или GTK.</summary>
    private async Task SaveSelfTestRecordingAsync(SettingsViewModel model)
    {
        if (_phone is not { } phone || !phone.SelfTest.HasRecording || _settingsWindow is not { } window)
        {
            return;
        }

        var mode = _settings?.Audio.WindowsProcessing.ToString() ?? "Auto";
        var documents = await window.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Get("AudioSelfTestSaveTitle"),
            DefaultExtension = ".wav",
            SuggestedFileName = $"EliteSIP-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}-{mode}.wav",
            SuggestedStartLocation = documents,
            FileTypeChoices = [new FilePickerFileType("WAV") { Patterns = ["*.wav"] }],
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            phone.SelfTest.SaveLastRecording(path);
            model.ReportSelfTest(Strings.Format("AudioSelfTestSaved", path));
            Log($"самопроверка звука: запись сохранена ({mode})");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            model.ReportSelfTest(Strings.Format("AudioSelfTestSaveFailed", error.Message));
        }
    }

    /// <summary>
    /// Архив для поддержки — на рабочий стол, как на Windows.
    /// </summary>
    ///
    /// <remarks>
    /// Рабочий стол — по XDG (<c>xdg-user-dir DESKTOP</c>, «Рабочий стол» на
    /// русском Mint), а не <c>~/Desktop</c>: .NET берёт его из
    /// <c>user-dirs.dirs</c> сам.
    /// </remarks>
    private CancellationTokenSource? _recordingPreview;
    private async void ReplaySelfTest(SettingsViewModel model)
    {
        if (_phone is not { IsCallActive: false } phone || phone.SelfTest.ReadLastRecording() is not { } recording) { return; }
        await PlayAudioPreviewAsync(model,recording.Samples,recording.SampleRate);
    }

    private async void TestSpeakers(SettingsViewModel model)
    {
        if (_phone?.IsCallActive is true) { return; }
        const int rate=8000;
        var samples=new short[rate/2];
        for(var i=0;i<samples.Length;i++) { samples[i]=(short)(Math.Sin(2*Math.PI*800*i/rate)*short.MaxValue*.15); }
        await PlayAudioPreviewAsync(model,samples,rate);
    }

    private async Task PlayAudioPreviewAsync(SettingsViewModel model,short[] samples,int sampleRate)
    {
        _recordingPreview?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _recordingPreview = cancellation;
        try
        {
            await Task.Run(async () =>
            {
                using var output = EliteSIP.Audio.Pulse.PulseSignalSounds.Outputs.Open(_settings?.Audio.OutputDeviceId);
                cancellation.Token.ThrowIfCancellationRequested();
                output.Play(new RecordingSamples(samples, sampleRate, () => model.Settings.Audio.PlaybackVolume));
                try { await Task.Delay(TimeSpan.FromSeconds((double)samples.Length / sampleRate + .15), cancellation.Token); }
                finally { output.Stop(); }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        { model.ReportSelfTest(Strings.Get("AudioReplayFailed")); }
        finally { if (ReferenceEquals(_recordingPreview, cancellation)) { _recordingPreview = null; } }
    }
    private bool _collectingSupport;

    /// <summary>
    /// Архив журналов для техподдержки: по кнопке в настройках и молча — по
    /// запросу из Spark (<see cref="PanelLineHost.BuildSupportArchive"/>).
    /// </summary>
    private async Task<string> BuildSupportArchive(string destination)
    {
        var version = SettingsViewModel.Version;
        var installation = _settings?.Panel.InstallationID;
        var number = _settings?.Account.Username;
        var log = _logFile;
        var history = _history;
        var configuration = SupportFacts.Configuration(_settings);
        var directory = Path.GetDirectoryName(AppSettings.DefaultPath)!;
        return await Task.Run(async () =>
        {
            var summary = SupportFacts.Summary(version, installation, number, _audioPlatform, Environment.GetEnvironmentVariable);
            var extras = new Dictionary<string, byte[]>
            {
                ["environment.txt"] = System.Text.Encoding.UTF8.GetBytes(SupportFacts.EnvironmentSummary()),
                ["configuration.txt"] = System.Text.Encoding.UTF8.GetBytes(configuration),
                ["linux-service-journal.txt"] = System.Text.Encoding.UTF8.GetBytes(await UserServiceJournal.CollectAsync().ConfigureAwait(false)),
                ["linux-updater-journal.txt"] = System.Text.Encoding.UTF8.GetBytes(await UserServiceJournal.CollectAsync(updater: true).ConfigureAwait(false)),
                ["history-status.txt"] = System.Text.Encoding.UTF8.GetBytes(history?.DiagnosticSummary() ?? "история не открыта"),
            };
            return SupportArchive.MakeFromDirectory(directory, summary, destination, log, extras);
        });
    }

    private async void CollectSupportArchive(SettingsViewModel model)
    {
        if (_collectingSupport) { return; }
        _collectingSupport = true;
        model.IsCollectingLogs = true;
        model.ReportSupport(Strings.Get("SupportLogsCollecting"), SupportArea.Logs);
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (desktop.Length == 0 || !Directory.Exists(desktop)) { desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            var made = await BuildSupportArchive(Path.Combine(desktop, SupportArchive.SuggestedName()));
            model.ReportSupport(Strings.Format("SupportLogsCollected", Path.GetFileName(made)), SupportArea.Logs);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            model.ReportSupport(Strings.Format("SupportLogsFailed", error.Message), SupportArea.Logs);
        }
        finally { _collectingSupport = false; model.IsCollectingLogs = false; }
    }

    /// <summary>
    /// Перезапуск — смена языка (язык выбирается при запуске).
    /// </summary>
    ///
    /// <remarks>
    /// Под службой — выход с кодом 3: служба считает его падением и поднимает
    /// софтфон через 5 с, уже с новым языком. Без службы — новый процесс сам.
    /// </remarks>
    private void RestartApplication()
    {
        _appearanceSave.Flush();
        ClientEventLog.Shared.NoteShutdown("перезапуск приложения");

        if (!UserService.IsCurrentProcess)
        {
            StartNewCopy();
        }

        _ = QuitAsync(TimeSpan.FromSeconds(2), exitCode: UserService.IsCurrentProcess ? 3 : 0);
    }

    /// <summary>
    /// Запустить новую копию с теми же ключами — без службы, после перезапуска
    /// или сброса машины.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// Замок одной копии отпускается заранее: иначе новая копия, найдя его
    /// занятым, решила бы, что она вторая, разбудила бы эту и вышла.
    /// </para>
    /// <para>
    /// Под <c>dotnet EliteSIP.App.Linux.dll</c> (запуск из сборки) путь
    /// процесса — сам <c>dotnet</c>, а путь сборки — первый из
    /// <see cref="Environment.GetCommandLineArgs"/>; у выпуска путь процесса и
    /// есть программа.
    /// </para>
    /// </remarks>
    private void StartNewCopy()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }

        Instance?.Dispose();
        Instance = null;

        var arguments = Environment.GetCommandLineArgs();
        var hosted = Path.GetFileNameWithoutExtension(executable) == "dotnet";
        using var started = Process.Start(executable, hosted ? arguments : arguments.Skip(1));
    }
}
