namespace EliteSIP.Audio.Pulse;

/// <summary>
/// Правила, по которым свойства звукового сервера становятся решениями продукта.
/// </summary>
///
/// <remarks>
/// <para>
/// Чистые функции, а не методы каталога: ошибка здесь не падает, а тихо
/// включает эхоподавитель на гарнитуре или подменяет микрофон не тем. Проверить
/// это на железе можно только перебором гарнитур, а тестом — сразу.
/// </para>
/// <para>
/// Свойства сняты на машине разработки 20 октября 2026 (<c>docs/L0-RISKS.md</c>,
/// «Свойства устройств») с PipeWire 1.0.5 через <c>pipewire-pulse</c>; у
/// чистого PulseAudio имена ключей те же — их задаёт <c>proplist.h</c>.
/// </para>
/// </remarks>
internal static class PulseDeviceRules
{
    /// <summary>
    /// Усиление сверх базовой громкости, выше которого микрофон считается
    /// перекрученным.
    /// </summary>
    ///
    /// <remarks>
    /// Тридцать децибел — между двумя замерами W-L0А: USB-гарнитура LifeChat на
    /// 100 % стоит на +24 дБ к базовой и звучит чисто, встроенный микрофон
    /// ноутбука на тех же 100 % — на +66 дБ, и первая секунда после открытия
    /// у него целиком в потолке.
    /// </remarks>
    internal const double OverdrivenBoostDb = 30;

    /// <summary>
    /// Чем подключено.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// По шине (<c>device.bus</c>) и API (<c>device.api</c>), а не по
    /// форм-фактору — то же правило, что на Windows с перечислителем: у
    /// проводной USB-гарнитуры и у Bluetooth-гарнитуры форм-фактор одинаковый.
    /// </para>
    /// <para>
    /// Bluetooth-вход — всегда режим связи: в A2DP у гарнитуры микрофона нет
    /// вовсе, и вход появляется у сервера только с профилем HFP/HSP. Выход
    /// различается по профилю.
    /// </para>
    /// <para>
    /// Узел без шины и без драйвера карты — программный: фильтр шумодава,
    /// эхоподавитель сервера, виртуальный кабель. Это роль <c>Virtual</c> на
    /// Windows, и правила подмены молчащего микрофона к нему те же.
    /// </para>
    /// </remarks>
    internal static AudioTransport TransportOf(PulseDevice device)
    {
        string? bus = device.Property("device.bus");
        string? api = device.Property("device.api");

        if (bus == "bluetooth" || api == "bluez5")
        {
            return device.Direction == AudioDeviceDirection.Capture || IsHandsFreeProfile(device)
                ? AudioTransport.BluetoothHandsFree
                : AudioTransport.Bluetooth;
        }

        if (IsHdmi(device))
        {
            return AudioTransport.Hdmi;
        }

        return bus switch
        {
            "usb" => AudioTransport.Usb,
            "pci" or "isa" or "platform" => AudioTransport.BuiltIn,
            null when api is null => AudioTransport.Virtual,
            _ => AudioTransport.Other,
        };
    }

    private static bool IsHandsFreeProfile(PulseDevice device)
    {
        string profile = (device.Property("api.bluez5.profile") ?? device.Property("device.profile.name") ?? string.Empty)
            .ToLowerInvariant();

        return profile.Contains("headset", StringComparison.Ordinal)
            || profile.Contains("hfp", StringComparison.Ordinal)
            || profile.Contains("hsp", StringComparison.Ordinal);
    }

