using System.Net;
using System.Net.Sockets;

namespace EliteSIP.SipCore.Tests;

public sealed class PortKnockRecoveryTests
{
    [Fact]
    public async Task Concurrent_recovery_sequences_do_not_interleave_packets()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<int>();
        var sequence = new PortKnockSequence { Steps = [new(228), new(126)], SpacingSeconds = 0.001 };
        using var knocker = new PortKnocker("127.0.0.1", sequence, _ => { })
        {
            PacketSender = async (_, payload) =>
            {
                sent.Add(payload);
                if (sent.Count == 1) { entered.SetResult(); await release.Task; }
                return true;
            },
        };
        var first = knocker.OpenPathAsync(SipPathOpenReason.Recovery);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = knocker.OpenPathAsync(SipPathOpenReason.Recovery);
        try { Assert.Single(sent); Assert.False(second.IsCompleted); }
        finally { release.SetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal([228, 126, 228, 126], sent);
    }

    [Fact]
    public async Task Failed_send_does_not_suppress_next_periodic_attempt()
    {
        var calls = 0;
        using var knocker = new PortKnocker("127.0.0.1", new PortKnockSequence { Steps = [new(228)] }, _ => { })
        {
            PacketSender = (_, _) => Task.FromResult(Interlocked.Increment(ref calls) > 1),
        };
        await knocker.OpenPathAsync(SipPathOpenReason.Periodic);
        await knocker.OpenPathAsync(SipPathOpenReason.Periodic);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task All_dns_targets_are_resolved_before_first_packet()
    {
        var echo = new RecordingEcho();
        var sequence = new PortKnockSequence { Steps = [new(228), new(126, "missing.test")] };
        using var knocker = new PortKnocker("127.0.0.1", sequence, _ => { }, echo: echo)
        {
            ResolveHost = (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
        };
        await knocker.OpenPathAsync(SipPathOpenReason.Recovery);
        Assert.Equal(0, echo.Sent);
    }

    [Fact]
    public async Task Echo_wait_is_included_in_spacing_instead_of_added_to_it()
    {
        var clock = new CompletedEchoClock();
        using var knocker = new PortKnocker("127.0.0.1", new PortKnockSequence { Steps = [new(228, count: 2)], SpacingSeconds = 1 }, _ => { }, clock)
        {
            PacketSender = (_, _) => { clock.Advance(); return Task.FromResult(true); },
        };
        await knocker.OpenPathAsync(SipPathOpenReason.Recovery);
        Assert.Equal(2, clock.Sends);
    }

    [Fact]
    public async Task Dispose_cancels_active_and_waiting_sequences_before_disposing_sender()
    {
        var echo = new CancellableEcho();
        using var knocker = new PortKnocker("127.0.0.1", new PortKnockSequence { Steps = [new(228)] }, _ => { }, echo: echo);
        var active = knocker.OpenPathAsync(SipPathOpenReason.Recovery);
        await echo.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var waiting = knocker.OpenPathAsync(SipPathOpenReason.Recovery);
        knocker.Dispose();
        await Task.WhenAll(active, waiting).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(echo.Disposed);
    }

    private sealed class CancellableEcho : IIcmpEcho
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public Task SendAsync(string host, int payloadBytes, TimeSpan timeout) => throw new NotSupportedException();
        public Task SendAsync(string host, int payloadBytes, TimeSpan timeout, CancellationToken token)
        {
            Started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingEcho : IIcmpEcho
    {
        public int Sent { get; private set; }
        public Task SendAsync(string host, int payloadBytes, TimeSpan timeout) { Sent++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class CompletedEchoClock : TimeProvider
    {
        public int Sends { get; private set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Sends * 1000L;
        public void Advance() => Sends++;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("межпакетная пауза уже прошла в ожидании эха");
    }
}
