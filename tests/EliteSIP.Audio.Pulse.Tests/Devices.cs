using EliteSIP.Audio.Pulse.Native;

namespace EliteSIP.Audio.Pulse.Tests;

/// <summary>
/// Устройства в том виде, в каком их отдал сервер на машине разработки
/// 20 октября 2026 (<c>docs/L0-RISKS.md</c>, «Свойства устройств»), плюс
/// Bluetooth и фильтры по документации WirePlumber — их на машине нет.
/// </summary>
internal static class Devices
{
    internal static PulseDevice LifeChatMic { get; } = Make(
        "alsa_input.usb-C-Media_Electronics_Inc._Microsoft_LifeChat_LX-3000-00.mono-fallback",
        "LifeChat LX-3000 Headset Моно",
        AudioDeviceDirection.Capture,
        new()
        {
            ["device.description"] = "LifeChat LX-3000 Headset",
            ["device.form_factor"] = "headset",
            ["device.bus"] = "usb",
            ["device.api"] = "alsa",
            ["device.class"] = "sound",
        },
        port: ("analog-input-mic", "Микрофон"),
        channels: [LibPulse.PositionMono],
        volume: LibPulse.VolumeNorm,
        baseVolume: 0x6666); // 40 % — −23,8 дБ

    internal static PulseDevice LifeChatOut { get; } = Make(
        "alsa_output.usb-C-Media_Electronics_Inc._Microsoft_LifeChat_LX-3000-00.analog-stereo",
        "LifeChat LX-3000 Headset Аналоговый стерео",
        AudioDeviceDirection.Render,
        new()
        {
            ["device.description"] = "LifeChat LX-3000 Headset",
            ["device.form_factor"] = "headset",
            ["device.bus"] = "usb",
            ["device.api"] = "alsa",
            ["device.class"] = "sound",
        },
        port: ("analog-output-speaker", "Динамики"),
        channels: [LibPulse.PositionFrontLeft, LibPulse.PositionFrontRight]);

    internal static PulseDevice BuiltInMic { get; } = Make(
        "alsa_input.pci-0000_00_1f.3.analog-stereo",
        "Встроенное аудио Аналоговый стерео",
        AudioDeviceDirection.Capture,
        new()
        {
            ["device.description"] = "Встроенное аудио",
            ["device.form_factor"] = "internal",
            ["device.bus"] = "pci",
            ["device.api"] = "alsa",
            ["device.class"] = "sound",
        },
        port: ("analog-input-internal-mic", "Внутренний микрофон"),
        channels: [LibPulse.PositionFrontLeft, LibPulse.PositionFrontRight],
        volume: LibPulse.VolumeNorm,
        baseVolume: 0x147A); // 8 % — −66 дБ

    internal static PulseDevice BuiltInOut { get; } = Make(
        "alsa_output.pci-0000_00_1f.3.analog-stereo",
        "Встроенное аудио Аналоговый стерео",
        AudioDeviceDirection.Render,
        new()
        {
            ["device.description"] = "Встроенное аудио",
            ["device.form_factor"] = "internal",
            ["device.bus"] = "pci",
            ["device.api"] = "alsa",
            ["device.class"] = "sound",
        },
        port: ("analog-output-speaker", "Динамики"),
        channels: [LibPulse.PositionFrontLeft, LibPulse.PositionFrontRight]);

    /// <summary>Монитор выхода: в списке входов у сервера, говорить в него нельзя.</summary>
    internal static PulseDevice BuiltInMonitor { get; } = Make(
        "alsa_output.pci-0000_00_1f.3.analog-stereo.monitor",
        "Monitor of Встроенное аудио",
        AudioDeviceDirection.Capture,
        new()
        {
            ["device.description"] = "Встроенное аудио",
            ["device.bus"] = "pci",
            ["device.api"] = "alsa",
            ["device.class"] = "monitor",
        },
        port: null,
        channels: [LibPulse.PositionFrontLeft, LibPulse.PositionFrontRight],
        isMonitor: true);

    internal static PulseDevice Make(
        string name,
        string description,
        AudioDeviceDirection direction,
        Dictionary<string, string> properties,
        (string Name, string Description)? port,
        int[] channels,
        bool isMonitor = false,
        bool unplugged = false,
        uint volume = LibPulse.VolumeNorm,
        uint baseVolume = LibPulse.VolumeNorm) =>
        new(
            name,
            description,
            direction,
            properties,
            channels.Length,
            48000,
            channels,
            isMonitor,
            port?.Name,
            port?.Description,
            unplugged,
            volume,
            baseVolume);
}
