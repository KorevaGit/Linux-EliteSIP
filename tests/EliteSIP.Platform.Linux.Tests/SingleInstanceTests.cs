namespace EliteSIP.Platform.Linux.Tests;

/// <summary>
/// Одна копия: замок и побудка.
/// </summary>
///
/// <remarks>
/// <c>flock</c> держится на открытом файле, а не на процессе, поэтому две
/// «копии» в одном процессе проверки видят друг друга так же, как два
/// процесса.
/// </remarks>
public sealed class SingleInstanceTests
{
    private static void Silent(string message)
    {
    }

    [Fact]
    public async Task Вторая_копия_будит_первую_и_уходит()
    {
        using TemporaryDirectory directory = new();
        using var first = SingleInstance.Claim(Silent, directory.Path);
        Assert.NotNull(first);

        TaskCompletionSource woken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Watch(() => woken.TrySetResult(), Silent);

        Assert.Null(SingleInstance.Claim(Silent, directory.Path));
        await woken.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void После_выхода_первой_место_свободно()
    {
        using TemporaryDirectory directory = new();

        SingleInstance.Claim(Silent, directory.Path)!.Dispose();

        using var next = SingleInstance.Claim(Silent, directory.Path);
        Assert.NotNull(next);
    }

    /// <summary>
    /// Сокет, оставшийся от упавшей копии, не мешает следующей: замок
    /// свободен — значит, слушать старый сокет некому.
    /// </summary>
    [Fact]
    public void Брошенный_сокет_не_мешает_запуску()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllText(directory.Combine("instance.sock"), "");

        using var instance = SingleInstance.Claim(Silent, directory.Path);
        Assert.NotNull(instance);
    }

    [Fact]
    public void Каталог_замка_закрыт_от_других()
    {
        using TemporaryDirectory directory = new();
        var run = directory.Combine("run");

        using var instance = SingleInstance.Claim(Silent, run);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(run));
    }

    /// <summary>
    /// Первая копия взяла замок, но не слушает: вторая не висит дольше
    /// своего терпения и не запускается рядом.
    /// </summary>
    [Fact]
    public void Молчащая_первая_не_держит_вторую()
    {
        using TemporaryDirectory directory = new();
        using var first = SingleInstance.Claim(Silent, directory.Path);
        File.Delete(directory.Combine("instance.sock"));

        List<string> log = [];
        var started = DateTime.UtcNow;

        Assert.Null(SingleInstance.Claim(log.Add, directory.Path));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
        Assert.Contains(log, line => line.Contains("не ответила", StringComparison.Ordinal));
    }
}
