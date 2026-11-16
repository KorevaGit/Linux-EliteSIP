using NAudio.Wave;
namespace EliteSIP.App;
/// <summary>Finite playback of the copied self-test samples, without a temporary file.</summary>
internal sealed class RecordingSamples(short[] samples, int sampleRate) : ISampleProvider
{
    private int _position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    public int Read(Span<float> buffer)
    {
        var count = Math.Min(buffer.Length, samples.Length - _position);
        for (var i=0;i<count;i++) { buffer[i]=samples[_position+i]/32768f; }
        _position+=count; return count;
    }
}
