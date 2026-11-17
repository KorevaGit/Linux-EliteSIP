using System.Reflection;
using EliteSIP.Audio;
using EliteSIP.MediaCore;
namespace EliteSIP.Audio.Pulse.Tests;
public sealed class AudioControlPathTests
{
    private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    private delegate void RenderSamples(Span<float> samples);
    private delegate int WriteSamples(ReadOnlySpan<float> samples);
    private delegate void LevelSamples(Span<float> samples,int chunk);
    [Fact]
    public void Real_render_callback_applies_live_playback_volume_zero_one_two()
    {
        using var engine=new PulseVoiceAudioEngine(new VoiceAudioConfiguration());
        var field=typeof(PulseVoiceAudioEngine).GetField("_playbackRing",Fields)!;
        var render=typeof(PulseVoiceAudioEngine).GetMethod("Render",Fields)!.CreateDelegate<RenderSamples>(engine);
        float Read(float volume)
        {
            engine.Apply(new VoiceAudioConfiguration {PlaybackVolume=volume});
            var ring=Activator.CreateInstance(field.FieldType,[4096,480])!;field.SetValue(engine,ring);
            var write=field.FieldType.GetMethod("Write")!.CreateDelegate<WriteSamples>(ring);
            var input=new float[480];Array.Fill(input,.1f);Assert.Equal(480,write(input));
            typeof(PulseVoiceAudioEngine).GetField("_fadePosition",Fields)!.SetValue(engine,10000);
            render(input);return input[400];
        }
        Assert.Equal(0,Read(0));var normal=Read(1);Assert.Equal(.1f,normal,4);Assert.Equal(normal*2,Read(2),4);
    }
    [Fact]
    public void Receive_leveling_is_created_when_enabled_and_bypassed_when_disabled()
    {
        using var engine=new PulseVoiceAudioEngine(new VoiceAudioConfiguration {ReceiveGainControl=false});
        var level=typeof(PulseVoiceAudioEngine).GetMethod("LevelReceived",Fields)!.CreateDelegate<LevelSamples>(engine);
        var samples=new float[480];Array.Fill(samples,.01f);level(samples,480);
        Assert.All(samples,x=>Assert.Equal(.01f,x));
        engine.Apply(new VoiceAudioConfiguration {ReceiveGainControl=true});level(samples,480);
        var field=typeof(PulseVoiceAudioEngine).GetField("_receiveGain",Fields)!;Assert.NotNull(field.GetValue(engine));
        engine.Apply(new VoiceAudioConfiguration {ReceiveGainControl=false});level(samples,480);Assert.Null(field.GetValue(engine));
    }
    [Fact]
    public void Capture_encoder_applies_live_microphone_gain()
    {
        using var engine=new PulseVoiceAudioEngine(new VoiceAudioConfiguration());
        var balance=new SampleBalance(1,160,0);
        typeof(PulseVoiceAudioEngine).GetField("_balance",Fields)!.SetValue(engine,balance);
        var emit=typeof(PulseVoiceAudioEngine).GetMethod("EmitFrames",Fields)!;
        short Read(float gain)
        {
            engine.Apply(new VoiceAudioConfiguration {MicrophoneGain=gain});
            byte[]? sent=null;engine.Handlers=new VoiceAudioHandlers {EncodedFrame=bytes=>sent=bytes.ToArray()};
            var input=new float[160];Array.Fill(input,.1f);
            emit.Invoke(engine,[new AudioFrameEncoder(AudioCodec.Pcmu),input,160,new short[160]]);
            Assert.NotNull(sent);return new AudioFrameDecoder(AudioCodec.Pcmu).Decode(sent)[100];
        }
        Assert.Equal(0,Read(0));var normal=Read(1);Assert.True(normal>0);Assert.InRange((double)Read(2)/normal,1.9,2.1);
    }
    [Fact]
    public void Live_processing_switches_rebuild_real_APM_with_requested_effects()
    {
        var off=new VoiceAudioConfiguration {AutomaticGainControl=false,NoiseSuppression=false,BackgroundVoiceSuppression=false};
        using var engine=new PulseVoiceAudioEngine(off);
        var field=typeof(PulseVoiceAudioEngine).GetField("_processor",Fields)!;
        var type=field.FieldType;
        field.SetValue(engine,Activator.CreateInstance(type,[48000,false,false,true,false]));
        var running=typeof(PulseVoiceAudioEngine).GetField("_running",Fields)!;running.SetValue(engine,true);
        try
        {
            engine.Apply(off with {AutomaticGainControl=true,NoiseSuppression=true,BackgroundVoiceSuppression=true});
            var enabled=field.GetValue(engine)!;
            Assert.NotNull(type.GetProperty("SuppressionLevel")!.GetValue(enabled));
            Assert.Equal(true,type.GetProperty("SuppressesBackgroundVoices")!.GetValue(enabled));
            Assert.NotNull(type.GetField("_gainControl",Fields)!.GetValue(enabled));
            engine.Apply(off);var disabled=field.GetValue(engine)!;Assert.NotSame(enabled,disabled);
            Assert.Null(type.GetProperty("SuppressionLevel")!.GetValue(disabled));
            Assert.Equal(false,type.GetProperty("SuppressesBackgroundVoices")!.GetValue(disabled));
            Assert.Null(type.GetField("_gainControl",Fields)!.GetValue(disabled));
        }
        finally {running.SetValue(engine,false);}
    }
}
