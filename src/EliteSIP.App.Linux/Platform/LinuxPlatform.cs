using EliteSIP.Audio;
using EliteSIP.Audio.Pulse;

namespace EliteSIP.App.Platform;

/// <summary>Звук Linux: тракт и каталог на PulseAudio, служебные звуки на том же сервере.</summary>
internal sealed class PulseAudioPlatform : IAudioPlatform
{
    public IVoiceAudioEngine CreateEngine(VoiceAudioConfiguration configuration) =>
        new PulseVoiceAudioEngine(configuration);

    public ISignalSounds CreateSignalSounds(Action<string> log) => PulseSignalSounds.Create(log);

    public IReadOnlyList<AudioDevice> Devices(AudioDeviceDirection direction) =>
        PulseDeviceCatalog.Devices(direction);

    public AudioDevice? DefaultDevice(AudioDeviceDirection direction) =>
        PulseDeviceCatalog.Default(direction);
}

/// <summary>
/// Передача выпуска обновляльщику — до W-L7 её нет.
/// </summary>
///
/// <remarks>
/// Каталог обмена тот, что решён в плане (<c>/var/lib/elitesip</c>), но
/// обновляльщика под root и его службы пока нет. Отказ здесь — честное
/// «не вышло»: линия обновлений скажет об этом в журнал и не будет ждать
/// установки, которой не случится.
/// </remarks>
internal sealed class PendingUpdateHandoff : IUpdateHandoff
{
    public string UpdatesDirectory => "/var/lib/elitesip/updates";

    public bool Request(Version version, Action<string> log)
    {
        log($"обновление {version}: обновляльщика на Linux ещё нет (W-L7)");
        return false;
    }

    public bool Trigger(Action<string> log) => false;
}
