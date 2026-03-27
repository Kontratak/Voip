using NAudio.Wave;

namespace Voip.Client.Core.Audio;

public sealed class AudioPlayback : IDisposable
{
    private readonly BufferedWaveProvider buffer;
    private readonly WaveOutEvent output;

    public AudioPlayback()
    {
        buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
        {
            DiscardOnBufferOverflow = true
        };

        output = new WaveOutEvent();
        output.Init(buffer);
        output.Play();
    }

    public void Play(byte[] data)
    {
        buffer.AddSamples(data, 0, data.Length);
    }

    public void Dispose()
    {
        output.Stop();
        output.Dispose();
    }
}
