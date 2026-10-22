using EliteSIP.App.Platform;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Передача выпуска обновляльщику через каталог обмена
/// <c>/var/lib/elitesip/updates</c> — Linux-половина <c>UpdateHandoff</c>
/// Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// Ставит выпуск <c>elitesip-updater.service</c> от root (W-L7). Отсюда уходят
/// три вещи, все — файлами в каталоге обмена: скачанный пакет (его пишет
/// общий <c>UpdateService</c>), отметка о согласии <c>update-requested</c> и
/// отметка «выпуск жив» <c>started</c>. Будить обновляльщика отдельно не
/// нужно: на отметку о согласии смотрит <c>elitesip-updater.path</c>, и
/// служба поднимается, как только файл записан.
/// </para>
/// <para>
/// <b>Каталог — <c>1777</c>, как <c>/tmp</c></b>, чтобы оператора не надо было
/// добавлять в группы. Отсюда две ловушки, и обе — ядро, а не мы.
/// <c>fs.protected_regular = 2</c> (Mint 22) запрещает открывать с
/// <c>O_CREAT</c> чужой файл в таком каталоге, даже открытый на запись всем;
/// бит sticky запрещает чужой файл удалить или заменить. Поэтому отметка,
/// которая уже есть, пишется поверх открытием без <c>O_CREAT</c>, а новая
/// создаётся с правами <c>0666</c> — иначе второй оператор той же машины не
/// смог бы дать согласие после первого. Поддельной отметкой ничего не
/// добиться: обновляльщик ставит только подписанный выпуск.
/// </para>
/// </remarks>
internal sealed class UpdateExchange(string directory = UpdateExchange.InstalledDirectory, string pathUnit = UpdateExchange.InstalledPathUnit) : IUpdateHandoff
{
    internal const string InstalledDirectory = "/var/lib/elitesip/updates";

    /// <summary>Где пакет кладёт путь, который будит обновляльщика.</summary>
    internal const string InstalledPathUnit = "/usr/lib/systemd/system/elitesip-updater.path";

    private const UnixFileMode Everyone =
        UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

    public string UpdatesDirectory => directory;

    private string RequestPath => Path.Combine(directory, "update-requested");

    private string StartedPath => Path.Combine(directory, "started");

    /// <summary>
    /// Оставляет отметку «оператор согласился на эту версию». Версией, а не
    /// пустым файлом: обновляльщик ставит ровно то, на что согласились.
    /// </summary>
    public bool Request(Version version, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(log);

        if (!Directory.Exists(directory))
        {
            // Каталог создаёт пакет. Нет его — программа поставлена не пакетом
            // (запуск из сборки), и ставить обновление здесь некому.
            log($"обновление не передано: нет каталога обмена {directory} — программа поставлена не пакетом");
            return false;
        }

        if (!TryWrite(RequestPath, version.ToString(3), out var error))
        {
            log($"обновление не передано: {error}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Будить обновляльщика не нужно — его будит запись отметки. Отвечает,
    /// есть ли кому проснуться.
    /// </summary>
    ///
    /// <remarks>
    /// От ответа зависит, уходить ли приложению с дороги (<c>PrepareForRestart</c>):
    /// ушедший софтфон поднимает обновляльщик после установки — удачной или
    /// нет. Если пути нет, обновляльщик всё равно проснётся по таймеру в течение
    /// десяти минут, но уходить заранее не стоит: оператор сидел бы без
    /// телефона неизвестно почему.
    /// </remarks>
    public bool Trigger(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (File.Exists(pathUnit))
        {
            return true;
        }

        log($"обновляльщик сразу не проснётся: нет {pathUnit} — подождём его такта");
        return false;
    }

    /// <summary>
    /// Отмечает, что этот выпуск запустился и прожил минуту. По этой отметке
    /// обновляльщик отличает выпуск, который не запускается вовсе, и ставит
    /// исправление без согласия (<c>Recovery</c>). Отметка одна на машину:
    /// её смысл — «этот выпуск жив», а не «жив у этого оператора».
    /// </summary>
    public void MarkStarted(Version version, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(log);

        if (Directory.Exists(directory) && !TryWrite(StartedPath, version.ToString(3), out var error))
        {
            // Не беда для работы, но обновляльщик сочтёт выпуск не
            // запускавшимся и, пока софтфон закрыт, поставит следующий без
            // вопроса.
            log($"отметка о запуске не записана: {error}");
        }
    }

    /// <summary>Пишет отметку так, чтобы её мог переписать и другой оператор.</summary>
    internal static bool TryWrite(string path, string text, out string error)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        try
        {
            FileStream stream;
            try
            {
                // Есть — поверх, без O_CREAT: см. описание класса.
                stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                });
            }
            catch (FileNotFoundException)
            {
                stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    UnixCreateMode = Everyone,
                });

                // umask срезал бы запись группе и остальным.
                File.SetUnixFileMode(path, Everyone);
            }

            using (stream)
            {
                stream.SetLength(0);
                stream.Write(bytes);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error = failure.Message;
            return false;
        }
    }
}
