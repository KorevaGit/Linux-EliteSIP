using System.Net;
using System.Security.Cryptography;
using EliteSIP.PanelLink;

namespace EliteSIP.Updater;

/// <summary>
/// Один такт обновляльщика: сценарий Windows-версии (<c>shared/tools/Updater</c>)
/// на Linux-путях.
/// </summary>
///
/// <remarks>
/// <para>
/// Порядок шагов и все правила — те же, и разбор каждого — у Windows-версии
/// и в <c>EliteSIP.Updates</c>. Здесь описано только то, что на Linux иначе.
/// </para>
/// <para>
/// <b>Кому здесь верят.</b> Ничему из общего каталога: ни отметке, ни
/// скачанному пакету. Подпись Ed25519 проверяется заново ключом из
/// <c>/opt/elitesip</c>, отпечаток пакета — по подписанному манифесту, и
/// ставится только копия в своём каталоге. Пакет <c>.deb</c> выполняет свой
/// <c>postinst</c> от root, поэтому ставится только файл, отпечаток которого
/// стоит в подписанном манифесте, — и только файл линии Linux
/// (<see cref="ReleaseLine.Holds"/>). Худшее, чего добьётся подделавший общий
/// каталог, — установка нашего же подписанного выпуска.
/// </para>
/// <para>
/// <b>Установка не закрывает софтфон.</b> На Windows установщик менял файлы,
/// которые держит запущенное приложение; <c>dpkg</c> заменяет файлы под
/// работающим процессом без помех. Приложение всё равно уходит само, как на
/// Windows, — оператор согласился на перезапуск, и разговор не должен
/// оборваться посреди установки, — а после установки софтфон поднимает
/// обновляльщик.
/// </para>
/// </remarks>
internal sealed class UpdaterRun(Layout layout, IMachine machine, HttpClient http, Action<string> log, Func<DateTime> utcNow)
{
    /// <summary>Больше версии в отметке быть не может.</summary>
    private const long MarkerBytes = 64;

    /// <summary>Потолок пакета: файл лежит в общем каталоге, и размер сверяется до чтения.</summary>
    private const long MaximumPackageBytes = 512L * 1024 * 1024;

    private static readonly ReleaseLine Line = ReleaseLine.Linux;

    private string RequestCopy => Path.Combine(layout.Inbox, "update-requested");

    private string StartedCopy => Path.Combine(layout.Inbox, "started");

    internal async Task<int> RunAsync()
    {
        RelaunchIfPending();

        // Отметки нет — обычное состояние, и молчать о нём надо тоже обычно.
        var consent = SharedFile.TryCopyIn(layout.Request, RequestCopy, MarkerBytes)
            ? Request.Read(RequestCopy)
            : null;
        var wanted = consent is { } fresh && !Request.IsHandled(fresh, layout.RequestHandled)
            ? fresh.Version
            : null;

        PruneUpdates(wanted);

        var stuck = IsInstalledReleaseStuck();

        Consent? pending = consent is { } fresher && wanted is not null ? fresher : null;
        var emergency = pending is null && stuck;
        if (pending is null && !emergency)
        {
            return 0;
        }

        // Установка, начатая прошлым тактом, ещё идёт. Согласие подождёт:
        // необработанным оно остаётся, и следующий такт его увидит.
        if (machine.IsInstalling())
        {
            return 0;
        }

        if (pending is not null)
        {
            log($"оператор согласился на {wanted}");
        }

        var secrets = Provisioning.Read(layout.Program);
        if (secrets?.Updates is null)
        {
            log("в заводской настройке рядом с программой нет канала — ставить нечего и неоткуда");
            return 1;
        }

        if (secrets.PublicKey() is not { } publicKey)
        {
            log("в заводской настройке нет открытого ключа линии — проверить подпись нечем");
            return 1;
        }

        var manifest = await FetchManifestAsync(secrets.Updates, publicKey).ConfigureAwait(false);
        if (manifest is null)
        {
            return 1;
        }

        if (pending is { } mismatched && manifest.Version != wanted)
        {
            log($"на канале {manifest.Version}, а согласие было на {wanted} — жду нового согласия");
            Request.MarkHandled(mismatched, layout.RequestHandled);
            return 0;
        }

        var installed = machine.InstalledVersion();
        if (installed is not null && !manifest.IsNewerThan(installed))
        {
            if (pending is { } current)
            {
                log($"уже стоит {installed} — ставить нечего");
                Request.MarkHandled(current, layout.RequestHandled);
            }

            return 0;
        }

        if (!Line.Holds(manifest))
        {
            log($"манифест {manifest.Version} указывает не на пакет Linux ({manifest.Url.AbsolutePath}) — не ставлю");
            return 1;
        }

        if (emergency)
        {
            log($"АВАРИЙНАЯ установка {manifest.Version} поверх {installed}: "
                + "установленный выпуск не запускается, согласия спросить не у кого");
        }

        var payload = await AcquireAsync(manifest, secrets.Updates).ConfigureAwait(false);
        if (payload is null)
        {
            return 1;
        }

        // Перенос в свой каталог — та граница, после которой файлу можно
        // доверять: писать сюда может только root.
        var trusted = Path.Combine(layout.Trusted, Line.FileName(manifest.Version));
        await File.WriteAllBytesAsync(trusted, payload).ConfigureAwait(false);

        // Отметка помечается обработанной до установки, а не после — иначе
        // сорвавшаяся установка повторялась бы по кругу каждые десять минут.
        if (pending is { } accepted)
        {
            Request.MarkHandled(accepted, layout.RequestHandled);
        }

        // Отметка «поднять после установки» — до установки: софтфон ушёл, и
        // вернуть его обязан кто-то, кто переживёт установку.
        await File.WriteAllTextAsync(layout.RelaunchMarker, manifest.Version.ToString(3)).ConfigureAwait(false);

        var installLog = Path.Combine(layout.Trusted, $"install-{manifest.Version.ToString(3)}.log");
        if (!machine.StartInstall(trusted, installLog, out var why))
        {
            log($"установка {manifest.Version} не началась ({why})");

            // Софтфон ушёл ради установки, которой не будет, — вернуть его
            // сейчас, а не через десять минут.
            RelaunchIfPending();
            return 1;
        }

        log($"выпуск {manifest.Version} передан службе установки");
        return 0;
    }

