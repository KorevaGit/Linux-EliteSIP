using System.Net.NetworkInformation;

namespace EliteSIP.Platform.Linux.Tests;

/// <summary>Стук через системный ping.</summary>
public sealed class PingUtilityEchoTests
{
    [Fact]
    public void Длина_данных_уходит_ключом_s_и_адрес_не_становится_ключом()
    {
        var info = PingUtilityEcho.StartInfo("/usr/bin/ping", "-f", 228, TimeSpan.FromSeconds(1));

        Assert.Equal(["-c", "1", "-n", "-q", "-W", "1", "-s", "228", "--", "-f"], info.ArgumentList);
        Assert.Equal("C", info.Environment["LC_ALL"]);
    }

    [Fact]
    public void Доли_секунды_в_ожидании_пишутся_точкой()
    {
        var info = PingUtilityEcho.StartInfo("/usr/bin/ping", "h", 1, TimeSpan.FromMilliseconds(250));

        Assert.Equal("0.25", info.ArgumentList[5]);
    }

    /// <summary>
    /// Настоящий ping на петлю: тот самый путь, на котором <c>Ping</c> .NET
    /// бросал <c>PlatformNotSupportedException</c>.
    /// </summary>
    [Fact]
    public async Task Пакет_заданной_длины_уходит_без_прав()
    {
        using PingUtilityEcho echo = new();

        await echo.SendAsync("127.0.0.1", 228, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Неразрешимое_имя_это_PingException_с_текстом_ping()
    {
        using PingUtilityEcho echo = new();

        var error = await Assert.ThrowsAsync<PingException>(
            () => echo.SendAsync("elitesip.invalid", 228, TimeSpan.FromSeconds(1)));

        Assert.Contains("elitesip.invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Нет_ping_это_PingException()
    {
        using PingUtilityEcho echo = new("/nonexistent/ping");

        await Assert.ThrowsAsync<PingException>(() => echo.SendAsync("127.0.0.1", 1, TimeSpan.FromSeconds(1)));
    }
}
