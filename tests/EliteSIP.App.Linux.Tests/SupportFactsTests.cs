using System.IO.Compression;
using EliteSIP.App.Platform;
using EliteSIP.Audio;
using EliteSIP.Diagnostics;

namespace EliteSIP.App.Tests;

/// <summary>Строка запуска и архив поддержки: версия, общий код, сеанс, звук, файлы.</summary>
public sealed class SupportFactsTests
{
    private static readonly Dictionary<string, string?> X11 = new()
    {
        ["XDG_SESSION_TYPE"] = "x11",
        ["DISPLAY"] = ":0",
        ["XDG_CURRENT_DESKTOP"] = "X-Cinnamon",
    };

    [Fact]
    public void Общий_код_вписан_сборкой()
    {
        // Сборка идёт из git-дерева с подмодулем — значит, git describe есть.
        Assert.NotEqual("неизвестен", SupportFacts.SharedCode);
        Assert.Matches("-g[0-9a-f]{7,}", SupportFacts.SharedCode);
    }

    [Fact]
    public void Строка_запуска_говорит_версию_общий_код_и_сеанс()
    {
        var line = SupportFacts.StartupLine("0.1.2", X11.GetValueOrDefault);

        Assert.Contains("EliteSIP 0.1.2 (Linux)", line, StringComparison.Ordinal);
        Assert.Contains(SupportFacts.SharedCode, line, StringComparison.Ordinal);
        Assert.Contains("X11 (X-Cinnamon", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Справка_со_звуком_и_сеансом()
    {
        var summary = SupportFacts.Summary("0.1.2", "M-1", "101", new TestAudioPlatform(), X11.GetValueOrDefault);

        Assert.Contains("общий код: " + SupportFacts.SharedCode, summary, StringComparison.Ordinal);
        Assert.Contains("сеанс: X11", summary, StringComparison.Ordinal);
        Assert.Contains("микрофон по умолчанию: Встроенный микрофон", summary, StringComparison.Ordinal);
        Assert.Contains("выходы: Динамики; LifeChat LX-3000", summary, StringComparison.Ordinal);
        Assert.Contains("добавочный: 101", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Нет_звукового_сервера_справка_всё_равно_собирается()
    {
        var summary = SupportFacts.Summary("0.1.2", null, null, new NoSoundServer(), X11.GetValueOrDefault);

        Assert.Contains("звук: сервер недоступен", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Архив_везёт_файлы_журнала_и_crash_log_а_не_строки()
    {
        // До исправления в архив уходили строки журнала вместо путей, и он
        // приезжал с одним summary.txt.
        var directory = Directory.CreateTempSubdirectory("elitesip-").FullName;
        try
        {
            var log = Path.Combine(directory, "elitesip.log");
            File.WriteAllLines(log, ["12:00:00.000 i запуск", string.Empty, "12:00:01.000 i регистрация"]);
            File.WriteAllText(Path.Combine(directory, "crash.log"), "System.InvalidOperationException");

            var files = SupportFacts.Files([log], directory);
            var archive = SupportArchive.Make(files, "справка", Path.Combine(directory, "support.zip"));

            using var zip = ZipFile.OpenRead(archive);
            Assert.Equal(
                ["crash.log", "elitesip.log", "summary.txt"],
                zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Нет_crash_log_нет_и_в_списке()
    {
        var directory = Directory.CreateTempSubdirectory("elitesip-").FullName;
        try
        {
            Assert.Equal(["a.log"], SupportFacts.Files(["a.log"], directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NoSoundServer : IAudioPlatform
    {
        public IVoiceAudioEngine CreateEngine(VoiceAudioConfiguration configuration) => throw new NotSupportedException();

        public ISignalSounds CreateSignalSounds(Action<string> log) => throw new NotSupportedException();

        public IReadOnlyList<AudioDevice> Devices(AudioDeviceDirection direction) =>
            throw new InvalidOperationException("Connection refused");

        public AudioDevice? DefaultDevice(AudioDeviceDirection direction) =>
            throw new InvalidOperationException("Connection refused");
    }
}
