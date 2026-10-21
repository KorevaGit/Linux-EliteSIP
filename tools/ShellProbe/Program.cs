using System.Diagnostics;
using System.Runtime.Versioning;
using EliteSIP.Platform.Linux;
using EliteSIP.SipCore;

[assembly: SupportedOSPlatform("linux")]

// Стенд оболочки Linux (W-L4). Каждый режим — один вопрос к живому сеансу.
//
//   appearance           тема и акцент из портала; ждёт смены 60 с
//   tray                 есть ли область уведомлений; следит 60 с
//   session [секунды]    замок выключения у logind, сон, SIGTERM
//   instance             одна копия: запустить дважды в двух терминалах
//   attention <XID>      просить внимания к окну 5 с, потом снять
//   knock <адрес> [байт] стук через ping, как PortKnocker
//   service              служба ли этот процесс; DISPLAY в окружении
//   crash                падение .NET (FailFast) — для Restart=on-failure
//   exit <код>           выход с кодом — для RestartPreventExitStatus

var stopwatch = Stopwatch.StartNew();
void Log(string line) => Console.WriteLine($"{stopwatch.Elapsed.TotalSeconds,7:0.000}  {line}");

switch (args.FirstOrDefault())
{
    case "appearance":
    {
        using DesktopAppearance appearance = new(Log);
        await appearance.StartAsync();
        Log($"тёмная={appearance.PrefersDark}, акцент={appearance.Accent?.ToString() ?? "нет"}");
        appearance.Changed += () => Log($"смена: тёмная={appearance.PrefersDark}, акцент={appearance.Accent?.ToString() ?? "нет"}");
        await Task.Delay(TimeSpan.FromSeconds(60));
        break;
    }

    case "tray":
    {
        using TrayAvailability tray = new(Log);
        await tray.StartAsync();
        Log($"значок можно ставить: {tray.IsAvailable}");
        tray.Changed += () => Log($"смена: значок можно ставить: {tray.IsAvailable}");
        await Task.Delay(TimeSpan.FromSeconds(60));
        break;
    }

    case "session":
    {
        var seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 60;
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using SessionLifetime session = new(Log)
        {
            Ending = async reason =>
            {
                // Вместо REGISTER expires=0 — полсекунды работы.
                Log($"конец сеанса: {reason}; «снимаю регистрацию»");
                await Task.Delay(500);
                Log("«регистрация снята»");
                ended.TrySetResult();
            },
        };

        session.Resumed += () => Log("пробуждение");
        await session.StartAsync();
        Log($"pid {Environment.ProcessId}; замки: systemd-inhibit --list | grep EliteSIP");

        await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        await Task.Delay(200);
        Log(ended.Task.IsCompleted ? "выход после конца сеанса" : "время вышло");
        break;
    }

    case "instance":
    {
        using var instance = SingleInstance.Claim(Log);
        if (instance is null)
        {
            Log("вторая копия: первая разбужена, выхожу");
            break;
        }

        Log($"первая копия, pid {Environment.ProcessId}; жду побудок 60 с");
        instance.Watch(() => Log("побудка: показать панель"), Log);
        await Task.Delay(TimeSpan.FromSeconds(60));
        break;
    }

    case "attention" when args.Length > 1:
    {
        var window = Convert.ToUInt64(args[1], args[1].StartsWith("0x", StringComparison.Ordinal) ? 16 : 10);
        Log($"просьба внимания: {WindowAttention.Request((nuint)window)}");
        await Task.Delay(TimeSpan.FromSeconds(5));
        Log($"снята: {WindowAttention.Cancel((nuint)window)}");
        break;
    }

    case "knock" when args.Length > 1:
    {
        var bytes = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 228;
        PortKnockSequence sequence = new() { Steps = [new PortKnockStep(bytes, args[1])], SpacingSeconds = 1 };

        using var knocker = PortKnocker.ForServer(args[1], WorkplaceSite.Remote, sequence, Log, () => new PingUtilityEcho());
        await knocker!.OpenPathAsync(SipPathOpenReason.Registration);
        break;
    }

    case "service":
        Log($"служба: {UserService.IsCurrentProcess}, DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}, "
            + $"XAUTHORITY={Environment.GetEnvironmentVariable("XAUTHORITY")}");
        break;

    case "crash":
        Log("падаю");
        Environment.FailFast("ShellProbe crash");
        break;

    case "exit" when args.Length > 1:
        return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);

    default:
        Console.Error.WriteLine("режимы: appearance | tray | session [с] | instance | attention <XID> | knock <адрес> [байт] | service | crash | exit <код>");
        return 2;
}

return 0;
