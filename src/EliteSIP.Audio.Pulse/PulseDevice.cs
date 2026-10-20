using System.Globalization;

namespace EliteSIP.Audio.Pulse;

/// <summary>
/// Устройство так, как его описал звуковой сервер: вход (<c>source</c>) или
/// выход (<c>sink</c>) со свойствами.
/// </summary>
///
/// <remarks>
/// Отдельно от общего <see cref="AudioDevice"/>, потому что правила продукта
/// (<see cref="PulseDeviceRules"/>) читают то, чего в общем типе нет и быть не
/// должно: свойства <c>device.*</c>, порт, громкость против базовой. Общий тип
/// из этого собирается один раз и дальше живёт как на Windows.
/// </remarks>
/// <param name="Name">
/// Имя устройства у сервера (<c>alsa_input.usb-…mono-fallback</c>). Оно же —
/// идентификатор в настройках: переживает перезагрузку и переподключение, как
/// строка конечной точки на Windows.
/// </param>
/// <param name="Description">Описание узла для человека — то, что показывает системный микшер.</param>
/// <param name="Direction">Вход или выход.</param>
/// <param name="Properties">Свойства узла (<c>device.form_factor</c>, <c>device.bus</c> и прочие) как есть.</param>
/// <param name="Channels">Каналов у устройства.</param>
/// <param name="SampleRate">Частота устройства.</param>
/// <param name="ChannelPositions">Раскладка каналов (<c>pa_channel_position_t</c>) — по ней голос кладётся в переднюю пару.</param>
/// <param name="IsMonitor">Вход — монитор выхода: слышит то, что играет, а не микрофон.</param>
/// <param name="PortName">Действующий порт (<c>analog-input-mic</c>). Пусто — портов нет.</param>
/// <param name="PortDescription">Подпись порта для человека: «Микрофон», «Наушники».</param>
/// <param name="PortUnplugged">Сервер знает, что в разъём порта ничего не воткнуто.</param>
/// <param name="Volume">Средняя громкость по каналам, в единицах сервера (<c>0x10000</c> — 100 %).</param>
/// <param name="BaseVolume">Базовая громкость — уровень без программного усиления. 0 — сервер её не знает.</param>
internal sealed record PulseDevice(
    string Name,
    string Description,
    AudioDeviceDirection Direction,
    IReadOnlyDictionary<string, string> Properties,
    int Channels,
    int SampleRate,
    IReadOnlyList<int> ChannelPositions,
    bool IsMonitor,
    string? PortName,
    string? PortDescription,
    bool PortUnplugged,
    uint Volume,
    uint BaseVolume)
{
    /// <summary>Свойство узла или <c>null</c>.</summary>
    internal string? Property(string key) => Properties.TryGetValue(key, out string? value) ? value : null;

    /// <summary>Чем подключено.</summary>
    internal AudioTransport Transport => PulseDeviceRules.TransportOf(this);

    /// <summary>Имя для человека в виде «Порт (Карта)» — см. <see cref="PulseDeviceRules.DisplayName"/>.</summary>
    internal string DisplayName => PulseDeviceRules.DisplayName(this);

    /// <summary>Строка для журнала: имя, шина, форм-фактор, формат, усиление.</summary>
    internal string Summary
    {
        get
        {
            double boost = PulseDeviceRules.BoostAboveBaseDb(Volume, BaseVolume);
            string gain = BaseVolume == 0
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $", громкость {boost:+0;-0;0} дБ к базовой");

            return $"{DisplayName} [{Name}; {Property("device.bus") ?? Property("device.api") ?? "без шины"}, "
                + $"{Property("device.form_factor") ?? "форм-фактор не заявлен"}], "
                + $"{SampleRate} Гц {Channels} кан.{gain}";
        }
    }

    /// <summary>Общий тип — для настроек, маршрута и правил подмены.</summary>
    internal AudioDevice ToAudioDevice() => new(
        Name,
        DisplayName,
        Direction,
        Transport,
        Property("device.bus") ?? Property("device.api") ?? Property("factory.name") ?? string.Empty,
        PortUnplugged ? AudioDeviceAvailability.Unplugged : AudioDeviceAvailability.Active,
        Channels,
        SampleRate,
        FormatIsLive: true);
}
