using System.Net;
using System.Net.NetworkInformation;
using EliteSIP.App.PanelLine;
using EliteSIP.Audio;
using EliteSIP.Audio.Pulse;
using EliteSIP.Diagnostics;

namespace EliteSIP.App.Platform;

/// <summary>События, которые нужны поддержке даже при выключенной SIP-трассе.</summary>
internal sealed class RuntimeDiagnostics : IDisposable
{
    private const int HistoryLimit = 256;
    private readonly Lock _gate = new();
    private readonly Queue<string> _history = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string> _log;
    private readonly LogFile _persistent;
    private bool _disposed;
    private readonly Func<(string? Input, string? Output)> _selection;
    private readonly Task _worker;
    private long _lastAudioResponse = System.Diagnostics.Stopwatch.GetTimestamp();
    private string _audioStatus = "Снимок аудиоустройств ещё не получен.";

    internal RuntimeDiagnostics(Action<string> log, Func<(string? Input, string? Output)> selection)
    {
        _log = log;
        _selection = selection;
        var directory = Path.Combine(Path.GetDirectoryName(Settings.AppSettings.DefaultPath)!, "Logs");
        _persistent = new LogFile(new LogFile.Settings(directory, maximumFileBytes: 1024 * 1024, keptFiles: 4));
        var token = _stop.Token;
        _worker = Task.WhenAll(Task.Run(() => WatchNetworkAsync(token)), Task.Run(() => WatchAudioAsync(token)));
    }

    internal string History()
    {
        lock (_gate)
        {
            return _history.Count == 0
                ? "Изменения оборудования и сети за текущий сеанс не обнаружены."
                : string.Join(Environment.NewLine, _history);
        }
    }

    internal string FlushForSupport()
    {
        bool flushed = _persistent.Flush(TimeSpan.FromSeconds(3));
        return $"Журнал диагностики: запись подтверждена={flushed}; {_persistent.DiagnosticSummary()}";
    }

    internal void RecordLocal(string message) => Record(message, audio: null);

    internal string AudioStatus() => Volatile.Read(ref _audioStatus);

    private async Task WatchNetworkAsync(CancellationToken token)
    {
        Dictionary<string, string>? network = null;
        bool awaitingAudio = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do
            {
                var current = Interfaces();
                if (current is not null)
                {
                    if (network is not null)
                    {
                        foreach (string name in network.Keys.Union(current.Keys, StringComparer.Ordinal))
                        {
                            string previous = network.GetValueOrDefault(name, "нет интерфейса");
                            string next = current.GetValueOrDefault(name, "нет интерфейса");
                            if (!string.Equals(previous, next, StringComparison.Ordinal))
                            {
                                Record($"Сеть {name}: {previous} → {next}", audio: false);
                            }
                        }
                    }

                    network = current;
                }

                bool stalled = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAudioResponse))
                    >= TimeSpan.FromSeconds(12);
                if (stalled != awaitingAudio)
                {
                    awaitingAudio = stalled;
                    Record(stalled ? "Звук: снимок аудиоустройств не обновлялся 12 секунд"
                        : "Звук: получение снимков аудиоустройств восстановилось", audio: true, warning: stalled);
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task WatchAudioAsync(CancellationToken token)
    {
        PulseSnapshot? audio = null;
        (string? Input, string? Output)? selection = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do
            {
                var snapshot = PulseDeviceCatalog.Snapshot();
                string inputs = string.Join("; ", snapshot.UsableInputs.Select(Model));
                string outputs = string.Join("; ", snapshot.Sinks.Select(Model));
                Volatile.Write(ref _audioStatus, LogRedaction.Redact(
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}Микрофоны: {inputs}{Environment.NewLine}Выходы: {outputs}"
                    + $"{Environment.NewLine}Микрофон по умолчанию: {Model(snapshot.Default(AudioDeviceDirection.Capture))}"
                    + $"{Environment.NewLine}Выход по умолчанию: {Model(snapshot.Default(AudioDeviceDirection.Render))}"));
                Interlocked.Exchange(ref _lastAudioResponse, System.Diagnostics.Stopwatch.GetTimestamp());

                var selected = _selection();
                if (audio is not null)
                {
                    ReportAudioChanges(audio, snapshot);
                    if (selection is { } previous)
                    {
                        ReportSelection(previous.Input, selected.Input, AudioDeviceDirection.Capture, audio, snapshot);
                        ReportSelection(previous.Output, selected.Output, AudioDeviceDirection.Render, audio, snapshot);
                    }
                }

                audio = snapshot;
                selection = selected;
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            Record($"Звук: каталог устройств недоступен ({error.GetType().Name})", audio: true);
        }
    }

