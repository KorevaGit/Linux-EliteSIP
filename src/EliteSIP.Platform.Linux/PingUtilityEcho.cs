using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using EliteSIP.SipCore;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Стук через системный <c>ping</c> из iputils.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Почему не <see cref="Ping"/>.</b> На Linux Mint 22 у оператора нет ни
/// <c>CAP_NET_RAW</c>, ни ICMP-сокета без прав: <c>net.ipv4.ping_group_range</c>
/// стоит <c>1 0</c>, то есть пустой. <see cref="Ping"/> в этом случае сам
/// запускает <c>ping</c>, но данные своего содержимого не принимает:
/// <see cref="PlatformNotSupportedException"/> на каждом пакете (замер
/// 21 октября 2026). А стук и есть данные заданной длины.
/// </para>
/// <para>
/// <b>Почему <c>ping</c>, а не правило <c>sysctl</c> в пакете.</b> У
/// <c>/usr/bin/ping</c> уже есть <c>cap_net_raw</c> — его ставит сам пакет
/// iputils-ping, и он в зависимостях нашего <c>.deb</c>. Раздвигать
/// <c>ping_group_range</c> на всю машину ради одной программы — менять
/// настройку безопасности, которую администратор заказчика не просил менять.
/// </para>
/// <para>
/// <b>Что получает шлюз.</b> <c>ping -s N</c> шлёт ровно N байт данных, как и
/// скрипт подключения, с которого стук начинался: правилу MikroTik, которое
/// смотрит на длину, это то же самое. Содержимое заполняет <c>ping</c> своим
/// шаблоном (метка времени и счётчик), и правилу оно безразлично.
/// </para>
/// </remarks>
internal sealed class PingUtilityEcho : IIcmpEcho
{
    /// <summary>Где искать <c>ping</c>. Первый найденный и берётся.</summary>
    private static readonly string[] Candidates = ["/usr/bin/ping", "/bin/ping"];

    private readonly string _ping;

    internal PingUtilityEcho(string? path = null)
    {
        _ping = path ?? Candidates.FirstOrDefault(File.Exists) ?? Candidates[0];
    }

    public async Task SendAsync(string host, int payloadBytes, TimeSpan timeout)
    {
        using Process process = new() { StartInfo = StartInfo(_ping, host, payloadBytes, timeout) };

        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            // Нет файла или нет права на запуск — то же, что «нет прав на ICMP»
            // у Ping: отправить нечем, и стучать дальше незачем.
            throw new PingException($"{_ping}: {error.Message}", error);
        }

        // Сводку ping вычитываем и бросаем: не прочитанный до конца канал
        // однажды заполнится и остановит процесс на записи.
        _ = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();

        // Запас сверх тайм-аута самого ping: процесс ещё надо запустить и
        // разобрать адрес. Без него зависший DNS держал бы стук бесконечно.
        using CancellationTokenSource deadline = new(timeout + TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new PingException($"ping {host}: не уложился в {timeout.TotalSeconds:0.#} с");
        }

        // 0 — ответ пришёл, 1 — не пришёл: пакет в обоих случаях ушёл, и
        // стуку этого достаточно. 2 и выше — ping не отправил ничего: не
        // разрешилось имя, нет маршрута, неверный ключ.
        if (process.ExitCode >= 2)
        {
            var text = (await errors.ConfigureAwait(false)).Trim();
            throw new PingException(text.Length > 0 ? text : $"ping {host}: код {process.ExitCode}");
        }
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Ключи запуска. Отдельно — чтобы проверки видели их без запуска процесса.
    /// </summary>
    ///
    /// <remarks>
    /// <c>-n</c> — без обратного разбора адреса: он только тратит время.
    /// <c>-W</c> принимает доли секунды с iputils 20190709. <c>--</c> перед
    /// адресом: адрес приходит из настроек и предустановок, и строка вида
    /// <c>-f</c> не должна стать ключом. Язык — <c>C</c>: текст ошибки уходит
    /// в журнал как есть, а разбирать его на языке машины никто не будет.
    /// </remarks>
    internal static ProcessStartInfo StartInfo(string ping, string host, int payloadBytes, TimeSpan timeout)
    {
        ProcessStartInfo info = new(ping)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("1");
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add("-q");
        info.ArgumentList.Add("-W");
        info.ArgumentList.Add(Math.Max(0.1, timeout.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add(Math.Max(0, payloadBytes).ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(host);

        info.Environment["LC_ALL"] = "C";

        return info;
    }
}
