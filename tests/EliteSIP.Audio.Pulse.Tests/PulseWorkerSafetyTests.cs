#pragma warning disable CA2201 // Проверяем обработку искусственно введённых ошибок устройства и среды.
using System.Reflection;
using System.Runtime.InteropServices;
using EliteSIP.Audio;

namespace EliteSIP.Audio.Pulse.Tests;

[CollectionDefinition("Pulse worker safety", DisableParallelization = true)]
public sealed class PulseWorkerSafetyGroup;

[Collection("Pulse worker safety")]
public sealed class PulseWorkerSafetyTests
{
    [Fact]
    public void Worker_failure_after_route_acknowledgement_is_not_lost()
    {
        using var engine = new PulseVoiceAudioEngine(new VoiceAudioConfiguration());
        using var release = new ManualResetEventSlim();
        using var failed = new ManualResetEventSlim();
        engine.Handlers = new VoiceAudioHandlers { Event = item => { if (item is VoiceAudioEvent.Broken) { failed.Set(); } } };
        var body = new Action(() => { release.Wait(); throw new InvalidOperationException("injected worker fault"); });
        var worker = (Thread)typeof(PulseVoiceAudioEngine).GetMethod("StartThread", Fields)!.Invoke(engine, ["test", body])!;
        engine.AcknowledgeRouteBuild();
        release.Set();
        Assert.True(worker.Join(TimeSpan.FromSeconds(3)));
        Assert.True(failed.IsSet);
    }

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    [Fact]
    public void No_device_during_restart_exhausts_policy_without_escaping_timer()
    {
        var configuration = new VoiceAudioConfiguration
        {
            RestartSettings = new AudioRestartSettings(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)),
        };
        using var engine = new PulseVoiceAudioEngine(configuration) { BuildOverride = () => throw new VoiceAudioException("no microphone after unplug") };
        var events = new List<VoiceAudioEvent>();
        engine.Handlers = new VoiceAudioHandlers { Event = events.Add };
        typeof(PulseVoiceAudioEngine).GetField("_running", Fields)!.SetValue(engine, true);
        engine.Restart("unplug");
        Assert.Single(events, item => item is VoiceAudioEvent.Broken);
    }
    [Fact]
    public void Unexpected_managed_error_in_restart_callback_is_reported_without_escape()
    {
        using var engine = new PulseVoiceAudioEngine(new VoiceAudioConfiguration()) { BuildOverride = () => throw new NullReferenceException("injected restart fault") };
        var events = new List<VoiceAudioEvent>();
        engine.Handlers = new VoiceAudioHandlers { Event = events.Add };
        typeof(PulseVoiceAudioEngine).GetField("_running", Fields)!.SetValue(engine, true);
        typeof(PulseVoiceAudioEngine).GetMethod("RunSupervisedRestart", Fields)!.Invoke(engine, ["device changed"]);
        Assert.Single(events, item => item is VoiceAudioEvent.Broken);
    }
    [Fact]
    public void Loose_cable_flapping_is_not_erased_by_active_notification_and_stops_cleanly()
    {
        using var engine = new PulseVoiceAudioEngine(new VoiceAudioConfiguration());
        var route = typeof(PulseVoiceAudioEngine).GetField("_binding", Fields)!;
        route.SetValue(engine, new AudioRouteBinding("mic", "headset", "mic", "headset"));
        var notify = typeof(PulseVoiceAudioEngine).GetMethod("OnDeviceChanged", Fields)!;
        var unplugged = new AudioDeviceChange(AudioDeviceChangeKind.DeviceStateChanged, "headset", AudioDeviceDirection.Render, AudioDeviceAvailability.Unplugged);
        var active = unplugged with { Availability = AudioDeviceAvailability.Active };
        var scheduler = new PulseManualScheduler();
        var rebuilds = 0;
        using var supervisor = new RestartSupervisor(_ =>
        {
            rebuilds++;
            route.SetValue(engine, new AudioRouteBinding("mic", "headset", "mic", "headset"));
            notify.Invoke(engine, [unplugged with { Kind = AudioDeviceChangeKind.DeviceRemoved }]);
            notify.Invoke(engine, [unplugged]);
            notify.Invoke(engine, [active]);
            engine.AcknowledgeRouteBuild();
            scheduler.Fire();
        }, scheduler.Schedule);
        typeof(PulseVoiceAudioEngine).GetField("_supervisor", Fields)!.SetValue(engine, supervisor);
        for (var cycle = 0; cycle < 100; cycle++)
        {
            notify.Invoke(engine, [unplugged]);
            notify.Invoke(engine, [active]);
            scheduler.Fire();
            scheduler.Fire();
            Assert.Equal(cycle + 1, rebuilds);
            Assert.Equal(0, scheduler.PendingCount);
        }
        notify.Invoke(engine, [unplugged]);
        engine.Stop();
        scheduler.FireEvenIfCancelled();
        Assert.Equal(100, rebuilds);
    }
    [Fact]
    public void Failed_worker_generation_is_not_acknowledged_as_healthy()
    {
        using var engine = new PulseVoiceAudioEngine(new VoiceAudioConfiguration());
        var route = typeof(PulseVoiceAudioEngine).GetField("_binding", Fields)!;
        var binding = new AudioRouteBinding("mic", "headset", "mic", "headset");
        route.SetValue(engine, binding);
        typeof(PulseVoiceAudioEngine).GetMethod("HandleWorkerFailure", Fields)!.Invoke(engine, ["capture", new COMException("invalidated")]);
        engine.AcknowledgeRouteBuild();
        Assert.Same(binding, route.GetValue(engine));
    }
    [Fact]
    public async Task Slow_worker_keeps_resources_alive_until_it_exits_even_after_dispose()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new PulseVoiceAudioEngine(new VoiceAudioConfiguration());
        var thread = new Thread(() => { entered.Set(); release.Wait(); }) { IsBackground = true };
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        typeof(PulseVoiceAudioEngine).GetField("_captureThread", Fields)!.SetValue(engine, thread);
        typeof(PulseVoiceAudioEngine).GetField("_stop", Fields)!.SetValue(engine, new CancellationTokenSource());
        var wake = (EventWaitHandle)typeof(PulseVoiceAudioEngine).GetField("_feedWake", Fields)!.GetValue(engine)!;
        var handle = wake.SafeWaitHandle;
        try
        {
            engine.Dispose();
            Assert.False(handle.IsClosed);
            Assert.NotNull(typeof(PulseVoiceAudioEngine).GetField("_stop", Fields)!.GetValue(engine));
        }
        finally { release.Set(); }
        await Task.Run(() => thread.Join());
        Assert.True(SpinWait.SpinUntil(() => handle.IsClosed, TimeSpan.FromSeconds(3)));
        Assert.Null(typeof(PulseVoiceAudioEngine).GetField("_stop", Fields)!.GetValue(engine));
    }
    [Fact]
    public void Burst_of_device_faults_stops_recovery_instead_of_restarting_forever()
    {
        using var engine = new PulseVoiceAudioEngine(new VoiceAudioConfiguration());
        var events = new List<VoiceAudioEvent>();
        engine.Handlers = new VoiceAudioHandlers { Event = events.Add };
        var handler = typeof(PulseVoiceAudioEngine).GetMethod("HandleWorkerFailure", Fields)!;
        for (var index = 0; index < 4; index++)
        {
            typeof(PulseVoiceAudioEngine).GetField("_binding", Fields)!.SetValue(engine, new AudioRouteBinding(null, null, "mic", "output"));
            handler.Invoke(engine, ["capture", new COMException("invalidated")]);
        }
        Assert.Single(events, item => item is VoiceAudioEvent.Broken);
    }
}

#pragma warning restore CA2201
