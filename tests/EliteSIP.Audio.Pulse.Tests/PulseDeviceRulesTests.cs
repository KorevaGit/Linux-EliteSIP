using EliteSIP.Audio.Pulse.Native;

namespace EliteSIP.Audio.Pulse.Tests;

public class PulseDeviceRulesTests
{
    [Fact]
    public void Транспорт_по_шине_USB_и_встроенная()
    {
        Assert.Equal(AudioTransport.Usb, Devices.LifeChatMic.Transport);
        Assert.Equal(AudioTransport.BuiltIn, Devices.BuiltInMic.Transport);
    }

    [Fact]
    public void Bluetooth_вход_всегда_режим_связи_выход_по_профилю()
    {
        var bluetooth = new Dictionary<string, string> { ["device.api"] = "bluez5", ["device.bus"] = "bluetooth" };

        PulseDevice input = Devices.Make("bluez_input.AA_BB", "Наушники", AudioDeviceDirection.Capture,
            new(bluetooth) { ["api.bluez5.profile"] = "headset-head-unit" }, null, [LibPulse.PositionMono]);
        PulseDevice a2dp = Devices.Make("bluez_output.AA_BB.1", "Наушники", AudioDeviceDirection.Render,
            new(bluetooth) { ["api.bluez5.profile"] = "a2dp-sink" }, null, [1, 2]);
        PulseDevice hfp = Devices.Make("bluez_output.AA_BB.1", "Наушники", AudioDeviceDirection.Render,
            new(bluetooth) { ["api.bluez5.profile"] = "headset-head-unit" }, null, [LibPulse.PositionMono]);

        Assert.Equal(AudioTransport.BluetoothHandsFree, input.Transport);
        Assert.Equal(AudioTransport.Bluetooth, a2dp.Transport);
        Assert.Equal(AudioTransport.BluetoothHandsFree, hfp.Transport);
    }

    [Fact]
    public void HDMI_узнаётся_по_имени_узла()
    {
        PulseDevice hdmi = Devices.Make("alsa_output.pci-0000_00_1f.3.hdmi-stereo", "Монитор", AudioDeviceDirection.Render,
            new() { ["device.bus"] = "pci", ["device.api"] = "alsa" }, null, [1, 2]);

        Assert.Equal(AudioTransport.Hdmi, hdmi.Transport);
    }

    [Fact]
    public void Узел_без_шины_и_драйвера_программный()
    {
        // Шумодав или эхоподавитель сервера: у такого узла нет ни device.bus, ни device.api.
        PulseDevice filter = Devices.Make("rnnoise_source", "Шумодав", AudioDeviceDirection.Capture,
            new() { ["media.class"] = "Audio/Source/Virtual" }, null, [LibPulse.PositionMono]);

        Assert.Equal(AudioTransport.Virtual, filter.Transport);
    }

    [Fact]
    public void Гарнитура_без_эха_по_форм_фактору()
    {
        Assert.True(PulseDeviceRules.IsEchoFree(Devices.LifeChatOut));
        Assert.False(PulseDeviceRules.IsEchoFree(Devices.BuiltInOut));
    }

    [Fact]
    public void Наушники_в_гнезде_встроенной_карты_тоже_без_эха()
    {
        // У встроенной карты форм-фактор internal и с воткнутыми наушниками —
        // видно это только по порту.
        PulseDevice headphones = Devices.BuiltInOut with
        {
            PortName = "analog-output-headphones",
            PortDescription = "Наушники",
        };

        Assert.True(PulseDeviceRules.IsEchoFree(headphones));
    }

    [Fact]
    public void Имя_собирается_как_на_Windows_порт_и_карта_в_скобках()
    {
        Assert.Equal("Микрофон (LifeChat LX-3000 Headset)", Devices.LifeChatMic.DisplayName);
        Assert.Equal("Внутренний микрофон (Встроенное аудио)", Devices.BuiltInMic.DisplayName);
    }

    [Fact]
    public void Без_порта_имя_из_описания_узла()
    {
        PulseDevice noPort = Devices.BuiltInMic with { PortName = null, PortDescription = null };
        PulseDevice bare = Devices.Make("null_sink", "Пустой выход", AudioDeviceDirection.Render, [], null, [1, 2]);

        // Описание узла уже содержит имя карты — не повторяем его в скобках.
        Assert.Equal("Встроенное аудио Аналоговый стерео", noPort.DisplayName);
        Assert.Equal("Пустой выход", bare.DisplayName);
    }

