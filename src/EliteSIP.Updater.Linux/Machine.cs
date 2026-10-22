using System.ComponentModel;
using System.Diagnostics;

namespace EliteSIP.Updater;

/// <summary>То, что обновляльщик спрашивает у системы и просит у неё сделать.</summary>
///
/// <remarks>
/// Интерфейсом — ради проверок: сценарий (<see cref="UpdaterRun"/>) прогоняется
/// без root, systemd и logind, а сами вызовы здесь — запуск трёх программ,
/// которые проверены на стенде.
/// </remarks>
internal interface IMachine
{
    /// <summary>Версия, которая стоит сейчас. <c>null</c> — прочитать не удалось.</summary>
    public Version? InstalledVersion();

    /// <summary>Запущен ли софтфон хоть в каком-то сеансе.</summary>
    public bool IsAppRunning();

    /// <summary>Идёт ли установка, начатая прежним тактом.</summary>
    public bool IsInstalling();

    /// <summary>Начать установку пакета и сразу вернуться. <c>false</c> — не началась.</summary>
    public bool StartInstall(string package, string installLog, out string why);

    /// <summary>
    /// Поднять софтфон в сеансах тех, кто сидит за машиной. <c>false</c> —
    /// не поднят ни в одном.
    /// </summary>
    public bool RestartInSessions(out string why);
}

/// <summary>Настоящая машина: systemd, logind, apt.</summary>
internal sealed class SystemdMachine(Layout layout) : IMachine
{
    /// <summary>Временная служба установки. Её имя — замок «установка идёт».</summary>
    internal const string InstallUnit = "elitesip-install.service";

    /// <summary>Пользовательская служба софтфона из пакета.</summary>
    internal const string AppUnit = "elitesip.service";

    /// <summary>
    /// Исполняемый файл приложения в <c>/opt/elitesip</c>. Имя процесса ядро
    /// берёт из него, и обрезает до 15 знаков — «EliteSIP» влезает.
    /// </summary>
    internal const string AppProcess = "EliteSIP";

