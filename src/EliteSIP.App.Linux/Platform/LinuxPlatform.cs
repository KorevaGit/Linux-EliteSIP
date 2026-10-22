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
