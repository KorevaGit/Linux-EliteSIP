using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteSIP.Audio.Pulse.Native;

namespace EliteSIP.Audio.Pulse;

/// <summary>
/// Слежение за устройствами: появились, исчезли, выдернули из гнезда, сменилось
/// умолчание. То же, что <c>AudioDeviceWatch</c> на Windows, и на выходе тот же
/// язык — <see cref="AudioDeviceChange"/>, который читает общий фильтр
/// <see cref="DeviceChangeRelevance"/>.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Сравнение снимков, а не перевод событий.</b> Событие подписки libpulse
/// несёт только номер устройства и вид («новое», «изменилось», «удалено») — ни
/// имени, ни направления у удалённого уже не спросить. А «изменилось» приходит
/// на каждое движение ползунка громкости. Поэтому событие здесь — только повод
/// снять новый снимок и сравнить с прежним по именам; что именно поменялось,
/// решает <see cref="Diff"/>, и это проверяется тестом без сервера.
/// </para>
/// <para>
/// Снимок снимается не на потоке цикла: там ждать ответа сервера нельзя —
/// ответ придёт на тот же поток, и он будет занят ожиданием. Повод уходит в
/// пул, и пачка событий (а при втыкании гарнитуры их десяток) склеивается в
/// один снимок.
/// </para>
/// </remarks>
internal sealed unsafe class PulseDeviceWatch : IDisposable
{
    private readonly Action<AudioDeviceChange> _onChange;
    private readonly PulseConnection _connection;
    private readonly Lock _gate = new();
    private GCHandle _self;
    private PulseSnapshot _last;
    private int _pending;
    private bool _disposed;

    private PulseDeviceWatch(Action<AudioDeviceChange> onChange, PulseConnection connection)
    {
        _onChange = onChange;
        _connection = connection;
        _last = PulseIntrospection.Snapshot(connection);
    }

    /// <summary>Начинает слежение. Бросает, если сервер недоступен.</summary>
    internal static PulseDeviceWatch Start(Action<AudioDeviceChange> onChange)
    {
        PulseConnection connection = PulseConnection.Connect("EliteSIP watch", out string? failure)
            ?? throw new InvalidOperationException("сервер звука недоступен: " + failure);

        PulseDeviceWatch watch;
        try
        {
            watch = new PulseDeviceWatch(onChange, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        watch._self = GCHandle.Alloc(watch);
        using (connection.Lock())
        {
            LibPulse.ContextSetSubscribeCallback(connection.Context, &OnEvent, GCHandle.ToIntPtr(watch._self));
            nint operation = LibPulse.ContextSubscribe(
                connection.Context,
                LibPulse.SubscriptionMaskSink | LibPulse.SubscriptionMaskSource | LibPulse.SubscriptionMaskServer,
                0,
                0);

            if (operation != 0)
            {
                LibPulse.OperationUnref(operation);
            }
        }

        return watch;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        using (_connection.Lock())
        {
            LibPulse.ContextSetSubscribeCallback(_connection.Context, null, 0);
        }

        // Подключение закрывается после снятия обратного вызова: последний
        // вызов, уже стоявший в очереди цикла, тогда не придёт в освобождённое.
        _connection.Dispose();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => Refresh());
    }

    private void Refresh()
    {
        // Небольшая пауза до снимка — собрать пачку: сервер при втыкании
        // гарнитуры шлёт карту, вход, выход и умолчание порознь, за единицы
        // миллисекунд. Это не звуковой контур, здесь ждать можно.
        Thread.Sleep(50);
        Volatile.Write(ref _pending, 0);

        List<AudioDeviceChange> changes;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            PulseSnapshot current;
            try
            {
                current = PulseIntrospection.Snapshot(_connection);
            }
            catch (InvalidOperationException)
            {
                // Сервер ушёл. Тракт узнает об этом сам по своим потокам.
                return;
            }

            changes = Diff(_last, current);
            _last = current;
        }

        foreach (AudioDeviceChange change in changes)
        {
            _onChange(change);
        }
    }

    /// <summary>
    /// Что изменилось между двумя снимками — на общем языке изменений.
    /// </summary>
    ///
    /// <remarks>
    /// Мониторы выходов в сравнение не попадают: говорить в них нельзя, и
    /// их появление вслед за каждым выходом было бы лишним поводом.
    /// </remarks>
    internal static List<AudioDeviceChange> Diff(PulseSnapshot before, PulseSnapshot after)
    {
        var changes = new List<AudioDeviceChange>();

        foreach (AudioDeviceDirection direction in new[] { AudioDeviceDirection.Capture, AudioDeviceDirection.Render })
        {
            Dictionary<string, PulseDevice> old = Relevant(before, direction);
            Dictionary<string, PulseDevice> now = Relevant(after, direction);

            foreach ((string name, PulseDevice device) in now)
            {
                if (!old.TryGetValue(name, out PulseDevice? previous))
                {
                    changes.Add(new AudioDeviceChange(AudioDeviceChangeKind.DeviceAdded, name, direction, null));
                }
                else if (previous.PortUnplugged != device.PortUnplugged || previous.PortName != device.PortName)
                {
                    // Выдернули или воткнули в гнездо: устройство на месте, а
                    // звук с него идёт в другое место или никуда.
                    changes.Add(new AudioDeviceChange(
                        AudioDeviceChangeKind.DeviceStateChanged,
                        name,
                        direction,
                        device.PortUnplugged ? AudioDeviceAvailability.Unplugged : AudioDeviceAvailability.Active));
                }
            }

            foreach (string name in old.Keys.Where(name => !now.ContainsKey(name)))
            {
                changes.Add(new AudioDeviceChange(AudioDeviceChangeKind.DeviceRemoved, name, direction, null));
            }

            string? defaultBefore = direction == AudioDeviceDirection.Capture ? before.DefaultSource : before.DefaultSink;
            string? defaultAfter = direction == AudioDeviceDirection.Capture ? after.DefaultSource : after.DefaultSink;
            if (!string.Equals(defaultBefore, defaultAfter, StringComparison.Ordinal))
            {
                changes.Add(new AudioDeviceChange(
                    AudioDeviceChangeKind.DefaultChanged,
                    defaultAfter ?? string.Empty,
                    direction,
                    null));
            }
        }

        return changes;
    }

    private static Dictionary<string, PulseDevice> Relevant(PulseSnapshot snapshot, AudioDeviceDirection direction)
    {
        IEnumerable<PulseDevice> devices = direction == AudioDeviceDirection.Capture
            ? snapshot.UsableInputs
            : snapshot.Sinks;

        var map = new Dictionary<string, PulseDevice>(StringComparer.Ordinal);
        foreach (PulseDevice device in devices)
        {
            map[device.Name] = device;
        }

        return map;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(nint context, int type, uint index, nint userdata)
    {
        int facility = type & LibPulse.EventFacilityMask;
        if (facility is LibPulse.EventSink or LibPulse.EventSource or LibPulse.EventServer
            && GCHandle.FromIntPtr(userdata).Target is PulseDeviceWatch watch)
        {
            watch.Schedule();
        }
    }
}
