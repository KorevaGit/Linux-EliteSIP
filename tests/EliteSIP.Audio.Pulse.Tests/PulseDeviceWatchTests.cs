namespace EliteSIP.Audio.Pulse.Tests;

public class PulseDeviceWatchTests
{
    private static readonly PulseSnapshot Desk = new(
        [Devices.BuiltInMonitor, Devices.BuiltInMic, Devices.LifeChatMic],
        [Devices.BuiltInOut, Devices.LifeChatOut],
        Devices.BuiltInMic.Name,
        Devices.LifeChatOut.Name);

    [Fact]
    public void Ничего_не_поменялось_ничего_не_сообщаем()
    {
        Assert.Empty(PulseDeviceWatch.Diff(Desk, Desk));
    }

    [Fact]
    public void Громкость_не_повод()
    {
        // «Изменилось» сервер шлёт на каждое движение ползунка; пересборка на
        // это рвала бы разговор.
        PulseSnapshot quieter = Desk with
        {
            Sources = [Devices.BuiltInMonitor, Devices.BuiltInMic with { Volume = 1000 }, Devices.LifeChatMic],
        };

        Assert.Empty(PulseDeviceWatch.Diff(Desk, quieter));
    }

    [Fact]
    public void Выдернули_USB_гарнитуру_исчезли_вход_и_выход_и_сменилось_умолчание()
    {
        PulseSnapshot unplugged = new(
            [Devices.BuiltInMonitor, Devices.BuiltInMic],
            [Devices.BuiltInOut],
            Devices.BuiltInMic.Name,
            Devices.BuiltInOut.Name);

        List<AudioDeviceChange> changes = PulseDeviceWatch.Diff(Desk, unplugged);

        Assert.Contains(new AudioDeviceChange(AudioDeviceChangeKind.DeviceRemoved, Devices.LifeChatMic.Name, AudioDeviceDirection.Capture, null), changes);
        Assert.Contains(new AudioDeviceChange(AudioDeviceChangeKind.DeviceRemoved, Devices.LifeChatOut.Name, AudioDeviceDirection.Render, null), changes);
        Assert.Contains(new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged, Devices.BuiltInOut.Name, AudioDeviceDirection.Render, null), changes);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void Воткнули_гарнитуру_обратно()
    {
        PulseSnapshot without = new([Devices.BuiltInMic], [Devices.BuiltInOut], Devices.BuiltInMic.Name, Devices.BuiltInOut.Name);
        PulseSnapshot with = new([Devices.BuiltInMic, Devices.LifeChatMic], [Devices.BuiltInOut, Devices.LifeChatOut], Devices.BuiltInMic.Name, Devices.BuiltInOut.Name);

        List<AudioDeviceChange> changes = PulseDeviceWatch.Diff(without, with);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(AudioDeviceChangeKind.DeviceAdded, change.Kind));
    }

    [Fact]
    public void Наушники_из_гнезда_встроенной_карты_это_смена_состояния()
    {
        PulseDevice plugged = Devices.BuiltInOut with { PortName = "analog-output-headphones", PortDescription = "Наушники" };
        PulseSnapshot before = Desk with { Sinks = [plugged, Devices.LifeChatOut] };
        PulseSnapshot after = Desk with { Sinks = [Devices.BuiltInOut, Devices.LifeChatOut] };

        AudioDeviceChange change = Assert.Single(PulseDeviceWatch.Diff(before, after));

        Assert.Equal(AudioDeviceChangeKind.DeviceStateChanged, change.Kind);
        Assert.Equal(Devices.BuiltInOut.Name, change.DeviceId);
    }

    [Fact]
    public void Монитор_выхода_не_повод()
    {
        PulseSnapshot noMonitor = Desk with { Sources = [Devices.BuiltInMic, Devices.LifeChatMic] };

        Assert.Empty(PulseDeviceWatch.Diff(Desk, noMonitor));
    }

    [Fact]
    public void Выдернутая_гарнитура_нашего_тракта_пересобирает_его()
    {
        // Стык с общим фильтром: изменения отсюда он понимает так же, как
        // изменения от WASAPI.
        var binding = new AudioRouteBinding(null, null, Devices.BuiltInMic.Name, Devices.LifeChatOut.Name);
        PulseSnapshot unplugged = new([Devices.BuiltInMic], [Devices.BuiltInOut], Devices.BuiltInMic.Name, Devices.BuiltInOut.Name);

        Assert.Contains(
            PulseDeviceWatch.Diff(Desk, unplugged),
            change => DeviceChangeRelevance.AffectsRoute(change, binding));
    }
}
