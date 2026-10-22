using Tmds.DBus.Protocol;

namespace EliteSIP.Platform.Linux.Tests;

/// <summary>Разбор ответов портала о теме.</summary>
public sealed class DesktopAppearanceTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    public void Тёмная_только_по_явной_просьбе(uint scheme, bool dark)
    {
        Assert.Equal(dark, DesktopAppearance.IsDark(VariantValue.UInt32(scheme)));
    }

    [Fact]
    public void Нет_настройки_значит_светлая()
    {
        Assert.False(DesktopAppearance.IsDark(null));
    }

    /// <summary>Значение, которое отдал xapp-портал Cinnamon 21 октября 2026.</summary>
    [Fact]
    public void Акцент_из_долей_в_байты()
    {
        var accent = DesktopAppearance.ToAccent(VariantValue.Struct(
            VariantValue.Double(0.121569), VariantValue.Double(0.619608), VariantValue.Double(0.870588)));

        Assert.Equal(new AccentColor(31, 158, 222), accent);
    }

    [Fact]
    public void Доли_вне_диапазона_значат_акцент_не_задан()
    {
        Assert.Null(DesktopAppearance.ToAccent(VariantValue.Struct(
            VariantValue.Double(-1), VariantValue.Double(-1), VariantValue.Double(-1))));
    }

    [Fact]
    public async Task Молчащий_портал_не_держит_запуск()
    {
        // Автозапуск на входе в сеанс опередил портал, или портал завис:
        // запуск ждёт предел, а не 25 с D-Bus.
        var never = new TaskCompletionSource();
        var log = new List<string>();
        var started = System.Diagnostics.Stopwatch.StartNew();

        await DesktopAppearance.StartAsync(() => never.Task, TimeSpan.FromMilliseconds(50), () => { }, log.Add);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains(log, line => line.Contains("не ответил", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Опоздавший_ответ_перекрашивает()
    {
        var answer = new TaskCompletionSource();
        var late = new TaskCompletionSource();
        var log = new List<string>();

        await DesktopAppearance.StartAsync(() => answer.Task, TimeSpan.FromMilliseconds(20), () => late.TrySetResult(), log.Add);
        Assert.False(late.Task.IsCompleted);

        answer.SetResult();

        await late.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Ответ_в_срок_без_опоздания()
    {
        var lateCalls = 0;
        var log = new List<string>();

        await DesktopAppearance.StartAsync(() => Task.CompletedTask, TimeSpan.FromSeconds(5), () => lateCalls++, log.Add);

        Assert.Equal(0, lateCalls);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Неожиданное_исключение_не_роняет_запуск()
    {
        // Поток окна ждёт этот метод на запуске: исключение отсюда — софтфон,
        // который не открылся.
        var log = new List<string>();

        await DesktopAppearance.StartAsync(
            () => throw new InvalidOperationException("нет адреса сеансовой шины"),
            TimeSpan.FromSeconds(5),
            () => { },
            log.Add);

        Assert.Contains(log, line => line.Contains("нет адреса сеансовой шины", StringComparison.Ordinal));
    }
}
