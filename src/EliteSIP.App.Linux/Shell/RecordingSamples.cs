using NAudio.Wave;
namespace EliteSIP.App;
/// <summary>Finite playback of the copied self-test samples, without a temporary file.</summary>
internal sealed class RecordingSamples(short[] samples, int sampleRate, Func<double>? volume = null) : ISampleProvider
{
    private int _position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    public int Read(Span<float> buffer)
    {
        var count = Math.Min(buffer.Length, samples.Length - _position);
        var gain=(float)Math.Clamp(volume?.Invoke() ?? 1,0,2);
        for (var i=0;i<count;i++) { buffer[i]=Math.Clamp(samples[_position+i]/32768f*gain,-1f,1f); }
        _position+=count; return count;
    }
}
