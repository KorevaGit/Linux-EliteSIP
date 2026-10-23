using EliteSIP.App.Platform;
using EliteSIP.Audio;

namespace EliteSIP.App.Tests;

/// <summary>Звук без сервера: два устройства в каждую сторону и немые сигналы.</summary>
internal sealed class TestAudioPlatform : IAudioPlatform
{
    public IVoiceAudioEngine CreateEngine(VoiceAudioConfiguration configuration) => throw new NotSupportedException();

    public ISignalSounds CreateSignalSounds(Action<string> log) => new SilentSounds();

    public IReadOnlyList<AudioDevice> Devices(AudioDeviceDirection direction) =>
    [
        new("one", direction is AudioDeviceDirection.Capture ? "Встроенный микрофон" : "Динамики", direction,
            AudioTransport.BuiltIn, "pci", AudioDeviceAvailability.Active, 2, 48000, true),
        new("two", "LifeChat LX-3000", direction,
            AudioTransport.Usb, "usb", AudioDeviceAvailability.Active, 2, 48000, true),
    ];

    public AudioDevice? DefaultDevice(AudioDeviceDirection direction) => Devices(direction)[0];

    private sealed class SilentSounds : ISignalSounds
    {
        public void StartRingback(string? deviceId, double volume = 0.35) { }
        public void StopRingback() { }
        public void StartRingtone(string? filePath, double volume, string? deviceId) { }
        public void StopRingtone() { }
        public bool PreviewRingtone(string? filePath, double volume, string? deviceId) => true;
        public void StopPreview() { }
        public void PlayKeyTone(char key, string? deviceId) { }
        public void PlayHangUp(string? deviceId) { }
        public void PlayConnected(string? deviceId) { }
        public void Dispose() { }
    }
}

internal sealed class TestAutoStart : IAutoStart
{
    public bool IsEnabled { get; private set; } = true;

    public bool TrySet(bool enabled)
    {
        IsEnabled = enabled;
        return true;
    }
}
