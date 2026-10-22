namespace EliteSIP.Platform.Linux;

/// <summary>
/// Каталог профиля софтфона (<c>~/.local/share/EliteSIP</c>) закрыт от
/// других пользователей машины.
/// </summary>
///
/// <remarks>
/// <para>
/// Там лежат настройки с номером, адресом АТС и идентификатором машины,
/// <c>pairing.json</c>, журнал, история и ключ секретов. Общий код
/// (<c>AppSettings.Save</c>, <c>PairingStore.Save</c>) пишет файлы с правами
/// по <c>umask</c> — на Mint это <c>0664</c>, — а каталог создаёт тот, кто
/// успел первым: ранняя строка журнала до настроек, и тоже по <c>umask</c>.
/// <c>ProtectedSecret</c> просит для каталога <c>0700</c>, но существующему
/// каталогу <c>CreateDirectory</c> права не меняет.
/// </para>
/// <para>
/// <b>Защита — каталог, а не каждый файл.</b> Файл настроек пересоздаётся
/// переносом временного на каждую правку, и права на нём каждый раз снова
/// от <c>umask</c>. Каталог <c>0700</c> закрывает всё внутри разом, что бы
/// там ни создалось потом. Уже лежащие файлы сужаются до <c>0600</c> — на
/// случай, если каталог когда-нибудь откроют руками.
/// </para>
/// <para>
/// <b>Не <c>umask(077)</c> на весь процесс.</b> Его унаследовали бы
/// программы, которые софтфон запускает: <c>xdg-open</c> и поднятый им
/// файловый менеджер создавали бы у оператора закрытые файлы где угодно.
/// </para>
/// </remarks>
internal static class ProfilePrivacy
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Создаёт каталог <c>0700</c> или сужает существующий; файлы в нём — до
    /// <c>0600</c>. Возвращает, что пришлось сузить (для журнала); пусто —
    /// всё и так было закрыто. Не бросает: закрыть не вышло — софтфон всё
    /// равно запускается.
    /// </summary>
    internal static IReadOnlyList<string> Secure(string directory)
    {
        var narrowed = new List<string>();

        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory, OwnerDirectory);
            }

            // Ссылку вместо каталога не трогаем: права сменились бы у того,
            // на что она указывает.
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                return narrowed;
            }

            if (File.GetUnixFileMode(directory) != OwnerDirectory)
            {
                File.SetUnixFileMode(directory, OwnerDirectory);
                narrowed.Add(Path.GetFileName(directory) + "/");
            }

            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (file.LinkTarget is null && (file.UnixFileMode & ~OwnerFile) != 0)
                {
                    file.UnixFileMode = OwnerFile;
                    narrowed.Add(file.Name);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            narrowed.Add($"не закрылся ({error.Message})");
        }

        return narrowed;
    }
}
