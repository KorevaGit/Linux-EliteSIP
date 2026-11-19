using System.Reflection;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Platform;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Core.Tests;

public sealed class UpdateOfferTests
{
    [Fact]
    public void Deferral_survives_reload_and_applies_only_to_the_same_release()
    {
        var path = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings();
            using (var service = CreateService(settings, () => false, new Handoff()))
            {
                service.AskToInstall = _ => false;
                service.Offer();
            }

            settings.Save(path);
            var reloaded = AppSettings.Load(path);
            var handoff = new Handoff();
            using var restarted = CreateService(reloaded, () => false, handoff);
            var offers = 0;
            restarted.AskToInstall = _ => { offers++; return false; };

            restarted.Offer();
            Assert.Equal(0, offers);
            restarted.Offer(userInitiated: true);
            Assert.Equal(1, offers);

            SetVersion(restarted, new Version(9, 0, 1));
            restarted.Offer();
            Assert.Equal(2, offers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Busy_line_blocks_offer_and_blocks_install_if_a_call_arrives_in_the_dialog()
    {
        var handoff = new Handoff();
        var busy = true;
        var ui = new Ui();
        using var service = CreateService(new AppSettings(), () => busy, handoff, ui);
        var offers = 0;
        service.AskToInstall = _ => { offers++; busy = true; return true; };

        service.Offer(userInitiated: true);
        Assert.Equal(0, offers);
        busy = false;
        ui.Timers[1].Fire();
        Assert.Equal(1, offers);
        Assert.Equal(0, handoff.Requests);

        busy = false;
        service.AskToInstall = _ => true;
        ui.Timers[1].Fire();
        Assert.Equal(1, handoff.Requests);
    }

    private static UpdateService CreateService(AppSettings settings, Func<bool> busy, Handoff handoff, Ui? ui = null)
    {
        var service = new UpdateService(busy, _ => { }, () => { }, ui ?? new Ui(), handoff, settings.Maintenance, _ => { });
        SetVersion(service, new Version(9, 0, 0));
        typeof(UpdateService).GetField("_readyInstaller", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, "installer.deb");
        return service;
    }

    private static void SetVersion(UpdateService service, Version version) =>
        typeof(UpdateService).GetProperty("ReadyVersion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, version);

    private sealed class Ui : IUiThread
    {
        public List<Timer> Timers { get; } = [];
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }

        public IUiTimer CreateTimer(TimeSpan interval)
        {
            var timer = new Timer { Interval = interval };
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class Timer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;

        internal void Fire()
        {
            Assert.True(IsEnabled);
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Handoff : IUpdateHandoff
    {
        public string UpdatesDirectory => Path.GetTempPath();
        public int Requests { get; private set; }
        public bool Request(Version version, Action<string> log) { Requests++; return true; }
        public bool Trigger(Action<string> log) => false;
    }
}
