namespace EliteSIP.Platform.Linux.Tests;

/// <summary>
/// Каталог обмена с обновляльщиком: отметки, которые потом читает root.
/// </summary>
public sealed class UpdateExchangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EliteSIP.UpdateExchange.Tests", Guid.NewGuid().ToString("N"));

    public UpdateExchangeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string PathUnit => Path.Combine(_root, "elitesip-updater.path");

    [Fact]
    public void Согласие_пишется_версией_и_открыто_всем()
    {
        var exchange = new UpdateExchange(_root, PathUnit);

        Assert.True(exchange.Request(new Version(0, 1, 3, 0), _ => { }));

        var path = Path.Combine(_root, "update-requested");
        Assert.Equal("0.1.3", File.ReadAllText(path));

        // Второй оператор той же машины должен суметь переписать отметку:
        // удалить или заменить чужой файл в каталоге 1777 он не может.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite,
            File.GetUnixFileMode(path));
    }

    [Fact]
    public void Повторное_согласие_переписывает_отметку_и_её_время()
    {
        var exchange = new UpdateExchange(_root, PathUnit);
        var path = Path.Combine(_root, "update-requested");

        exchange.Request(new Version(0, 1, 30), _ => { });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        var before = File.GetLastWriteTimeUtc(path);

        Assert.True(exchange.Request(new Version(0, 1, 4), _ => { }));

        // Короче прежней — без хвоста от прежней записи.
        Assert.Equal("0.1.4", File.ReadAllText(path));
        Assert.True(File.GetLastWriteTimeUtc(path) > before);
    }

    [Fact]
    public void Без_каталога_пакета_согласие_не_передаётся()
    {
        var lines = new List<string>();
        var exchange = new UpdateExchange(Path.Combine(_root, "нет"), PathUnit);

        Assert.False(exchange.Request(new Version(0, 1, 3), lines.Add));
        Assert.Contains("поставлена не пакетом", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void С_дороги_уходим_только_если_есть_путь_обновляльщика()
    {
        var exchange = new UpdateExchange(_root, PathUnit);

        Assert.False(exchange.Trigger(_ => { }));

        File.WriteAllText(PathUnit, "[Path]");
        Assert.True(exchange.Trigger(_ => { }));
    }

    [Fact]
    public void Отметка_запуска_пишется_тремя_числами()
    {
        new UpdateExchange(_root, PathUnit).MarkStarted(new Version(0, 1, 3, 0), _ => { });

        Assert.Equal("0.1.3", File.ReadAllText(Path.Combine(_root, "started")));
    }
}