    /// <summary>Не запускается ли установленный выпуск — см. <see cref="Recovery"/>.</summary>
    private bool IsInstalledReleaseStuck()
    {
        if (machine.InstalledVersion() is not { } installed)
        {
            return false;
        }

        var now = utcNow();
        var firstSeen = Recovery.FirstSeen(installed, layout.InstalledSeen, now);
        var started = SharedFile.TryCopyIn(layout.Started, StartedCopy, MarkerBytes)
            ? Request.Read(StartedCopy)?.Version
            : null;

        if (!Recovery.IsStuck(installed, started, firstSeen, now, machine.IsAppRunning()))
        {
            return false;
        }

        if (Recovery.NoteOnce(installed, layout.RecoveryNoted))
        {
            log($"выпуск {installed.ToString(3)} стоит больше {Recovery.Grace.TotalMinutes:0} мин и ни разу "
                + "не запустился — следующий выпуск с канала встанет без согласия оператора");
        }

        return true;
    }

    /// <summary>Подъём софтфона после установки.</summary>
    ///
    /// <remarks>
    /// <para>
    /// Отметка ставится до установки и снимается только удачным подъёмом.
    /// Зовётся в начале каждого такта, а первый такт после установки
    /// случается сразу: служба установки будит обновляльщика, когда кончится.
    /// </para>
    /// <para>
    /// <b>Отличие от Windows.</b> Там при невставшем выпуске подъём ждал
    /// (<c>installed &lt; wanted</c> — «рано»), и оператор, ушедший с дороги
    /// установщику, сидел без софтфона до следующего входа. Здесь видно, что
    /// установка кончилась (<see cref="IMachine.IsInstalling"/>), — и если
    /// выпуск не встал, поднимается прежний: он рабочий, с ним оператор и жил.
    /// Новое согласие приложение спросит само, увидев, что версия не та.
    /// </para>
    /// <para>
    /// Сутки — предел, как на Windows: отметка от установки, которая так ничем
    /// и не кончилась, не должна поднимать софтфон через неделю.
    /// </para>
    /// </remarks>
    private void RelaunchIfPending()
    {
        if (!File.Exists(layout.RelaunchMarker))
        {
            return;
        }

        if (utcNow() - File.GetLastWriteTimeUtc(layout.RelaunchMarker) > TimeSpan.FromDays(1))
        {
            TryDelete(layout.RelaunchMarker);
            return;
        }

        if (machine.IsInstalling())
        {
            return;
        }

        var wanted = Version.TryParse(ReadText(layout.RelaunchMarker), out var version) ? version : null;
        var installed = machine.InstalledVersion();

        if (wanted is not null && (installed is null || installed.CompareTo(wanted) < 0))
        {
            log($"выпуск {wanted} не встал (стоит {installed?.ToString(3) ?? "неизвестно что"}, "
                + $"журнал — install-{wanted}.log) — поднимаю стоящий");
        }

        if (machine.RestartInSessions(out var why))
        {
            log($"выпуск {installed?.ToString(3)} стоит, софтфон поднят в сеансе оператора"
                + (why.Length > 0 ? $" (не везде: {why})" : string.Empty));
            TryDelete(layout.RelaunchMarker);
        }
        else
        {
            // Отметка остаётся: её подберёт следующий такт.
            log($"софтфон после установки не поднят ({why}) — попробую на следующем такте");
        }
    }

