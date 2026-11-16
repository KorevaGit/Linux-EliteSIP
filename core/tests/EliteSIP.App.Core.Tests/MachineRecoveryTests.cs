using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.Tests;

public sealed class MachineRecoveryTests
{
    private static AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.Panel.Mode = PanelMode.Manual;
        settings.Panel.InstallationID = "machine-test";
        settings.Panel.PresetID = "preset-test";
        settings.Panel.PresetName = "Test";
        settings.Panel.AppliedRevision = 8;
        settings.Panel.SetMachineKey(MachineKeyPair.Generate().PrivateKeyBase64);
        settings.Panel.SetChannelKey("channel-test");
        settings.Account.Username = "707";
        settings.Credentials.SetPassword("sip-test-secret");
        return settings;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handle(request, cancellationToken);
    }

    [Fact]
    public async Task Manual_mode_submits_signed_snapshot_once_and_remembers_acceptance_after_restart()
    {
        var settings = Settings();
        var posts = 0;
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("/api/pair/recovery", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) { return new(HttpStatusCode.NoContent); }
            posts++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var payload = body.RootElement.GetProperty("payload").GetBytesFromBase64();
            using var snapshot = JsonDocument.Parse(payload);
            Assert.Equal("machine-test", snapshot.RootElement.GetProperty("installation_id").GetString());
            Assert.Equal("707", snapshot.RootElement.GetProperty("extension").GetString());
            Assert.Equal(ChannelKey.Hash("channel-test"), snapshot.RootElement.GetProperty("channel_key_hash").GetString());
            var message = Encoding.UTF8.GetBytes("elitesip-recovery-v1\n").Concat(payload).ToArray();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("sip-test-secret"), message)),
                body.RootElement.GetProperty("proofs").GetProperty("707").GetString());
            Assert.DoesNotContain("sip-test-secret", Encoding.UTF8.GetString(payload));
            Assert.DoesNotContain("channel-test", Encoding.UTF8.GetString(payload));
            return new(HttpStatusCode.Accepted);
        });
        using var client = new HttpClient(handler);
        using var recovery = new MachineRecovery(client, new Uri("https://spark.test/"));
        await recovery.CheckAsync(settings, _ => { });
        var directory = Directory.CreateTempSubdirectory("spark-recovery-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            settings.Save(path);
            await recovery.CheckAsync(AppSettings.Load(path), _ => { });
            Assert.Equal(1, posts);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(404, 202, 0)]
    [InlineData(204, 403, 2)]
    [InlineData(204, 409, 2)]
    public async Task Closed_or_rejected_recovery_is_not_marked_accepted(int window, int submission, int expectedPosts)
    {
        var settings = Settings();
        var posts = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) { posts++; }
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)(request.Method == HttpMethod.Get ? window : submission)));
        });
        using var client = new HttpClient(handler);
        using var recovery = new MachineRecovery(client, new Uri("https://spark.test/"));
        await recovery.CheckAsync(settings, _ => { });
        await recovery.CheckAsync(settings, _ => { });
        Assert.Null(settings.Panel.RecoverySubmittedInstallationID);
        Assert.Equal(expectedPosts, posts);
    }

    [Fact]
    public async Task Old_binding_response_cannot_mark_new_binding_as_accepted()
    {
        var settings = Settings();
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) { settings.Panel.SetChannelKey("new-key"); }
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.NoContent : HttpStatusCode.Accepted));
        });
        using var client = new HttpClient(handler);
        using var recovery = new MachineRecovery(client, new Uri("https://spark.test/"));
        await recovery.CheckAsync(settings, _ => { });
        Assert.Null(settings.Panel.RecoverySubmittedInstallationID);
    }

    [Fact]
    public async Task Dispose_cancels_pending_recovery_without_recording_acceptance()
    {
        var settings = Settings();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.Accepted);
        });
        using var client = new HttpClient(handler);
        using var recovery = new MachineRecovery(client, new Uri("https://spark.test/"));
        var sending = recovery.CheckAsync(settings, _ => { });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        recovery.Dispose();
        await sending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(settings.Panel.RecoverySubmittedInstallationID);
    }
    [Fact]
    public async Task Unsupported_tcp_snapshot_is_not_misrepresented_as_udp()
    {
        var settings = Settings();
        settings.Pbx.Transport = SipTransport.Tcp;
        var requests = 0;
        using var handler = new Handler((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); });
        using var client = new HttpClient(handler);
        using var recovery = new MachineRecovery(client, new Uri("https://spark.test/"));
        await recovery.CheckAsync(settings, _ => { });
        Assert.Equal(0, requests);
        Assert.Null(settings.Panel.RecoverySubmittedInstallationID);
    }
}
