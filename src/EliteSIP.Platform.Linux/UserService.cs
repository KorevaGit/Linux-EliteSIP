using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Софтфон как пользовательская служба systemd — Linux-половина
/// <c>CrashGuard.RegisterRestart</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Зачем служба.</b> На Windows упавший софтфон поднимает сама система
/// (<c>RegisterApplicationRestart</c>). На Linux это умеет systemd:
/// <c>elitesip.service</c> из пакета с <c>Restart=on-failure</c>. Падение
/// .NET — это <c>SIGABRT</c>, и служба поднимается через 5 с; три падения за
/// пять минут — и systemd перестаёт, как Windows не поднимает процесс,
/// проживший меньше минуты.
/// </para>
/// <para>
/// <b>Кто кого запускает.</b> Ярлык в меню и автозапуск XDG запускают сам
/// исполняемый файл. Если он запущен не службой, а служба установлена, он
/// просит systemd запустить её и выходит: работать должна копия под
/// присмотром. Вторая копия при этом сюда не доходит — её раньше
/// останавливает <see cref="SingleInstance"/> и будит первую.
/// </para>
/// <para>
/// <b>Выход из сеанса — не падение.</b> Обрыв X-сервера Xlib заканчивает
/// <c>exit(1)</c>, и в службе стоит <c>RestartPreventExitStatus=1</c>:
/// поднимать софтфон в сеанс, которого уже нет, незачем. Поэтому код 1
/// приложение для своих выходов не берёт.
/// </para>
/// <para>
/// <b>Почему не <c>PartOf=graphical-session.target</c>, как в плане.</b>
/// Cinnamon на Mint 22.3 эту цель не поднимает вовсе (<c>inactive</c> в
/// живом сеансе, 21 октября 2026), и привязка к ней ничего бы не значила.
/// Окружение сеанса (<c>DISPLAY</c>, <c>XAUTHORITY</c>, шина) Cinnamon в
/// менеджер пользователя передаёт — службе его хватает.
/// </para>
/// </remarks>
internal static class UserService
{
    internal const string Unit = "elitesip.service";

    /// <summary>Где пакет кладёт службу.</summary>
    internal const string InstalledUnit = "/usr/lib/systemd/user/" + Unit;

    /// <summary>
    /// Этот процесс и есть служба.
    /// </summary>
    ///
    /// <remarks>
    /// <c>SYSTEMD_EXEC_PID</c> (systemd 248+), а не <c>INVOCATION_ID</c>: вторую
    /// наследуют и дочерние процессы службы, а первая совпадает с нашим
    /// номером, только если systemd запустил именно нас.
    /// </remarks>
    internal static bool IsCurrentProcess =>
        Environment.GetEnvironmentVariable("SYSTEMD_EXEC_PID") is { } pid
        && pid == Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Передать запуск службе. <see langword="true"/> — служба запускается,
    /// этому процессу пора выйти.
    /// </summary>
    ///
    /// <remarks>
    /// <see langword="false"/> — работать здесь: службы нет (запуск из
    /// сборки), мы уже служба, или systemd отказал. Отказ systemd не повод
    /// оставить оператора без телефона — софтфон просто работает без
    /// перезапуска после падения, и это пишется в журнал.
    /// </remarks>
    internal static bool TryHandOver(Action<string> log, string installedUnit = InstalledUnit)
    {
        if (IsCurrentProcess || !File.Exists(installedUnit))
        {
            return false;
        }

        try
        {
            ProcessStartInfo info = new("systemctl")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };

            // --no-block: дождаться только постановки в очередь. Иначе запуск
            // ждал бы, пока служба поднимется, а ей нужен наш замок одной
            // копии, который мы отпустим, только выйдя.
            foreach (var argument in new[] { "--user", "--no-block", "start", Unit })
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                return true;
            }

            log($"служба не запустилась (systemctl {process.ExitCode}: {errors.Trim()}) — работаю без неё");
            return false;
        }
        catch (Win32Exception error)
        {
            log($"служба не запустилась ({error.Message}) — работаю без неё");
            return false;
        }
    }
}