    private static Dictionary<string, string>? Interfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToDictionary(item => item.Name, item =>
                {
                    var properties = item.GetIPProperties();
                    var addresses = properties.UnicastAddresses
                        .Select(entry => entry.Address)
                        .Where(address => !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal)
                        .Select(address => address.ToString())
                        .Order(StringComparer.Ordinal);
                    var gateways = properties.GatewayAddresses.Select(entry => entry.Address.ToString()).Order(StringComparer.Ordinal);
                    return $"{item.OperationalStatus}, IP [{string.Join(", ", addresses)}], шлюз [{string.Join(", ", gateways)}]";
                }, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private void ReportAudioChanges(PulseSnapshot before, PulseSnapshot after)
    {
        foreach (var change in PulseDeviceWatch.Diff(before, after))
        {
            if (change.Direction is not { } direction)
            {
                continue;
            }
            string label = AudioLabel(direction);
            string message = change.Kind switch
            {
                AudioDeviceChangeKind.DefaultChanged =>
                    $"Звук, {label} по умолчанию: {Model(before.Default(direction))} → {Model(after.Default(direction))}",
                AudioDeviceChangeKind.DeviceRemoved =>
                    $"Звук, {label} исчез: {Model(before.Find(change.DeviceId, direction))}",
                AudioDeviceChangeKind.DeviceAdded =>
                    $"Звук, {label} появился: {Model(after.Find(change.DeviceId, direction))}",
                _ => $"Звук, {label}: {Model(before.Find(change.DeviceId, direction))} → {Model(after.Find(change.DeviceId, direction))}",
            };
            Record(message, audio: true, warning: change.Kind == AudioDeviceChangeKind.DeviceRemoved
                || change.Availability == AudioDeviceAvailability.Unplugged);
        }
    }

    private void ReportSelection(string? beforeId, string? afterId, AudioDeviceDirection direction,
        PulseSnapshot before, PulseSnapshot after)
    {
        if (!string.Equals(beforeId, afterId, StringComparison.Ordinal))
        {
            string previous = Model(string.IsNullOrEmpty(beforeId) ? before.Default(direction) : before.Find(beforeId, direction));
            string next = Model(string.IsNullOrEmpty(afterId) ? after.Default(direction) : after.Find(afterId, direction));
            Record($"Звук, {AudioLabel(direction)} выбран в EliteSIP: {previous} → {next}", audio: true, warning: false);
        }
    }

    private static string AudioLabel(AudioDeviceDirection direction)
        => direction == AudioDeviceDirection.Capture ? "микрофон" : "выход";

    private static string Model(PulseDevice? device)
    {
        if (device is null)
        {
            return "нет";
        }

        string name = device.Property("device.product.name") ?? device.Description;
        return $"{name} ({device.PortDescription ?? device.Transport.ToString()}{(device.PortUnplugged ? ", отключён" : "")})";
    }

    private void Record(string message, bool? audio, bool warning = true)
    {
        message = LogRedaction.Redact(message.Replace('\r', ' ').Replace('\n', ' '));
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_history.Count == HistoryLimit)
            {
                _history.Dequeue();
            }

            _history.Enqueue($"{DateTimeOffset.Now:O} {message}");
            _persistent.Write(message, "i");
        }

        if (audio is true)
        {
            _log(message);
            ClientEventLog.Shared.NoteAudio(message, warning);
        }
        else if (audio is false)
        {
            _log(message);
            ClientEventLog.Shared.NoteConnection(message);
        }
    }

    public void Dispose() => Dispose(waitForLog: true);

    internal void Dispose(bool waitForLog)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
        }

        if (waitForLog)
        {
            _persistent.Dispose();
        }
        else
        {
            _persistent.DisposeWithoutWaiting();
        }

        _ = _worker.ContinueWith(static (_, source) => ((CancellationTokenSource)source!).Dispose(),
            _stop, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
