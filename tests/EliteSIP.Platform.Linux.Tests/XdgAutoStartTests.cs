namespace EliteSIP.Platform.Linux.Tests;

/// <summary>Автозапуск: системная запись пакета и своя копия пользователя.</summary>
public sealed class XdgAutoStartTests
{
    private const string SystemEntry = """
        [Desktop Entry]
        Type=Application
        Name=EliteSIP
        Exec=/opt/elitesip/EliteSIP --autostart
        """;

    [Fact]
    public void Системная_запись_пакета_значит_включён()
    {
        using TemporaryDirectory root = new();
        var autoStart = Make(root, withSystemEntry: true);

        Assert.True(autoStart.IsEnabled);
    }

    [Fact]
    public void Выключение_пишет_свою_копию_с_Hidden_и_не_трогает_системную()
    {
        using TemporaryDirectory root = new();
        var autoStart = Make(root, withSystemEntry: true);

        Assert.True(autoStart.TrySet(false));

        Assert.False(autoStart.IsEnabled);
        Assert.Contains("Hidden=true", File.ReadAllText(UserFile(root)), StringComparison.Ordinal);
        Assert.Equal(SystemEntry, File.ReadAllText(SystemFile(root)));
    }

    [Fact]
    public void Включение_обратно_убирает_свою_копию()
    {
        using TemporaryDirectory root = new();
        var autoStart = Make(root, withSystemEntry: true);
        autoStart.TrySet(false);

        Assert.True(autoStart.TrySet(true));

        Assert.True(autoStart.IsEnabled);
        Assert.False(File.Exists(UserFile(root)));
    }

    /// <summary>
    /// Без пакета (запуск из сборки) автозапуск заводится своей записью с
    /// путём этой программы — путь с пробелом в кавычках.
    /// </summary>
    [Fact]
    public void Без_пакета_включение_заводит_свою_запись()
    {
        using TemporaryDirectory root = new();
        var autoStart = Make(root, withSystemEntry: false, executable: "/media/dev/Linux EliteSip/EliteSIP");

        Assert.False(autoStart.IsEnabled);
        Assert.True(autoStart.TrySet(true));

        Assert.True(autoStart.IsEnabled);
        Assert.Contains(
            "Exec=\"/media/dev/Linux EliteSip/EliteSIP\" --autostart",
            File.ReadAllText(UserFile(root)),
            StringComparison.Ordinal);

        Assert.True(autoStart.TrySet(false));
        Assert.False(File.Exists(UserFile(root)));
    }

    /// <summary>
    /// «Автоматически запускаемые приложения» Cinnamon выключают запись своим
    /// ключом — и переключатель должен показать то же, что и они.
    /// </summary>
    [Fact]
    public void Выключенный_в_Cinnamon_читается_выключенным()
    {
        using TemporaryDirectory root = new();
        var autoStart = Make(root, withSystemEntry: true);
        Directory.CreateDirectory(Path.GetDirectoryName(UserFile(root))!);
        File.WriteAllText(UserFile(root), SystemEntry + "\nX-GNOME-Autostart-enabled=false\n");

        Assert.False(autoStart.IsEnabled);
    }

    [Fact]
    public void Hidden_из_группы_действия_к_записи_не_относится()
    {
        var entry = DesktopEntry.Parse(
        [
            "[Desktop Entry]",
            "Name=EliteSIP",
            "[Desktop Action Quit]",
            "Hidden=true",
        ]);

        Assert.True(entry.IsActive);
    }

    [Theory]
    [InlineData("/opt/elitesip/EliteSIP", "/opt/elitesip/EliteSIP")]
    [InlineData("/home/a b/EliteSIP", "\"/home/a b/EliteSIP\"")]
    [InlineData("/home/$x/EliteSIP", "\"/home/\\\\$x/EliteSIP\"")]
    public void Путь_в_Exec_экранируется_по_спецификации(string path, string expected)
    {
        Assert.Equal(expected, DesktopEntry.QuoteExec(path));
    }

    private static XdgAutoStart Make(TemporaryDirectory root, bool withSystemEntry, string executable = "/opt/elitesip/EliteSIP")
    {
        if (withSystemEntry)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SystemFile(root))!);
            File.WriteAllText(SystemFile(root), SystemEntry);
        }

        return new XdgAutoStart(root.Combine("home"), [root.Combine("etc")], executable);
    }

    private static string UserFile(TemporaryDirectory root) => root.Combine("home", "autostart", XdgAutoStart.FileName);

    private static string SystemFile(TemporaryDirectory root) => root.Combine("etc", "autostart", XdgAutoStart.FileName);
}
