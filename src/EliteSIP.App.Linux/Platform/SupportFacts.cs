using System.Reflection;
using EliteSIP.Audio;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Platform;

/// <summary>
/// Из чего собран выпуск и на чём он работает — для строки запуска в журнале
/// и для архива поддержки.
/// </summary>
///
/// <remarks>Все исходники принадлежат Linux; ревизия берётся из его репозитория.</remarks>
internal static class SupportFacts
{
    /// <summary>Ревизия Linux при сборке либо source-archive для архива без Git.</summary>
    internal static string SourceRevision { get; } =
        typeof(SupportFacts).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SourceRevision")?.Value is { Length: > 0 } code
                ? code
                : "неизвестен";

    /// <summary>Строка запуска: версия, исходники Linux, сеанс.</summary>
    internal static string StartupLine(string version, Func<string, string?> environment) =>
        $"запуск: EliteSIP {version} (Linux), исходники Linux {SourceRevision}, сеанс {GraphicsSession.Describe(environment)}";

    /// <summary>
    /// <c>summary.txt</c> архива. Секретов здесь нет: номер и идентификатор
    /// машины поддержка и так спрашивает первыми.
    /// </summary>
    internal static string Summary(
        string version,
        string? installation,
        string? number,
        IAudioPlatform audio,
        Func<string, string?> environment,
        string? audioStatus = null)
    {
        var lines = new List<string>
        {
            $"EliteSIP {version} (Linux)",
            $"исходники Linux: {SourceRevision}",
            $"машина: {installation}",
            $"добавочный: {number}",
            $"система: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            $"сеанс: {GraphicsSession.Describe(environment)}",
        };

        if (audioStatus is null)
        {
            lines.AddRange(Sound(audio));
        }
        else
        {
            lines.Add(audioStatus);
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string Configuration(EliteSIP.App.Settings.AppSettings? settings)
    {
        if (settings is null) { return "настройки не загружены"; }
        // Только явный список безопасных полей; Credentials и ключи канала не сериализуются.
        return string.Join(Environment.NewLine,
            $"площадка: {settings.Account.Site}",
            $"АТС: офис={settings.Pbx.OfficeAddress}; порт={settings.Pbx.Port}; транспорт={settings.Pbx.Transport}; expires={settings.Pbx.RegistrationExpirySeconds}",
            $"TLS принимает любой сертификат: {settings.Pbx.AcceptsAnyTlsCertificate}",
            $"звук: микрофон={settings.Audio.InputDeviceId}; выход={settings.Audio.OutputDeviceId}; усиление={settings.Audio.MicrophoneGain}; громкость={settings.Audio.PlaybackVolume}",
            $"обработка: шум={settings.Audio.NoiseSuppression}; голоса вокруг={settings.Audio.BackgroundVoiceSuppression}; АРУ={settings.Audio.AutomaticGainControl}; приём={settings.Audio.ReceiveGainControl}",
            $"рингтон: выход={settings.Ringtone.Output}; громкость={settings.Ringtone.Volume}",
            $"журнал: файл={settings.Maintenance.LogToFile}; SIP-трасса={settings.Maintenance.LogsSipTrace}",
            $"стук: интервал={settings.PortKnock.RepeatIntervalSeconds}; пауза={settings.PortKnock.SpacingSeconds}");
    }

    internal static string ProfileStatus()
    {
        string path = Settings.AppSettings.DefaultPath;
        return string.Join(Environment.NewLine, new[]
        {
            path,
            path + ".tmp",
            path + Settings.AppSettings.RecoverySuffix,
            path + Settings.AppSettings.RecoverySuffix + ".tmp",
            path + Settings.AppSettings.UnreadableSuffix,
            Path.Combine(Path.GetDirectoryName(path)!, Settings.AppSettings.ResetMarkerName),
        }.Select(file =>
        {
            string name = Path.GetFileName(file);
            try
            {
                var info = new FileInfo(file);
                return $"{name}: {info.Length} байт; последнее изменение {info.LastWriteTimeUtc:O}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"{name}: {error.GetType().Name}";
            }
        }));
    }

    internal static string EnvironmentSummary()
    {
        var lines = new List<string>
        {
            $"машина: {Environment.MachineName}",
            $"ОС: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            $"архитектура: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}; процесс: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            $"CLR: {Environment.Version}; CPU: {Environment.ProcessorCount}; память процесса: {Environment.WorkingSet}; PID: {Environment.ProcessId}",
            $"часовой пояс: {TimeZoneInfo.Local.Id}; время ОС: {Environment.TickCount64} мс",
        };
        foreach (var path in new[] { "/proc/cpuinfo", "/proc/meminfo", "/sys/class/dmi/id/sys_vendor", "/sys/class/dmi/id/product_name" })
        {
            try { lines.Add(path + Environment.NewLine + File.ReadAllText(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { lines.Add(path + ": недоступно"); }
        }
        try
        {
            foreach (var item in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                lines.Add($"сеть: {item.Name}; {item.Description}; {item.Id}; {item.NetworkInterfaceType}; {item.OperationalStatus}; MAC={item.GetPhysicalAddress()}; скорость={item.Speed}");
                var properties = item.GetIPProperties();
                lines.Add("адреса: " + string.Join(", ", properties.UnicastAddresses.Select(address => address.Address.ToString())));
                lines.Add("DNS: " + string.Join(", ", properties.DnsAddresses));
                lines.Add("шлюзы: " + string.Join(", ", properties.GatewayAddresses.Select(address => address.Address.ToString())));
            }
        }
        catch (Exception error) when (error is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException) { lines.Add("сеть: " + error.Message); }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Файлы журнала и <c>crash.log</c> — пути, а не строки: архив копирует
    /// файлы сам (<see cref="EliteSIP.Diagnostics.SupportArchive.Make"/>).
    /// </summary>
    internal static IReadOnlyList<string> Files(IEnumerable<string> logs, string profileDirectory)
    {
        var files = logs.ToList();

        var crash = Path.Combine(profileDirectory, "crash.log");
        if (File.Exists(crash))
        {
            files.Add(crash);
        }

        return files;
    }

    /// <summary>
    /// Устройства по умолчанию и списки: половина жалоб на звук — «говорю в
    /// ноутбук, слушаю гарнитуру» (план, «Что ещё вскрылось»), и это видно
    /// отсюда сразу.
    /// </summary>
    private static IEnumerable<string> Sound(IAudioPlatform audio)
    {
        IReadOnlyList<AudioDevice> inputs;
        IReadOnlyList<AudioDevice> outputs;
        AudioDevice? input;
        AudioDevice? output;

        try
        {
            inputs = audio.Devices(AudioDeviceDirection.Capture);
            outputs = audio.Devices(AudioDeviceDirection.Render);
            input = audio.DefaultDevice(AudioDeviceDirection.Capture);
            output = audio.DefaultDevice(AudioDeviceDirection.Render);
        }
        catch (InvalidOperationException error)
        {
            return [$"звук: сервер недоступен ({error.Message})"];
        }

        return
        [
            $"микрофон по умолчанию: {input?.Name ?? "нет"}",
            $"выход по умолчанию: {output?.Name ?? "нет"}",
            $"микрофоны: {string.Join("; ", inputs.Select(device => device.Name))}",
            $"выходы: {string.Join("; ", outputs.Select(device => device.Name))}",
        ];
    }
}