    /// <summary>
    /// Звук по видеовыходу. У ALSA это отдельный профиль и порт карты, а имя
    /// узла несёт <c>hdmi</c> — по нему и опознаётся.
    /// </summary>
    private static bool IsHdmi(PulseDevice device) =>
        device.Name.Contains("hdmi", StringComparison.OrdinalIgnoreCase)
        || (device.PortName?.Contains("hdmi", StringComparison.OrdinalIgnoreCase) ?? false)
        || (device.Property("device.profile.name")?.Contains("hdmi", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Выход без пути до микрофона — наушники или гарнитура: эхоподавитель
    /// не нужен.
    /// </summary>
    ///
    /// <remarks>
    /// Два признака, а не один. Форм-фактор узла — тот же, что на Windows
    /// (<c>headset</c>, <c>headphone</c>, <c>handset</c>). Но у встроенной карты
    /// ноутбука форм-фактор <c>internal</c> и тогда, когда в её гнездо воткнуты
    /// наушники: это видно только по действующему порту
    /// (<c>analog-output-headphones</c>). На Windows эту развилку делал драйвер,
    /// заводя отдельную конечную точку «Наушники».
    /// </remarks>
    internal static bool IsEchoFree(PulseDevice output)
    {
        string? formFactor = output.Property("device.form_factor");
        if (formFactor is "headset" or "headphone" or "handset")
        {
            return true;
        }

        string port = output.PortName ?? string.Empty;
        return port.Contains("headphone", StringComparison.OrdinalIgnoreCase)
            || port.Contains("headset", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Имя для человека в виде Windows: «Микрофон (LifeChat LX-3000 Headset)».
    /// </summary>
    ///
    /// <remarks>
    /// Не косметика. Общие правила (<c>AudioDeviceRules</c>) узнают карту по
    /// тому, что в последних скобках, — так выбирается запасной микрофон той же
    /// карты, что и вывод, и так опознаётся пара «микрофон и наушники одной
    /// гарнитуры». Описание узла у сервера — «Встроенное аудио Аналоговый
    /// стерео», карты в скобках в нём нет. Поэтому имя собирается из подписи
    /// порта и описания карты (<c>device.description</c>); нет порта — из
    /// описания узла.
    /// </remarks>
    internal static string DisplayName(PulseDevice device)
    {
        string? card = device.Property("device.description");
        string? port = device.PortDescription;

        if (!string.IsNullOrWhiteSpace(card) && !string.IsNullOrWhiteSpace(port))
        {
            return $"{port} ({card})";
        }

        if (!string.IsNullOrWhiteSpace(card) && !string.IsNullOrWhiteSpace(device.Description)
            && !device.Description.Contains(card, StringComparison.Ordinal))
        {
            return $"{device.Description} ({card})";
        }

        return string.IsNullOrWhiteSpace(device.Description) ? device.Name : device.Description;
    }

    /// <summary>
    /// На сколько децибел громкость устройства выше базовой.
    /// </summary>
    ///
    /// <remarks>
    /// Шкала громкости сервера кубическая: линейный множитель — это
    /// <c>(v / 100 %)³</c>, то есть 60·lg отношения в децибелах. Так считает и
    /// сам libpulse (<c>pa_sw_volume_to_dB</c>): базовая 8 % даёт те самые
    /// −66 дБ, что показал <c>pactl</c>.
    /// </remarks>
    internal static double BoostAboveBaseDb(uint volume, uint baseVolume) =>
        volume == 0 || baseVolume == 0 ? 0 : 60 * Math.Log10((double)volume / baseVolume);

    /// <summary>
    /// Микрофон и выход — одна гарнитура. Решает общее правило
    /// (<c>AudioDeviceRules.IsHeadsetPair</c>) по имени карты в скобках —
    /// поэтому сюда идут имена в виде <see cref="DisplayName"/>.
    /// </summary>
    internal static bool IsHeadsetPair(PulseDevice input, PulseDevice output) =>
        AudioDeviceRules.IsHeadsetPair(input.Transport, input.DisplayName, output.Transport, output.DisplayName);

    /// <summary>Микрофон годится в разговор: не монитор выхода.</summary>
    internal static bool IsUsableInput(PulseDevice device) =>
        device.Direction == AudioDeviceDirection.Capture
        && !device.IsMonitor
        && device.Property("device.class") != "monitor";

    /// <summary>
    /// Каналы, в которые кладётся голос: передняя пара, а у моно-устройства —
    /// его единственный канал.
    /// </summary>
    ///
    /// <remarks>
    /// То же решение, что на Windows с 1 октября 2026: голос в 7.1-гарнитуре,
    /// разложенный во все восемь каналов, виртуализатор сводит обратно с
    /// задержками, и выходит «робот». Здесь поток открывается раскладкой самого
    /// устройства, чтобы сервер ничего не расширял сам, а молчат все каналы,
    /// кроме этих. Раскладка без передней пары (редкость) — первые два канала.
    /// </remarks>
    internal static bool[] VoicedChannels(IReadOnlyList<int> positions)
    {
        bool[] voiced = new bool[positions.Count];
        bool any = false;

        for (int i = 0; i < positions.Count; i++)
        {
            if (positions[i] is Native.LibPulse.PositionMono
                or Native.LibPulse.PositionFrontLeft
                or Native.LibPulse.PositionFrontRight)
            {
                voiced[i] = true;
                any = true;
            }
        }

        if (!any)
        {
            for (int i = 0; i < Math.Min(2, voiced.Length); i++)
            {
                voiced[i] = true;
            }
        }

        return voiced;
    }
}