    /// <summary>
    /// Убирает пакеты, которые уже никому не нужны, — только в своём каталоге.
    /// Правила те же, что на Windows: стоящий выпуск и ожидаемый остаются,
    /// ничего моложе часа не трогается, журналов установки — три последних.
    /// </summary>
    private void PruneUpdates(Version? pending)
    {
        var installed = machine.InstalledVersion();

        string[] packages;
        try
        {
            packages = Directory.Exists(layout.Trusted)
                ? Directory.GetFiles(layout.Trusted, Line.FilePattern)
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var package in packages)
        {
            var version = Line.VersionOf(package);

            if (version is null
                || SameRelease(version, installed)
                || SameRelease(version, pending)
                || IsFresh(package))
            {
                continue;
            }

            try
            {
                File.Delete(package);
                log($"убран выпуск {version}: он больше не нужен");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Уберём на следующем такте.
            }
        }

        PruneInstallLogs();
    }

    private void PruneInstallLogs()
    {
        const int keep = 3;

        try
        {
            if (!Directory.Exists(layout.Trusted))
            {
                return;
            }

            var stale = new DirectoryInfo(layout.Trusted)
                .GetFiles("install-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(keep);

            foreach (var file in stale)
            {
                TryDelete(file.FullName);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Убирать нечего и нечем.
        }
    }

    private async Task<ReleaseManifest?> FetchManifestAsync(Provisioning.UpdateChannel channel, PanelPublicKey publicKey)
    {
        if (channel.ReleasesUrl() is not { } url)
        {
            log("адрес манифеста не сложился");
            return null;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", channel.BasicHeader());

            using var response = await http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                log($"манифест не отдан, канал ответил {(int)response.StatusCode}");
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            return ReleaseManifest.Verified(data, publicKey);
        }
        catch (PanelLinkException error)
        {
            // Подпись не сошлась — это не «канал шалит», это подменённый манифест.
            log($"манифест ОТБРОШЕН: {error.Message}");
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            log($"канал недоступен: {error.Message}");
            return null;
        }
    }

    /// <summary>Берёт пакет: сперва скачанный приложением, иначе качает сам.</summary>
    private async Task<byte[]?> AcquireAsync(ReleaseManifest manifest, Provisioning.UpdateChannel channel)
    {
        var staged = Path.Combine(layout.Shared, Line.FileName(manifest.Version));
        var limit = manifest.Size > 0 ? Math.Min(manifest.Size, MaximumPackageBytes) : MaximumPackageBytes;

        if (SharedFile.TryRead(staged, limit, out var bytes, out _))
        {
            if (Matches(bytes, manifest.Sha256))
            {
                log("взят выпуск, скачанный приложением");
                return bytes;
            }

            // То ли докачан не до конца, то ли подменён. Разбирать незачем.
            log("скачанное приложением не сошлось по отпечатку — качаю сам");
        }
        else if (File.Exists(staged) || Directory.Exists(staged))
        {
            log("скачанное приложением не прочиталось (не файл, ссылка или не тот размер) — качаю сам");
        }

        // Пару Basic — только хосту манифеста (разбор у `ReleaseManifest.IsServedFrom`).
        if (channel.ReleasesUrl() is not { } manifestAddress || !manifest.IsServedFrom(manifestAddress))
        {
            log($"выпуск {manifest.Version} лежит на другом хосте ({manifest.Url.Authority}), "
                + "чем манифест — не качаю");
            return null;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, manifest.Url);
            request.Headers.TryAddWithoutValidation("Authorization", channel.BasicHeader());

            using var response = await http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                log($"выпуск не отдан, канал ответил {(int)response.StatusCode}");
                return null;
            }

            var downloaded = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (!Matches(downloaded, manifest.Sha256))
            {
                log("выпуск ОТБРОШЕН: отпечаток не сошёлся");
                return null;
            }

            return downloaded;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            log($"выпуск не скачался: {error.Message}");
            return null;
        }
    }

    private static bool Matches(byte[] bytes, string expected)
        => string.Equals(
            Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Один ли это выпуск — по трём числам: у файла версия трёхчастная, у сборки — четырёхчастная.</summary>
    private static bool SameRelease(Version? left, Version? right) =>
        left is not null && right is not null
            && left.Major == right.Major
            && left.Minor == right.Minor
            && left.Build == right.Build;

    private bool IsFresh(string path)
    {
        try
        {
            return utcNow() - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Не снялась — снимет следующий такт.
        }
    }
}
