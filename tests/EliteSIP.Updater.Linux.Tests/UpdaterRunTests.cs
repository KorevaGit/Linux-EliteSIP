namespace EliteSIP.Updater.Linux.Tests;

/// <summary>
/// Такт обновляльщика: когда ставить, что ставить и чего не трогать.
/// </summary>
public sealed class UpdaterRunTests : IDisposable
{
    private readonly Stand _stand = new();

    /// <summary>Срок аварийного пути — <c>Recovery.Grace</c> общего кода.</summary>
    private static readonly TimeSpan RecoveryGrace = TimeSpan.FromHours(1);

    public void Dispose() => _stand.Dispose();

    [Fact]
    public async Task Без_согласия_в_канал_не_ходит()
    {
        _stand.Publish("0.1.1");

        Assert.Equal(0, await _stand.RunAsync());

        Assert.Empty(_stand.Server.Requested);
        Assert.Empty(_stand.Machine.Installs);
    }

    [Fact]
    public async Task Согласие_ставит_скачанное_приложением_через_свою_копию()
    {
        var package = _stand.Publish("0.1.1");
        File.WriteAllBytes(Path.Combine(_stand.Layout.Shared, "EliteSIP-0.1.1.deb"), package);
        _stand.Consent("0.1.1");
        var before = _stand.SharedSnapshot();

        Assert.Equal(0, await _stand.RunAsync());

        var install = Assert.Single(_stand.Machine.Installs);
        Assert.Equal(Path.Combine(_stand.Layout.Trusted, "EliteSIP-0.1.1.deb"), install.Package);
        Assert.Equal(package, File.ReadAllBytes(install.Package));
        Assert.Equal(Path.Combine(_stand.Layout.Trusted, "install-0.1.1.log"), install.Log);
        Assert.Equal("0.1.1", File.ReadAllText(_stand.Layout.RelaunchMarker));

        // Пакет взят у приложения — второй раз не качался.
        Assert.DoesNotContain(_stand.Server.Requested, url => url.EndsWith(".deb", StringComparison.Ordinal));

        // В общем каталоге root ничего не написал и не стёр.
        Assert.Equal(before, _stand.SharedSnapshot());
    }

    [Fact]
    public async Task Обработанное_согласие_второй_раз_не_ставится()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");

        await _stand.RunAsync();
        await _stand.RunAsync();

