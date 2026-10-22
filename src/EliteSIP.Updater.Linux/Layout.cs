namespace EliteSIP.Updater;

/// <summary>Куда обновляльщик смотрит и куда пишет.</summary>
///
/// <remarks>
/// <para>
/// Два каталога, и разница между ними — вся защита здесь, как и на Windows
/// (<c>Paths</c> в <c>shared/tools/Updater</c>).
/// </para>
/// <para>
/// <b>Общий</b> (<c>/var/lib/elitesip/updates</c>, <c>1777</c>, как
/// <c>/tmp</c>) открыт на запись любому оператору — чтобы не добавлять его в
/// группы: это забудут, и линия на машине молча умрёт. Сюда приложение
/// качает выпуск и кладёт отметки «согласен» и «запустилось». Всё, что лежит
/// здесь, — непроверенное, и <b>root здесь ничего не пишет и не удаляет</b>:
/// аналог атаки через junction из аудита 1 октября на Linux — символическая
/// ссылка. Читается отсюда только через <see cref="SharedFile"/>: без
/// следования ссылкам, и сперва к себе.
/// </para>
/// <para>
/// <b>Свой</b> (<c>/var/lib/elitesip/updater</c>, <c>0700</c>, root) оператору
/// недоступен. Его родитель <c>/var/lib/elitesip</c> — root и <c>0755</c>, так
/// что подменить сам каталог оператор тоже не может. Сюда переносится выпуск
/// перед установкой, здесь журнал и всё, что обновляльщик помнит между тактами.
/// </para>
/// <para>
/// Ключ, которым проверяется подпись, — в заводской настройке рядом с
/// программой, в <c>/opt/elitesip</c>: туда пишет только пакет.
/// </para>
/// </remarks>
internal sealed record Layout(string Program, string Shared, string Trusted, string LockFile)
{
    internal static Layout Installed { get; } = new(
        AppContext.BaseDirectory,
        "/var/lib/elitesip/updates",
        "/var/lib/elitesip/updater",
        "/run/elitesip-updater/lock");

    /// <summary>Сборка приложения: по её версии видно, какой выпуск стоит.</summary>
    internal string AppAssembly => Path.Combine(Program, "EliteSIP.App.Linux.dll");

    /// <summary>Отметка «оператор согласился». Внутри — версия. Только читается.</summary>
    internal string Request => Path.Combine(Shared, "update-requested");

    /// <summary>
    /// Отметка приложения «запустилось и прожило минуту». Внутри — версия.
    /// Только читается; подделав её, оператор лишь выключит аварийный путь
    /// своей машины.
    /// </summary>
    internal string Started => Path.Combine(Shared, "started");

    /// <summary>Копии отметок из общего каталога — читаются только они.</summary>
    internal string Inbox => Path.Combine(Trusted, "inbox");

    internal string Log => Path.Combine(Trusted, "updater.log");

    /// <summary>Какая отметка о согласии уже обработана. Вместо удаления самой отметки.</summary>
    internal string RequestHandled => Path.Combine(Trusted, "request-handled");

    /// <summary>Отметка «поднять софтфон после установки». Внутри — версия.</summary>
    internal string RelaunchMarker => Path.Combine(Trusted, "relaunch-pending");

    /// <summary>Когда обновляльщик впервые увидел установленный выпуск. См. <see cref="Recovery"/>.</summary>
    internal string InstalledSeen => Path.Combine(Trusted, "installed-seen");

    /// <summary>О каком выпуске уже сказано, что он не запускается.</summary>
    internal string RecoveryNoted => Path.Combine(Trusted, "recovery-noted");
}