    [Fact]
    public void Микрофон_и_выход_одной_USB_гарнитуры_пара()
    {
        Assert.True(PulseDeviceRules.IsHeadsetPair(Devices.LifeChatMic, Devices.LifeChatOut));
    }

    [Fact]
    public void Микрофон_ноутбука_и_его_динамики_не_пара()
    {
        // Между ними эхо как раз есть — общее правило не берёт встроенную карту.
        Assert.False(PulseDeviceRules.IsHeadsetPair(Devices.BuiltInMic, Devices.BuiltInOut));
    }

    [Fact]
    public void Микрофон_ноутбука_и_USB_выход_не_пара()
    {
        Assert.False(PulseDeviceRules.IsHeadsetPair(Devices.BuiltInMic, Devices.LifeChatOut));
    }

    [Fact]
    public void Усиление_над_базовой_по_кубической_шкале()
    {
        // pactl на машине разработки: базовая 8 % — −66 дБ, 40 % — −23,8 дБ.
        Assert.Equal(65.8, PulseDeviceRules.BoostAboveBaseDb(LibPulse.VolumeNorm, 0x147A), 1);
        Assert.Equal(23.9, PulseDeviceRules.BoostAboveBaseDb(LibPulse.VolumeNorm, 0x6666), 1);
        Assert.Equal(0, PulseDeviceRules.BoostAboveBaseDb(LibPulse.VolumeNorm, 0));
    }

    [Fact]
    public void Порог_перекрученного_микрофона_между_LifeChat_и_ноутбуком()
    {
        double lifeChat = PulseDeviceRules.BoostAboveBaseDb(Devices.LifeChatMic.Volume, Devices.LifeChatMic.BaseVolume);
        double laptop = PulseDeviceRules.BoostAboveBaseDb(Devices.BuiltInMic.Volume, Devices.BuiltInMic.BaseVolume);

        Assert.True(lifeChat < PulseDeviceRules.OverdrivenBoostDb);
        Assert.True(laptop > PulseDeviceRules.OverdrivenBoostDb);
    }

    [Fact]
    public void Монитор_выхода_не_микрофон()
    {
        Assert.False(PulseDeviceRules.IsUsableInput(Devices.BuiltInMonitor));
        Assert.True(PulseDeviceRules.IsUsableInput(Devices.BuiltInMic));
    }

    [Fact]
    public void Голос_только_в_передней_паре_семь_один()
    {
        // FL FR FC LFE RL RR SL SR — раскладка 7.1 у pa_channel_map.
        int[] surround = [1, 2, 3, 7, 5, 6, 10, 11];

        Assert.Equal(
            [true, true, false, false, false, false, false, false],
            PulseDeviceRules.VoicedChannels(surround));
    }

    [Fact]
    public void Голос_в_моно_и_в_раскладке_без_передней_пары()
    {
        Assert.Equal([true], PulseDeviceRules.VoicedChannels([LibPulse.PositionMono]));

        // Только «вспомогательные» каналы — первые два, а не тишина.
        Assert.Equal([true, true, false], PulseDeviceRules.VoicedChannels([12, 13, 14]));
    }

    [Fact]
    public void Запасной_микрофон_той_же_карты_что_и_вывод()
    {
        PulseSnapshot snapshot = new(
            [Devices.BuiltInMonitor, Devices.BuiltInMic, Devices.LifeChatMic],
            [Devices.BuiltInOut, Devices.LifeChatOut],
            "virtual_source",
            Devices.LifeChatOut.Name);

        PulseDevice? spare = PulseDeviceCatalog.SpareCapture(snapshot, ["virtual_source"], Devices.LifeChatOut.DisplayName);

        // Случай машины разработки наоборот: звук идёт в LifeChat — значит и
        // говорить в неё, а не в ноутбук. Монитор не рассматривается вовсе.
        Assert.Equal(Devices.LifeChatMic.Name, spare?.Name);
    }

    [Fact]
    public void Общий_тип_несёт_имя_для_правил_и_состояние_гнезда()
    {
        AudioDevice device = (Devices.BuiltInMic with { PortUnplugged = true }).ToAudioDevice();

        Assert.Equal(Devices.BuiltInMic.Name, device.Id);
        Assert.Equal("Внутренний микрофон (Встроенное аудио)", device.Name);
        Assert.Equal(AudioDeviceAvailability.Unplugged, device.Availability);
        Assert.Equal("pci", device.TransportName);
    }
}
