using System.IO;
using System.Reflection;
using EliteSIP.Audio;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using EliteSIP.App.Platform;
using EliteSIP.Platform.Linux;
using EliteSIP.App.Incoming;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Shell;
using EliteSIP.CallHistory;
using EliteSIP.SipCore;

namespace EliteSIP.App.Tests;

public sealed class NetworkRecoveryTests
{
    [AvaloniaTheory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    public async Task Закрытие_канала_переподключает_телефон_и_возвращает_исходящие(int queuedConnections, bool busy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new AppSettings();
        settings.Account.Username = "707";
        settings.Account.Domain = "127.0.0.1";
        settings.Pbx.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        settings.Pbx.Transport = Settings.SipTransport.Tcp;
        settings.Credentials.SetPassword("test");
        var path = Path.Combine(Path.GetTempPath(), $"network-recovery-{Guid.NewGuid():N}.db");
        using var history = new CallHistoryStore(new(path));
        var panel = new PanelViewModel();
        PhoneService phone = null!;
        Task startup = Task.CompletedTask;

        Run(() =>
        {
            phone = new PhoneService(settings, panel, history,
                new IncomingCallPresenter(_ => { }, () => null), new AvaloniaUiThread(), new TestAudioPlatform { EngineFactory = _ => new SilentEngine() }, _ => { });
            var app = new App();
            const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(App).GetField("_panel", instancePrivate)!.SetValue(app, panel);
            typeof(App).GetField("_phone", instancePrivate)!.SetValue(app, phone);
            var reconnect = typeof(App).GetMethod("Reconnect", instancePrivate)!.CreateDelegate<Action<string>>(app);
            phone.RecoverConnection = () => reconnect("канал закрыт");
            var gate = (SemaphoreSlim)typeof(PhoneService).GetField("_connection", instancePrivate)!.GetValue(phone)!;
            Assert.True(gate.Wait(0));
            try { startup = Task.WhenAll(Enumerable.Range(0, queuedConnections).Select(_ => phone.ConnectAsync())); }
            finally { gate.Release(); }
        });

        try
        {
            await startup.WaitAsync(token);
            using (var first = await listener.AcceptTcpClientAsync(token))
            {
                await RespondAsync(first.GetStream(), SipMethod.Register, 200, token);
                await WaitForDialAsync(panel, token);
                Run(() =>
                {
                    Assert.False(phone.HasLocalRouteChanged());
                    if (busy)
                    {
                        panel.PendingNumber = "709";
                        Assert.True(panel.IsInCall);
                    }
                });
            }

            using var second = await listener.AcceptTcpClientAsync(token);
            var stream = second.GetStream();
            await RespondAsync(stream, SipMethod.Register, 200, token);
            await WaitForDialAsync(panel, token);

            Task dialing = Task.CompletedTask;
            Run(() => dialing = phone.PlaceCallAsync("709"));
            await RespondAsync(stream, SipMethod.Invite, 486, token);
            await dialing.WaitAsync(token);
        }
        finally
        {
            Run(phone.Dispose);
            history.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Обновление_линий_сохраняет_модель_и_не_сигналит_ложный_простой()
    {
        var path = Path.Combine(Path.GetTempPath(), $"line-refresh-{Guid.NewGuid():N}.db");
        try
        {
            using var history = new CallHistoryStore(new(path));
            var panel = new PanelViewModel();
            using var phone = new PhoneService(new AppSettings(), panel, history,
                new IncomingCallPresenter(_ => { }, () => null), new AvaloniaUiThread(), new TestAudioPlatform(), _ => { });
            const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
            var records = (Dictionary<string, CallRecord>)typeof(PhoneService)
                .GetField("_records", instancePrivate)!.GetValue(phone)!;
            var sync = typeof(PhoneService).GetMethod("SyncLines", instancePrivate)!.CreateDelegate<Action>(phone);
            records.Add("one", Answered("one", "701"));
            sync();
            var first = Assert.Single(panel.Lines);

            var idle = false;
            panel.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(PanelViewModel.IsInCall) && !panel.IsInCall)
                {
                    idle = true;
                }
            };

            records.Add("two", Answered("two", "702"));
            sync();
            Assert.Same(first, panel.Lines[0]);
            Assert.Equal("two", panel.ActiveLine?.CallId);

            records.Remove("one");
            sync();
            Assert.Equal("two", Assert.Single(panel.Lines).CallId);
            Assert.False(idle);

            records.Clear();
            sync();
            Assert.Empty(panel.Lines);
            Assert.True(idle);
        }
        finally
        {
            File.Delete(path);
        }

        static CallRecord Answered(string id, string number) => new()
        {
            CallId = id,
            Number = number,
            Direction = CallDirection.Outgoing,
            AnsweredAt = DateTimeOffset.Now,
        };
    }

    private static async Task WaitForDialAsync(PanelViewModel panel, CancellationToken token)
    {
        while (true)
        {
            var ready = false;
            Run(() => ready = panel.CanPlaceCall);
            if (ready)
            {
                return;
            }
            await Task.Delay(20, token);
        }
    }

    private static async Task RespondAsync(NetworkStream stream, SipMethod method, int status, CancellationToken token)
    {
        var framer = new SipMessageFramer();
        var buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token);
            Assert.True(read > 0);
            framer.Append(buffer.AsSpan(0, read));
            while (framer.NextMessageData() is { } message)
            {
                var request = SipParser.Parse(message).AsRequest;
                if (request?.Method != method)
                {
                    continue;
                }

                var headers = new SipHeaders();
                foreach (var name in new[] { SipHeaderName.Via, SipHeaderName.From, SipHeaderName.To, SipHeaderName.CallId, SipHeaderName.CSeq })
                {
                    headers.Append(name, request.Headers.First(name)!);
                }
                var response = new SipResponse(status, status == 200 ? "OK" : "Busy Here", headers);
                await stream.WriteAsync(response.Encoded(), token);
                return;
            }
        }
    }
    private static void Run(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) { action(); }
        else { Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult(); }
    }
    private sealed class SilentEngine : IVoiceAudioEngine
    {
        public AudioLatencySnapshot Latency => default;
        public VoiceAudioHandlers Handlers { get; set; } = VoiceAudioHandlers.None;
        public void Reconfigure(VoiceAudioConfiguration configuration) { }
        public void Apply(VoiceAudioConfiguration configuration) { }
        public void Start() { }
        public void Stop() { }
        public void Restart(string reason) { }
        public void Dispose() { }
    }
}
