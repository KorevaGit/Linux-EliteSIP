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

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        SettingsViewModel? model = null;
        model = new SettingsViewModel(_settings!, _ui, _audioPlatform, _autoStart)
        {
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
        };
        _settingsWindow.AdministrationRequested += ShowAdministration;

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
            _administrationWindow.Activate();
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
            _historyWindow.Activate();
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
        if (!new FirstRunWindow(model).Run())
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

        var answer = Dialog.Ask(
            _panelWindow,
            Strings.Format("UpdateOfferTitle", version.ToString(3)),
            Strings.Get("UpdateOfferBody"),
            confirmTitle: Strings.Get("UpdateOfferInstall"));

        return answer is DialogAnswer.Confirm;
    }

    private async Task RunAudioSelfTestAsync(SettingsViewModel model)
    {
        if (_phone is not { } phone || phone.IsCallActive)
        {
            return;
        }

        var test = phone.SelfTest;
        void OnProgress(string value) => Dispatcher.UIThread.Post(() => model.ReportSelfTest(value));

        test.Progress += OnProgress;
        try
        {
            model.ReportSelfTest("готовлю тракт");
            var outcome = await test.RunAsync(phone.AudioConfiguration()).ConfigureAwait(true);
            model.ReportSelfTest(outcome.Summary);
            model.ReportRecording(test.HasRecording);
            Log($"самопроверка звука: {outcome.Summary}");
        }
        finally
        {
            test.Progress -= OnProgress;
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
    private void CollectSupportArchive(SettingsViewModel model)
    {
        try
        {
            _logFile?.Flush();
            var lines = new List<string>();
            foreach (var path in (_logFile?.Files() ?? []).Reverse())
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    lines.Add(line);
                }
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (desktop.Length == 0 || !Directory.Exists(desktop))
            {
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var summary = $"EliteSIP {SettingsViewModel.Version} (Linux)"
                + $"{Environment.NewLine}машина: {_settings?.Panel.InstallationID}"
                + $"{Environment.NewLine}добавочный: {_settings?.Account.Username}"
                + $"{Environment.NewLine}система: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

            var made = SupportArchive.Make(lines, summary, Path.Combine(desktop, SupportArchive.SuggestedName()));
            model.ReportSupport(Strings.Format("SupportLogsCollected", Path.GetFileName(made)), SupportArea.Logs);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            model.ReportSupport(Strings.Format("SupportLogsFailed", error.Message), SupportArea.Logs);
        }
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
