namespace EliteSIP.Platform.Linux.Tests;

/// <summary>X11 или Wayland — по окружению сеанса.</summary>
public sealed class GraphicsSessionTests
{
    [Fact]
    public void Cinnamon_на_X11()
    {
        Assert.Equal(GraphicsSessionKind.X11, Detect(("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":0")));
    }

    [Fact]
    public void Wayland_по_типу_сеанса_даже_с_DISPLAY_от_XWayland()
    {
        Assert.Equal(GraphicsSessionKind.Wayland, Detect(("XDG_SESSION_TYPE", "wayland"), ("DISPLAY", ":0")));
    }

    [Fact]
    public void Wayland_в_службе_без_типа_сеанса()
    {
        // Пользовательской службе systemd GNOME и KDE передают WAYLAND_DISPLAY,
        // а XDG_SESSION_TYPE — не всегда.
        Assert.Equal(GraphicsSessionKind.Wayland, Detect(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")));
    }

    [Fact]
    public void Служба_без_типа_сеанса_но_с_DISPLAY_это_X11()
    {
        Assert.Equal(GraphicsSessionKind.X11, Detect(("DISPLAY", ":0")));
    }

    [Fact]
    public void Ничего_нет_неизвестно()
    {
        Assert.Equal(GraphicsSessionKind.Unknown, Detect());
    }

    [Fact]
    public void Описание_Wayland_говорит_что_не_поддерживается()
    {
        var text = GraphicsSession.Describe(Environment(("WAYLAND_DISPLAY", "wayland-0"), ("XDG_CURRENT_DESKTOP", "GNOME")));

        Assert.Contains("НЕ ПОДДЕРЖИВАЕТСЯ", text, StringComparison.Ordinal);
        Assert.Contains("GNOME", text, StringComparison.Ordinal);
    }

    private static GraphicsSessionKind Detect(params (string Name, string Value)[] variables) =>
        GraphicsSession.Detect(Environment(variables));

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(variable => variable.Name, variable => (string?)variable.Value);
        return name => map.GetValueOrDefault(name);
    }
}
