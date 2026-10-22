using EliteSIP.Updater;

namespace EliteSIP.Updater.Linux.Tests;

/// <summary>
/// Чьи сеансы получат софтфон после установки — по ответу
/// <c>loginctl show-session</c>, снятому на Mint 22.3.
/// </summary>
public sealed class SessionsTests
{
    [Theory]
    [InlineData("Name=koreva\nRemote=no\nType=x11\nClass=user\n", true)]
    [InlineData("Name=koreva\nRemote=no\nType=wayland\nClass=user\n", true)]
    [InlineData("Name=koreva\nRemote=yes\nType=tty\nClass=user\n", false)]
    [InlineData("Name=koreva\nRemote=no\nType=tty\nClass=user\n", false)]
    [InlineData("Name=lightdm\nRemote=no\nType=x11\nClass=greeter\n", false)]
    [InlineData("Remote=no\nType=x11\nClass=user\n", false)]
    public void Только_местный_графический_сеанс_человека(string output, bool expected)
    {
        Assert.Equal(expected, SystemdMachine.IsGraphicalUser(SystemdMachine.ParseProperties(output)));
    }

    [Fact]
    public void Живой_logind_отвечает()
    {
        // На машине разработки — сам разработчик; на CI сеансов нет вовсе.
        // Главное — разбор вывода живого loginctl не падает.
        var users = SystemdMachine.GraphicalUsers();

        Assert.All(users, user => Assert.False(string.IsNullOrWhiteSpace(user)));
    }
}
