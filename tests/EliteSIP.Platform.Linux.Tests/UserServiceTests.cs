namespace EliteSIP.Platform.Linux.Tests;

/// <summary>
/// Передача запуска службе — то, что можно проверить без systemd.
/// </summary>
///
/// <remarks>
/// Перезапуск после падения и отказ от него на коде 1 проверены на живом
/// менеджере пользователя временными службами (<c>systemd-run --user</c>,
/// стенд ShellProbe, режимы <c>crash</c> и <c>exit</c>): на CI менеджера
/// пользователя нет.
/// </remarks>
public sealed class UserServiceTests
{
    [Fact]
    public void Без_установленной_службы_работаем_здесь()
    {
        List<string> log = [];

        Assert.False(UserService.TryHandOver(log.Add, "/nonexistent/elitesip.service"));
        Assert.Empty(log);
    }

    [Fact]
    public void Процесс_проверки_не_служба()
    {
        Assert.False(UserService.IsCurrentProcess);
    }
}
