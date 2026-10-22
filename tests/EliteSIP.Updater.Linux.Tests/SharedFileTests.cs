using System.Diagnostics;
using EliteSIP.Updater;

namespace EliteSIP.Updater.Linux.Tests;

/// <summary>
/// Чтение общего каталога: всё, чем оператор может подменить файл, на
/// настоящей файловой системе.
/// </summary>
public sealed class SharedFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EliteSIP.SharedFile.Tests", Guid.NewGuid().ToString("N"));

    public SharedFileTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Обычный_файл_читается_со_временем_записи()
    {
        var path = Path.Combine(_root, "started");
        File.WriteAllText(path, "0.1.4");
        var written = new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        Assert.True(SharedFile.TryRead(path, 64, out var data, out var time));
        Assert.Equal("0.1.4"u8.ToArray(), data);
        Assert.Equal(written, time);
    }

    [Fact]
    public void Ссылка_не_открывается()
    {
        var target = Path.Combine(_root, "target");
        File.WriteAllText(target, "0.1.4");
        var link = Path.Combine(_root, "link");
        File.CreateSymbolicLink(link, target);

        Assert.False(SharedFile.TryRead(link, 64, out _, out _));
    }

    [Fact]
    public void Больше_потолка_не_читается()
    {
        var path = Path.Combine(_root, "big");
        File.WriteAllBytes(path, new byte[65]);

        Assert.False(SharedFile.TryRead(path, 64, out _, out _));
    }

    [Fact]
    public void Каталог_вместо_файла_не_читается()
    {
        Assert.False(SharedFile.TryRead(_root, 64, out _, out _));
    }

    /// <summary>
    /// Именованный канал без писателя: обычное открытие ждало бы вечно, и
    /// служба обновлений висела бы, пока её не снимет systemd.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task Именованный_канал_не_вешает()
    {
        var fifo = Path.Combine(_root, "update-requested");
        using (var mkfifo = Process.Start("mkfifo", [fifo]))
        {
            await mkfifo.WaitForExitAsync();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var read = await Task.Run(() => SharedFile.TryRead(fifo, 64, out _, out _));

        Assert.False(read);
    }

    [Fact]
    public void Копия_уходит_вместе_с_оригиналом()
    {
        var source = Path.Combine(_root, "update-requested");
        var copy = Path.Combine(_root, "inbox", "update-requested");
        File.WriteAllText(source, "0.1.4");

        Assert.True(SharedFile.TryCopyIn(source, copy, 64));
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(copy));

        File.Delete(source);

        Assert.False(SharedFile.TryCopyIn(source, copy, 64));
        Assert.False(File.Exists(copy));
    }
}
