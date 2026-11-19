using EliteSIP.Audio;
using Xunit;

namespace EliteSIP.Audio.Pulse.Tests;

public sealed class ReceiveOnlyTests
{
    [Fact]
    public void Silent_packet_timing_carries_partial_playback_periods()
    {
        var clock = new ReceiveOnlyClock(48_000, 20);

        Assert.Equal(0, clock.Advance(240));
        Assert.Equal(1, clock.Advance(960));
        Assert.Equal(2, clock.Advance(1_680));
        Assert.Equal(0, clock.Advance(0));
    }

    [Fact]
    public void New_capture_device_triggers_recovery_only_when_capture_is_missing()
    {
        var added = new AudioDeviceChange(AudioDeviceChangeKind.DeviceAdded, "mic",
            AudioDeviceDirection.Capture, null);
        var playbackOnly = new AudioRouteBinding(null, null, null, "sink");

        Assert.True(PulseVoiceAudioEngine.NeedsRouteRebuild(added, playbackOnly));
        Assert.False(PulseVoiceAudioEngine.NeedsRouteRebuild(added, playbackOnly with { ActiveInputId = "other" }));
        Assert.False(PulseVoiceAudioEngine.NeedsRouteRebuild(added, AudioRouteBinding.None));
    }
}
