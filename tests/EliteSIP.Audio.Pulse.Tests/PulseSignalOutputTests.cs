using NAudio.Wave;

namespace EliteSIP.Audio.Pulse.Tests;

public class PulseSignalOutputTests
{
    [Fact]
    public void Источник_отдал_меньше_остаток_тишина()
    {
        var target = Filled(8, 7f);

        Assert.True(PulseSignalOutput.Fill(new Fixed(3, 0.5f), target, out var failure));

        Assert.Null(failure);
        Assert.Equal([0.5f, 0.5f, 0.5f, 0, 0, 0, 0, 0], target);
    }

    [Fact]
    public void Источник_бросил_исключение_тишина_а_не_падение()
    {
        // Из обратного вызова libpulse исключение .NET не пропускает — процесс
        // кончился бы сразу. Fill обязан проглотить любое.
        var target = Filled(8, 7f);

        Assert.False(PulseSignalOutput.Fill(new Throwing(new InvalidDataException("битый кадр MP3")), target, out var failure));

        Assert.IsType<InvalidDataException>(failure);
        Assert.All(target, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Ловится_и_не_IOException()
    {
        var target = Filled(4, 1f);

        Assert.False(PulseSignalOutput.Fill(new Throwing(new ArgumentOutOfRangeException("buffer")), target, out var failure));

        Assert.IsType<ArgumentOutOfRangeException>(failure);
    }

    [Fact]
    public void Рингтон_читается_в_память_и_живёт_без_файла()
    {
        // Файл на отмонтированном ресурсе или удалённый посреди звонка не
        // должен доходить до потока цикла libpulse.
        var path = WriteWave(seconds: 1);
        try
        {
            using var reader = PulseSignalSounds.OpenFile(path);
            File.Delete(path);

            var samples = reader.ToSampleProvider();
            var buffer = new float[samples.WaveFormat.SampleRate];
            Assert.Equal(buffer.Length, samples.Read(buffer));
            Assert.Contains(buffer, sample => sample != 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Файл_больше_потолка_не_открывается()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var stream = File.OpenWrite(path))
            {
                stream.SetLength(PulseSignalSounds.MaximumFileBytes + 1);
            }

            // IOException разбирает общий проигрыватель: «рингтон не играет».
            Assert.Throws<IOException>(() => PulseSignalSounds.OpenFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static float[] Filled(int length, float value)
    {
        var buffer = new float[length];
        Array.Fill(buffer, value);
        return buffer;
    }

    private static string WriteWave(int seconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"elitesip-{Guid.NewGuid():N}.wav");
        var format = new WaveFormat(8000, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        for (int i = 0; i < format.SampleRate * seconds; i++)
        {
            writer.WriteSample((float)Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * 0.5f);
        }

        return path;
    }

    private sealed class Fixed(int count, float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);

        public int Read(Span<float> buffer)
        {
            int written = Math.Min(count, buffer.Length);
            buffer[..written].Fill(value);
            return written;
        }
    }

    private sealed class Throwing(Exception error) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);

        public int Read(Span<float> buffer) => throw error;
    }
}
