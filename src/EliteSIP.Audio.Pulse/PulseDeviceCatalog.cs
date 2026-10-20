namespace EliteSIP.Audio.Pulse;

/// <summary>
/// Устройства для окна настроек и оболочки — то же, что <c>AudioDeviceCatalog</c>
/// на Windows.
/// </summary>
///
/// <remarks>
/// Каждый вопрос — со своим коротким подключением. Список спрашивают редко
/// (открыли настройки, нажали «обновить»), а держать ради этого подключение всё
/// время работы значит ещё один поток цикла и ещё одну точку отказа при
/// перезапуске сервера. Подключение стоит единицы миллисекунд.
/// </remarks>
public static class PulseDeviceCatalog
{
    /// <summary>Устройства, на которых можно говорить. Пусто — сервера нет.</summary>
    public static IReadOnlyList<AudioDevice> Devices(AudioDeviceDirection direction)
    {
        PulseSnapshot snapshot = Snapshot();
        IEnumerable<PulseDevice> devices = direction == AudioDeviceDirection.Capture
            ? snapshot.UsableInputs
            : snapshot.Sinks;

        return [.. devices.Select(device => device.ToAudioDevice())];
    }

    /// <summary>Системное устройство по умолчанию. <c>null</c> — нет или сервера нет.</summary>
    public static AudioDevice? Default(AudioDeviceDirection direction) =>
        Snapshot().Default(direction)?.ToAudioDevice();

    /// <summary>
    /// Снимок у сервера. Сервер недоступен — пустой снимок, а не исключение:
    /// окну настроек нечего показать, но падать ему не из-за чего.
    /// </summary>
    internal static PulseSnapshot Snapshot()
    {
        using PulseConnection? connection = PulseConnection.Connect("EliteSIP", out _);
        if (connection is null)
        {
            return PulseSnapshot.Empty;
        }

        try
        {
            return PulseIntrospection.Snapshot(connection);
        }
        catch (InvalidOperationException)
        {
            return PulseSnapshot.Empty;
        }
    }

    /// <summary>
    /// Запасной микрофон взамен молчащего — по общим правилам продукта.
    /// </summary>
    internal static PulseDevice? SpareCapture(
        PulseSnapshot snapshot,
        IReadOnlyCollection<string> excluding,
        string? renderName)
    {
        AudioDevice? spare = AudioDeviceRules.PickSpareCapture(
            [.. snapshot.UsableInputs.Select(device => device.ToAudioDevice())],
            excluding,
            renderName);

        return spare is null ? null : snapshot.Find(spare.Id, AudioDeviceDirection.Capture);
    }
}
