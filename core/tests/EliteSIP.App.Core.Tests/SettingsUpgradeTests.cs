using System.IO;
using EliteSIP.App.Settings;
using EliteSIP.Audio;

namespace EliteSIP.App.Tests;

/// <summary>
/// Однократные правки старых файлов настроек (<c>AppSettings.Upgrade</c>).
/// </summary>
public sealed class SettingsUpgradeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("elitesip-settings-").FullName;

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"Setup\":null}")]
    [InlineData(null)]
    public void Startup_recovers_configured_profile_when_primary_is_unusable(string? primary)
    {
        var settings = new AppSettings();
        settings.Account.Username = "701";
        settings.Setup.IsCompleted = true;
        settings.Save(FilePath);
        if (primary is null)
        {
            File.Delete(FilePath);
        }
        else
        {
            File.WriteAllText(FilePath, primary);
        }

        var restored = AppSettings.LoadForStartup(FilePath, out bool recovered);

        Assert.True(recovered);
        Assert.True(restored.Setup.IsCompleted);
        Assert.Equal("701", AppSettings.Load(FilePath).Account.Username);
        if (primary is not null)
        {
            Assert.Equal(primary, File.ReadAllText(FilePath + AppSettings.UnreadableSuffix));
        }
    }

    [Fact]
    public void Startup_preserves_unreadable_profile_without_a_recovery_copy()
    {
        File.WriteAllText(FilePath, "{");

        Assert.Throws<InvalidDataException>(() => AppSettings.LoadForStartup(FilePath, out _));

        Assert.Equal("{", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Startup_distinguishes_fresh_installation_from_missing_profile_with_history()
    {
        Assert.False(AppSettings.LoadForStartup(FilePath, out _).Setup.IsCompleted);

        File.WriteAllText(Path.Combine(_directory, "history.db"), "");

        Assert.Throws<InvalidDataException>(() => AppSettings.LoadForStartup(FilePath, out _));
    }

    [Fact]
    public void Startup_finishes_an_interrupted_reset_without_restoring_old_profile()
    {
        var settings = new AppSettings();
        settings.Setup.IsCompleted = true;
        settings.Save(FilePath);
        File.Delete(FilePath);
        var marker = Path.Combine(_directory, AppSettings.ResetMarkerName);
        File.WriteAllText(marker, "");

        Assert.False(AppSettings.LoadForStartup(FilePath, out bool recovered).Setup.IsCompleted);

        Assert.False(recovered);
        Assert.False(File.Exists(FilePath + AppSettings.RecoverySuffix));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Sip_trace_is_on_by_default_and_preserves_an_explicit_opt_out()
    {
        var fresh = AppSettings.LoadForStartup(FilePath, out _);
        Assert.True(fresh.Maintenance.LogToFile);
        Assert.True(fresh.Maintenance.LogsSipTrace);
        fresh.Maintenance.LogsSipTrace = false;
        fresh.Save(FilePath);

        Assert.False(AppSettings.LoadForStartup(FilePath, out _).Maintenance.LogsSipTrace);
    }

    private static AppSettings Tinkered(int? revision)
    {
        var settings = new AppSettings { Revision = revision };
        settings.Audio.NoiseSuppression = false;
        settings.Audio.BackgroundVoiceSuppression = false;
        settings.Audio.AutomaticGainControl = true;
        settings.Audio.ReceiveGainControl = true;
        settings.Audio.WindowsProcessing = WindowsCaptureProcessing.On;
        settings.Audio.InputDeviceId = "гарнитура";
        settings.Audio.OutputDeviceId = "наушники";
        settings.Audio.MicrophoneGain = 1.5;
        settings.Audio.PlaybackVolume = 1.8;
        return settings;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Ревизия_2_возвращает_обработку_к_рекомендуемой(int? revision)
    {
        Tinkered(revision).Save(FilePath);

        var loaded = AppSettings.Load(FilePath);

        Assert.True(loaded.Audio.NoiseSuppression);
        Assert.True(loaded.Audio.BackgroundVoiceSuppression);
        Assert.False(loaded.Audio.AutomaticGainControl);
        Assert.False(loaded.Audio.ReceiveGainControl);
        Assert.Equal(WindowsCaptureProcessing.Auto, loaded.Audio.WindowsProcessing);
        Assert.Equal(AppSettings.CurrentRevision, loaded.Revision);
    }

    [Fact]
    public void Ревизия_5_включает_тоны_клавиш_и_сигналы_разговора_один_раз()
    {
        var old = new AppSettings { Revision = 4 };
        old.Audio.MutesSystemSounds = true;
        old.Save(FilePath);

        var loaded = AppSettings.Load(FilePath);
        Assert.False(loaded.Audio.MutesSystemSounds);

        // Выключенные после обновления остаются выключенными.
        loaded.Audio.MutesSystemSounds = true;
        loaded.Save(FilePath);
        Assert.True(AppSettings.Load(FilePath).Audio.MutesSystemSounds);
    }

    [Fact]
    public void Устройства_громкость_и_усиление_не_трогаются()
    {
        Tinkered(1).Save(FilePath);

        var loaded = AppSettings.Load(FilePath);

        Assert.Equal("гарнитура", loaded.Audio.InputDeviceId);
        Assert.Equal("наушники", loaded.Audio.OutputDeviceId);
        Assert.Equal(1.5, loaded.Audio.MicrophoneGain);
        Assert.Equal(1.8, loaded.Audio.PlaybackVolume);
    }

    [Fact]
    public void Сброс_однократный_выбор_после_него_остаётся()
    {
        Tinkered(1).Save(FilePath);
        var upgraded = AppSettings.Load(FilePath);

        upgraded.Audio.NoiseSuppression = false;
        upgraded.Audio.WindowsProcessing = WindowsCaptureProcessing.Off;
        upgraded.Save(FilePath);

        var again = AppSettings.Load(FilePath);

        Assert.False(again.Audio.NoiseSuppression);
        Assert.Equal(WindowsCaptureProcessing.Off, again.Audio.WindowsProcessing);
    }

    [Fact]
    public void Новая_машина_сразу_на_текущей_ревизии()
    {
        // Иначе её файл ушёл бы на диск без ревизии, и правки сработали бы
        // на следующем запуске поверх выбора первого дня.
        var fresh = AppSettings.Load(FilePath);
        fresh.Audio.NoiseSuppression = false;
        fresh.Save(FilePath);

        var again = AppSettings.Load(FilePath);

        Assert.Equal(AppSettings.CurrentRevision, fresh.Revision);
        Assert.False(again.Audio.NoiseSuppression);
    }
}
