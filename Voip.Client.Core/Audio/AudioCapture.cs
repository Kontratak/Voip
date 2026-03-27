using NAudio.Wave;

namespace Voip.Client.Core.Audio;

public sealed class AudioCapture : IDisposable
{
    private WaveInEvent? waveIn;

    public event Action<byte[]>? OnAudioCaptured;

    public void Start()
    {
        if (waveIn is not null)
        {
            return;
        }

        waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            BufferMilliseconds = 20,
            NumberOfBuffers = 3,
            WaveFormat = new WaveFormat(48000, 16, 1)
        };

        waveIn.DataAvailable += (_, e) =>
        {
            var buffer = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
            OnAudioCaptured?.Invoke(buffer);
        };

        waveIn.StartRecording();
    }

    public void Stop()
    {
        if (waveIn is null)
        {
            return;
        }

        waveIn.StopRecording();
        waveIn.Dispose();
        waveIn = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
