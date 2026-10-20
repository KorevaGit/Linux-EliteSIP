using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteSIP.Audio.Pulse.Native;

namespace EliteSIP.Audio.Pulse;

/// <summary>
/// Что сейчас есть у звукового сервера: входы, выходы и устройства по
/// умолчанию — одним снимком.
/// </summary>
///
/// <remarks>
/// Снимком, а не отдельными вопросами, потому что сборке тракта нужно всё
/// сразу и согласованно: «системный микрофон» и «список микрофонов», снятые в
/// разные моменты, могут разойтись ровно тогда, когда гарнитуру втыкают.
/// </remarks>
internal sealed record PulseSnapshot(
    IReadOnlyList<PulseDevice> Sources,
    IReadOnlyList<PulseDevice> Sinks,
    string? DefaultSource,
    string? DefaultSink)
{
    internal static PulseSnapshot Empty { get; } = new([], [], null, null);

    internal IReadOnlyList<PulseDevice> Of(AudioDeviceDirection direction) =>
        direction == AudioDeviceDirection.Capture ? Sources : Sinks;

    internal PulseDevice? Find(string? name, AudioDeviceDirection direction) =>
        string.IsNullOrEmpty(name)
            ? null
            : Of(direction).FirstOrDefault(device => string.Equals(device.Name, name, StringComparison.Ordinal));

    internal PulseDevice? Default(AudioDeviceDirection direction) =>
        Find(direction == AudioDeviceDirection.Capture ? DefaultSource : DefaultSink, direction);

    /// <summary>Микрофоны, на которых можно говорить: без мониторов выходов.</summary>
    internal IEnumerable<PulseDevice> UsableInputs => Sources.Where(PulseDeviceRules.IsUsableInput);
}

/// <summary>Запросы к серверу поверх открытого подключения.</summary>
///
/// <remarks>
/// Ответ libpulse приходит обратными вызовами на поток цикла, по одному на
/// устройство и последний — с признаком конца. Здесь вызывающий ждёт под
/// замком цикла, пока операция не перестанет идти: обратные вызовы будят его
/// сигналом цикла. Пропадёт сервер — операция отменится, и ожидание кончится
/// так же.
/// </remarks>
internal static unsafe class PulseIntrospection
{
    /// <summary>Снимает всё одним проходом. Бросает, если сервер ответил отказом.</summary>
    internal static PulseSnapshot Snapshot(PulseConnection connection)
    {
        var collector = new Collector(connection.Loop);
        GCHandle handle = GCHandle.Alloc(collector);
        try
        {
            nint userdata = GCHandle.ToIntPtr(handle);
            using (connection.Lock())
            {
                Run(connection, LibPulse.ContextGetServerInfo(connection.Context, &OnServerInfo, userdata));
                Run(connection, LibPulse.ContextGetSourceInfoList(connection.Context, &OnSourceInfo, userdata));
                Run(connection, LibPulse.ContextGetSinkInfoList(connection.Context, &OnSinkInfo, userdata));
            }

            return new PulseSnapshot(collector.Sources, collector.Sinks, collector.DefaultSource, collector.DefaultSink);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void Run(PulseConnection connection, nint operation)
    {
        if (operation == 0)
        {
            throw new InvalidOperationException(
                "сервер звука не принял запрос: " + LibPulse.StrError(LibPulse.ContextErrno(connection.Context)));
        }

        try
        {
            while (LibPulse.OperationGetState(operation) == LibPulse.OperationRunning)
            {
                connection.Wait();
            }
        }
        finally
        {
            LibPulse.OperationUnref(operation);
        }
    }

    private static Collector Of(nint userdata) => (Collector)GCHandle.FromIntPtr(userdata).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnServerInfo(nint context, LibPulse.ServerInfo* info, nint userdata)
    {
        Collector collector = Of(userdata);
        if (info is not null)
        {
            collector.DefaultSink = LibPulse.Utf8(info->DefaultSinkName);
            collector.DefaultSource = LibPulse.Utf8(info->DefaultSourceName);
        }

        LibPulse.ThreadedMainloopSignal(collector.Loop, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSourceInfo(nint context, LibPulse.DeviceInfo* info, int eol, nint userdata)
    {
        Collector collector = Of(userdata);
        if (eol == 0 && info is not null)
        {
            collector.Sources.Add(Read(info, AudioDeviceDirection.Capture));
        }

        LibPulse.ThreadedMainloopSignal(collector.Loop, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSinkInfo(nint context, LibPulse.DeviceInfo* info, int eol, nint userdata)
    {
        Collector collector = Of(userdata);
        if (eol == 0 && info is not null)
        {
            collector.Sinks.Add(Read(info, AudioDeviceDirection.Render));
        }

        LibPulse.ThreadedMainloopSignal(collector.Loop, 0);
    }

    /// <summary>
    /// Копирует описание из памяти libpulse. Указатели в ней живут только до
    /// выхода из обратного вызова, поэтому всё переносится в управляемые строки
    /// здесь же.
    /// </summary>
    private static PulseDevice Read(LibPulse.DeviceInfo* info, AudioDeviceDirection direction)
    {
        int channels = Math.Min((int)info->ChannelMap.Channels, LibPulse.ChannelsMax);
        int[] positions = new int[channels];
        for (int i = 0; i < channels; i++)
        {
            positions[i] = info->ChannelMap.Map[i];
        }

        int volumeChannels = Math.Min((int)info->Volume.Channels, LibPulse.ChannelsMax);
        ulong volumeSum = 0;
        for (int i = 0; i < volumeChannels; i++)
        {
            volumeSum += info->Volume.Values[i];
        }

        LibPulse.PortInfo* port = info->ActivePort;

        return new PulseDevice(
            Name: LibPulse.Utf8(info->Name) ?? string.Empty,
            Description: LibPulse.Utf8(info->Description) ?? string.Empty,
            Direction: direction,
            Properties: ReadProperties(info->Proplist),
            Channels: info->SampleSpec.Channels,
            SampleRate: (int)info->SampleSpec.Rate,
            ChannelPositions: positions,
            IsMonitor: direction == AudioDeviceDirection.Capture && info->MonitorOfSink != LibPulse.InvalidIndex,
            PortName: port is null ? null : LibPulse.Utf8(port->Name),
            PortDescription: port is null ? null : LibPulse.Utf8(port->Description),
            PortUnplugged: port is not null && port->Available == LibPulse.PortAvailableNo,
            Volume: volumeChannels == 0 ? 0 : (uint)(volumeSum / (ulong)volumeChannels),
            BaseVolume: info->BaseVolume);
    }

    private static Dictionary<string, string> ReadProperties(nint list)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list == 0)
        {
            return properties;
        }

        nint state = 0;
        while (LibPulse.Utf8(LibPulse.ProplistIterate(list, &state)) is string key)
        {
            if (LibPulse.Utf8(LibPulse.ProplistGetsRaw(list, key)) is string value)
            {
                properties[key] = value;
            }
        }

        return properties;
    }

    private sealed class Collector(nint loop)
    {
        internal nint Loop { get; } = loop;

        internal List<PulseDevice> Sources { get; } = [];

        internal List<PulseDevice> Sinks { get; } = [];

        internal string? DefaultSource { get; set; }

        internal string? DefaultSink { get; set; }
    }
}
