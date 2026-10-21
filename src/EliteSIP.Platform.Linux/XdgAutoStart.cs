using EliteSIP.App.Platform;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Автозапуск при входе — по спецификации XDG Autostart.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Включён по умолчанию у всех.</b> Пакет кладёт
/// <c>/etc/xdg/autostart/elitesip.desktop</c>: софтфон поднимается у оператора
/// на первом же входе, до того как его кто-то запустил руками (W-L7). На
/// Windows то же давала запись в <c>HKCU\…\Run</c>, которую ставил установщик.
/// </para>
/// <para>
/// <b>Выключение — для одного пользователя.</b> Файл с тем же именем в
/// <c>~/.config/autostart</c> перекрывает системный, и <c>Hidden=true</c> в нём
/// по спецификации значит «этой записи нет». Так XDG и задумывал: системный
/// файл администратора не трогается, а переключатель в «Обслуживании» не
/// требует прав. Включение обратно — удаление своей копии.
/// </para>
/// <para>
/// <b>Правда живёт в файлах, а не в наших настройках</b> — как на Windows она
/// жила в реестре. В Cinnamon автозапуск выключают и в «Автоматически
/// запускаемых приложениях»: тот пишет в ту же копию
/// <c>X-GNOME-Autostart-enabled=false</c>. Это тоже читается как «выключен».
/// </para>
/// </remarks>
internal sealed class XdgAutoStart : IAutoStart
{
    internal const string FileName = "elitesip.desktop";

    private readonly string _userFile;
    private readonly IReadOnlyList<string> _systemFiles;
    private readonly string _executable;

    /// <param name="configHome">вместо <c>$XDG_CONFIG_HOME</c>; для проверок.</param>
    /// <param name="configDirs">вместо <c>$XDG_CONFIG_DIRS</c>; для проверок.</param>
    /// <param name="executable">что запускать, если системного файла нет.</param>
    internal XdgAutoStart(string? configHome = null, IReadOnlyList<string>? configDirs = null, string? executable = null)
    {
        configHome ??= XdgConfigHome();
        configDirs ??= XdgConfigDirs();

        _userFile = Path.Combine(configHome, "autostart", FileName);
        _systemFiles = [.. configDirs.Select(directory => Path.Combine(directory, "autostart", FileName))];
        _executable = executable ?? Environment.ProcessPath ?? "elitesip";
    }

    public bool IsEnabled
    {
        get
        {
            // Своя копия перекрывает системную целиком — и в ту, и в другую
            // сторону: и «выключено», и «включено, хотя администратор не ставил».
            if (DesktopEntry.TryRead(_userFile) is { } user)
            {
                return user.IsActive;
            }

            return SystemEntry() is { IsActive: true };
        }
    }

    public bool TrySet(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (SystemEntry() is { IsActive: true })
                {
                    // Системная запись всё сделает сама: своя копия только
                    // отрезала бы её будущие правки из пакета.
                    File.Delete(_userFile);
                }
                else
                {
                    // Пакет не ставили (запуск из сборки) или администратор
                    // убрал системный файл — автозапуск заводится свой.
                    Write(enabled: true);
                }
            }
            else if (SystemEntry() is { IsActive: true })
            {
                Write(enabled: false);
            }
            else
            {
                // Системной записи нет — выключать нечего, кроме своей.
                File.Delete(_userFile);
            }

            return IsEnabled == enabled;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private DesktopEntry? SystemEntry()
    {
        // Первый найденный по порядку $XDG_CONFIG_DIRS и есть действующий.
        foreach (var file in _systemFiles)
        {
            if (DesktopEntry.TryRead(file) is { } entry)
            {
                return entry;
            }
        }

        return null;
    }

    private void Write(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_userFile)!);

        // Пишется через временный файл: сеанс читает каталог при входе, и
        // наполовину записанная запись в этот момент — это либо софтфон,
        // который не поднялся, либо «Hidden» без конца строки.
        var temporary = _userFile + ".tmp";
        File.WriteAllText(temporary, enabled
            ? $"""
              [Desktop Entry]
              Type=Application
              Name=EliteSIP
              Exec={DesktopEntry.QuoteExec(_executable)} --autostart
              Icon=elitesip
              Terminal=false
              X-GNOME-Autostart-enabled=true

              """
            : """
              [Desktop Entry]
              Type=Application
              Name=EliteSIP
              Hidden=true

              """);
        File.Move(temporary, _userFile, overwrite: true);
    }

    private static string XdgConfigHome()
    {
        var value = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrEmpty(value)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : value;
    }

    private static string[] XdgConfigDirs()
    {
        var value = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
        return string.IsNullOrEmpty(value)
            ? ["/etc/xdg"]
            : value.Split(':', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>Ровно то из файла <c>.desktop</c>, что решает, запускается ли он.</summary>
internal sealed record DesktopEntry(bool Hidden, bool GnomeEnabled)
{
    internal bool IsActive => !Hidden && GnomeEnabled;

    internal static DesktopEntry? TryRead(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Parse(lines);
    }

    /// <remarks>
    /// Только группа <c>[Desktop Entry]</c>: в группах действий (<c>[Desktop
    /// Action …]</c>) бывают свои ключи, и <c>Hidden</c> оттуда к записи
    /// отношения не имеет.
    /// </remarks>
    internal static DesktopEntry Parse(IEnumerable<string> lines)
    {
        var inEntry = false;
        var hidden = false;
        var gnomeEnabled = true;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }

            if (!inEntry)
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();

            if (key == "Hidden")
            {
                hidden = value == "true";
            }
            else if (key == "X-GNOME-Autostart-enabled")
            {
                gnomeEnabled = value != "false";
            }
        }

        return new DesktopEntry(hidden, gnomeEnabled);
    }

    /// <summary>
    /// Путь программы для ключа <c>Exec</c>.
    /// </summary>
    ///
    /// <remarks>
    /// В кавычки — если в пути пробел или знак, который спецификация считает
    /// особым; внутри кавычек экранируются <c>"</c>, <c>`</c>, <c>$</c> и
    /// <c>\</c>. Каталог разработки «Linux EliteSip» с пробелом — ровно такой
    /// путь.
    /// </remarks>
    internal static string QuoteExec(string path)
    {
        if (path.IndexOfAny([' ', '\t', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`']) < 0)
        {
            return path;
        }

        var escaped = path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal);

        // В файле .desktop обратная косая сама экранируется ещё раз: строка
        // сперва разбирается как значение ключа, потом как командная строка.
        return "\"" + escaped.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
    }
}