        Assert.Single(_stand.Machine.Installs);
    }

    [Fact]
    public async Task Повторное_согласие_на_ту_же_версию_снова_ставит()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");
        await _stand.RunAsync();

        // Установка не встала, приложение спросило снова и переписало отметку.
        _stand.Consent("0.1.1", _stand.Now.AddMinutes(20));
        await _stand.RunAsync();

        Assert.Equal(2, _stand.Machine.Installs.Count);
    }

    [Fact]
    public async Task На_канале_другая_версия_ждёт_нового_согласия()
    {
        _stand.Publish("0.1.2");
        _stand.Consent("0.1.1");

        Assert.Equal(0, await _stand.RunAsync());
        Assert.Empty(_stand.Machine.Installs);
        Assert.Contains("согласие было на 0.1.1", _stand.LogText, StringComparison.Ordinal);

        // Обработано: следующий такт в канал не идёт.
        _stand.Server.Requested.Clear();
        await _stand.RunAsync();
        Assert.Empty(_stand.Server.Requested);
    }

    [Fact]
    public async Task Уже_стоящий_выпуск_не_ставится()
    {
        _stand.Machine.Installed = new Version(0, 1, 1, 0);
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");

        Assert.Equal(0, await _stand.RunAsync());
        Assert.Empty(_stand.Machine.Installs);
    }

    [Fact]
    public async Task Подменённый_пакет_в_общем_каталоге_качается_заново()
    {
        var package = _stand.Publish("0.1.1");
        File.WriteAllText(Path.Combine(_stand.Layout.Shared, "EliteSIP-0.1.1.deb"), "подмена");
        _stand.Consent("0.1.1");

        await _stand.RunAsync();

        var install = Assert.Single(_stand.Machine.Installs);
        Assert.Equal(package, File.ReadAllBytes(install.Package));
        Assert.Contains($"{Stand.Channel}/releases/linux/EliteSIP-0.1.1.deb", _stand.Server.Requested);
    }

    [Fact]
    public async Task Ссылка_вместо_пакета_не_читается()
    {
        var package = _stand.Publish("0.1.1");
        var elsewhere = Path.Combine(_stand.Layout.Program, "чужое.deb");
        File.WriteAllBytes(elsewhere, package);
        File.CreateSymbolicLink(Path.Combine(_stand.Layout.Shared, "EliteSIP-0.1.1.deb"), elsewhere);
        _stand.Consent("0.1.1");

        await _stand.RunAsync();

        // Отпечаток бы сошёлся, но по ссылке root не ходит — качает сам.
        Assert.Single(_stand.Machine.Installs);
        Assert.Contains($"{Stand.Channel}/releases/linux/EliteSIP-0.1.1.deb", _stand.Server.Requested);
    }

    [Fact]
    public async Task Подпись_чужим_ключом_отвергается()
    {
        _stand.Publish("0.1.1");
        var manifest = $"{Stand.Channel}/releases/linux/current.json";
        var forged = _stand.Server.Files[manifest];
        forged[^5] ^= 1;
        _stand.Consent("0.1.1");

        Assert.Equal(1, await _stand.RunAsync());
        Assert.Empty(_stand.Machine.Installs);
        Assert.Contains("манифест ОТБРОШЕН", _stand.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Установщик_Windows_не_ставится_даже_подписанный()
    {
        _stand.Publish("0.1.1", $"{Stand.Channel}/releases/EliteSIP-0.1.1.exe");
        _stand.Consent("0.1.1");

        Assert.Equal(1, await _stand.RunAsync());
        Assert.Empty(_stand.Machine.Installs);
        Assert.Empty(Directory.GetFiles(_stand.Layout.Trusted, "*.exe"));
    }

    [Fact]
    public async Task Пакет_с_другого_хоста_не_качается()
    {
        _stand.Publish("0.1.1", "https://elsewhere.example/releases/linux/EliteSIP-0.1.1.deb");
        _stand.Consent("0.1.1");

        Assert.Equal(1, await _stand.RunAsync());
        Assert.Empty(_stand.Machine.Installs);
        Assert.DoesNotContain(_stand.Server.Requested, url => url.StartsWith("https://elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Пока_идёт_установка_второй_не_начинается()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");
        _stand.Machine.Installing = true;

        await _stand.RunAsync();
        Assert.Empty(_stand.Machine.Installs);

        // Согласие не потеряно: установка кончилась — ставится.
        _stand.Machine.Installing = false;
        await _stand.RunAsync();
        Assert.Single(_stand.Machine.Installs);
    }

    [Fact]
    public async Task Аварийный_путь_ставит_без_согласия()
    {
        _stand.Publish("0.1.1");

        // Первый такт запоминает, когда выпуск увиден; «запустилось» нет.
        await _stand.RunAsync();
        Assert.Empty(_stand.Machine.Installs);

        _stand.Now += RecoveryGrace;
        await _stand.RunAsync();

        Assert.Single(_stand.Machine.Installs);
        Assert.Contains("АВАРИЙНАЯ установка 0.1.1", _stand.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Аварийный_путь_молчит_если_выпуск_запускался()
    {
        _stand.Publish("0.1.1");
        File.WriteAllText(_stand.Layout.Started, "0.1.0");

        await _stand.RunAsync();
        _stand.Now += RecoveryGrace;
        await _stand.RunAsync();

        Assert.Empty(_stand.Machine.Installs);
    }

    [Fact]
    public async Task Аварийный_путь_молчит_пока_софтфон_запущен()
    {
        _stand.Publish("0.1.1");
        _stand.Machine.AppRunning = true;

        await _stand.RunAsync();
        _stand.Now += RecoveryGrace;
        await _stand.RunAsync();

        Assert.Empty(_stand.Machine.Installs);
    }

    [Fact]
    public async Task После_установки_софтфон_поднимается_и_отметка_снимается()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");
        await _stand.RunAsync();

        _stand.Machine.Installed = new Version(0, 1, 1, 0);
        await _stand.RunAsync();

        Assert.Equal(1, _stand.Machine.Restarts);
        Assert.False(File.Exists(_stand.Layout.RelaunchMarker));
    }

    [Fact]
    public async Task Пока_установка_идёт_софтфон_не_поднимается()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");
        await _stand.RunAsync();

        _stand.Machine.Installing = true;
        await _stand.RunAsync();

        Assert.Equal(0, _stand.Machine.Restarts);
        Assert.True(File.Exists(_stand.Layout.RelaunchMarker));
    }

    [Fact]
    public async Task Невставший_выпуск_возвращает_прежний_софтфон()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");
        await _stand.RunAsync();

        // Установка кончилась, а стоит всё ещё 0.1.0.
        await _stand.RunAsync();

        Assert.Equal(1, _stand.Machine.Restarts);
        Assert.Contains("выпуск 0.1.1 не встал", _stand.LogText, StringComparison.Ordinal);
        Assert.Single(_stand.Machine.Installs);
    }

    [Fact]
    public async Task Несостоявшаяся_установка_сразу_возвращает_софтфон()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");
        _stand.Machine.InstallStarts = false;

        Assert.Equal(1, await _stand.RunAsync());

        Assert.Equal(1, _stand.Machine.Restarts);
        Assert.False(File.Exists(_stand.Layout.RelaunchMarker));
    }

    [Fact]
    public async Task Неподнятый_софтфон_ждёт_следующего_такта()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");
        await _stand.RunAsync();
        _stand.Machine.Installed = new Version(0, 1, 1, 0);
        _stand.Machine.SessionsAnswer = false;

        await _stand.RunAsync();
        Assert.True(File.Exists(_stand.Layout.RelaunchMarker));

        _stand.Machine.SessionsAnswer = true;
        await _stand.RunAsync();
        Assert.False(File.Exists(_stand.Layout.RelaunchMarker));
    }

    [Fact]
    public async Task Отметка_подъёма_старше_суток_снимается_молча()
    {
        File.WriteAllText(_stand.Layout.RelaunchMarker, "0.1.0");
        File.SetLastWriteTimeUtc(_stand.Layout.RelaunchMarker, _stand.Now.AddDays(-2));

        await _stand.RunAsync();

        Assert.Equal(0, _stand.Machine.Restarts);
        Assert.False(File.Exists(_stand.Layout.RelaunchMarker));
    }

    [Fact]
    public async Task Уборка_оставляет_стоящий_ожидаемый_и_свежий()
    {
        void Put(string name, TimeSpan age)
        {
            var path = Path.Combine(_stand.Layout.Trusted, name);
            File.WriteAllText(path, name);
            File.SetLastWriteTimeUtc(path, _stand.Now - age);
        }

        var day = TimeSpan.FromDays(1);
        Put("EliteSIP-0.0.9.deb", day);
        Put("EliteSIP-0.1.0.deb", day);
        Put("EliteSIP-0.1.2.deb", day);
        Put("EliteSIP-0.1.3.deb", TimeSpan.FromMinutes(5));
        for (var i = 0; i < 5; i++)
        {
            Put($"install-0.0.{i}.log", day + TimeSpan.FromHours(i));
        }

        _stand.Consent("0.1.2");
        await _stand.RunAsync();

        var left = Directory.GetFiles(_stand.Layout.Trusted).Select(Path.GetFileName).ToHashSet();
        Assert.DoesNotContain("EliteSIP-0.0.9.deb", left);
        Assert.Contains("EliteSIP-0.1.0.deb", left);
        Assert.Contains("EliteSIP-0.1.2.deb", left);
        Assert.Contains("EliteSIP-0.1.3.deb", left);
        Assert.Equal(3, left.Count(name => name!.StartsWith("install-", StringComparison.Ordinal)));
        Assert.Contains("install-0.0.0.log", left);
    }

    [Fact]
    public async Task Стёртое_согласие_больше_не_действует()
    {
        _stand.Publish("0.1.1");
        _stand.Consent("0.1.1");
        _stand.Machine.Installing = true;
        await _stand.RunAsync();

        // Оператор передумал — приложение стёрло отметку. Копия у root не
        // должна пережить оригинал.
        File.Delete(_stand.Layout.Request);
        _stand.Machine.Installing = false;
        await _stand.RunAsync();

        Assert.Empty(_stand.Machine.Installs);
    }
}