    public Version? InstalledVersion()
    {
        try
        {
            var raw = FileVersionInfo.GetVersionInfo(layout.AppAssembly).FileVersion;

            return Version.TryParse(raw, out var version) ? version : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsAppRunning()
    {
        var processes = Process.GetProcessesByName(AppProcess);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    public bool IsInstalling()
    {
        var state = Run("systemctl", ["show", "--property=ActiveState", "--value", InstallUnit]);

        return state.Output.Trim() is "active" or "activating" or "deactivating" or "reloading";
    }

    /// <summary>Ставит пакет отдельной временной службой и сразу возвращается.</summary>
    ///
    /// <remarks>
    /// <para>
    /// <b>Почему не дочерним процессом.</b> Урок Windows 11 сентября 2026:
    /// обновляльщик, который сам ждёт установщика, живёт внутри того, что
    /// установщик заменяет. Здесь хуже: дочерний процесс живёт в группе
    /// <c>elitesip-updater.service</c>, и всё, что сделает с этой службой
    /// пакет или systemd по ходу установки, сделает и с <c>apt-get</c> посреди
    /// <c>dpkg</c>. Своя служба от нашей не зависит вовсе — как задача
    /// установки на Windows.
    /// </para>
    /// <para>
    /// <c>--service-type=exec</c>: <c>systemd-run</c> возвращается, как только
    /// <c>apt-get</c> запущен, и с этой минуты служба <c>active</c> — следующий
    /// такт видит, что установка идёт (<see cref="IsInstalling"/>). Срок —
    /// полчаса, как у задачи установки на Windows: зависшая установка
    /// снимается сама. Когда она кончится — удачей или нет, — служба будит
    /// обновляльщика (<c>ExecStopPost</c>), и тот поднимает софтфон, не ждя
    /// своего такта.
    /// </para>
    /// <para>
    /// <c>apt-get</c>, а не <c>dpkg -i</c>: новому выпуску могут понадобиться
    /// новые зависимости. Без вопросов: <c>DEBIAN_FRONTEND=noninteractive</c>,
    /// <c>-y</c>, а правленный администратором файл в <c>/etc</c> остаётся его
    /// (<c>--force-confold</c>) — спрашивать в службе некого. Замок
    /// <c>dpkg</c> ждётся до десяти минут: администратор может как раз ставить
    /// обновления системы.
    /// </para>
    /// <para>
    /// Журнал установки — в своём каталоге, куда оператор не пишет.
    /// </para>
    /// </remarks>
    public bool StartInstall(string package, string installLog, out string why)
    {
        var result = Run("systemd-run",
        [
            "--unit=" + InstallUnit,
            "--description=EliteSIP: установка выпуска",
            "--collect",
            "--quiet",
            "--service-type=exec",
            "--property=RuntimeMaxSec=30min",
            "--property=StandardOutput=append:" + installLog,
            "--property=StandardError=append:" + installLog,
            "--property=ExecStopPost=/usr/bin/systemctl --no-block start elitesip-updater.service",
            "--setenv=DEBIAN_FRONTEND=noninteractive",
            "/usr/bin/apt-get", "install", "--yes",
            "-o", "DPkg::Lock::Timeout=600",
            "-o", "Dpkg::Options::=--force-confdef",
            "-o", "Dpkg::Options::=--force-confold",
            package,
        ]);

        why = result.ExitCode == 0 ? string.Empty : Explain(result);
        return result.ExitCode == 0;
    }

    /// <summary>Поднимает софтфон в каждом графическом сеансе из logind.</summary>
    ///
    /// <remarks>
    /// <para>
    /// Аналог <c>WTSQueryUserToken</c> + <c>CreateProcessAsUser</c> на Windows,
    /// только проще: софтфон и так работает пользовательской службой
    /// (<c>elitesip.service</c>, W-L4), и root просит менеджер пользователя
    /// перезапустить её. Окружение сеанса (<c>DISPLAY</c>, шина) у менеджера
    /// пользователя уже есть — его туда передал Cinnamon.
    /// </para>
    /// <para>
    /// <c>restart</c>, а не <c>start</c>: если софтфон по какой-то причине не
    /// ушёл перед установкой, в памяти у него прежний выпуск.
    /// </para>
    /// <para>
    /// Сеансы — местные графические (<c>x11</c>, <c>wayland</c>) пользователей,
    /// по одному разу на человека. Сеанс по SSH софтфона не получит.
    /// </para>
    /// </remarks>
    public bool RestartInSessions(out string why)
    {
        var users = GraphicalUsers();
        if (users.Count == 0)
        {
            why = "за машиной никого нет";
            return false;
        }

        var failures = new List<string>();
        var raised = 0;

        foreach (var user in users)
        {
            var result = Run("systemctl", ["--user", "--machine=" + user + "@", "restart", AppUnit]);
            if (result.ExitCode == 0)
            {
                raised++;
            }
            else
            {
                failures.Add($"{user}: {Explain(result)}");
            }
        }

        why = string.Join("; ", failures);
        return raised > 0;
    }

    /// <summary>Пользователи местных графических сеансов.</summary>
    internal static IReadOnlyList<string> GraphicalUsers()
    {
        var users = new List<string>();

        foreach (var line in Run("loginctl", ["list-sessions", "--no-legend"]).Output.Split('\n'))
        {
            var session = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (session is null)
            {
                continue;
            }

            var properties = ParseProperties(Run("loginctl",
                ["show-session", session, "--property=Name", "--property=Type", "--property=Class", "--property=Remote"]).Output);

            if (IsGraphicalUser(properties)
                && properties.TryGetValue("Name", out var name)
                && !users.Contains(name, StringComparer.Ordinal))
            {
                users.Add(name);
            }
        }

        return users;
    }

    /// <summary>Местный графический сеанс человека — не экран входа и не SSH.</summary>
    internal static bool IsGraphicalUser(IReadOnlyDictionary<string, string> session) =>
        session.GetValueOrDefault("Class") == "user"
            && session.GetValueOrDefault("Remote") == "no"
            && session.GetValueOrDefault("Type") is "x11" or "wayland"
            && !string.IsNullOrEmpty(session.GetValueOrDefault("Name"));

    /// <summary>Вывод <c>Ключ=Значение</c> построчно.</summary>
    internal static Dictionary<string, string> ParseProperties(string output)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n'))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                properties[line[..equals]] = line[(equals + 1)..].Trim();
            }
        }

        return properties;
    }

    private static string Explain(ProcessResult result) =>
        $"код {result.ExitCode}: {(result.Errors.Trim() is { Length: > 0 } errors ? errors : result.Output.Trim())}";

    private static ProcessResult Run(string program, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, $"{program} не запустился");
            }

            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();

            // Две минуты — с запасом: всё, что здесь зовётся, отвечает за
            // секунды. Дольше — значит висит, и такт должен кончиться.
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                process.Kill();
                return new ProcessResult(-1, output, $"{program} не ответил за две минуты");
            }

            return new ProcessResult(process.ExitCode, output, errors.GetAwaiter().GetResult());
        }
        catch (Win32Exception error)
        {
            return new ProcessResult(-1, string.Empty, $"{program}: {error.Message}");
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string Output, string Errors);
}
