using NAudio.Wave;
using System.Collections.Concurrent;
using Timer = System.Timers.Timer;

namespace Voip.Client.Core.Audio;

public sealed class AudioPlayback : IDisposable
{
    private readonly byte[] silenceFrame = new byte[OpusCodec.SamplesPerFrame * 2];
    private readonly ConcurrentQueue<byte[]> pendingFrames = new();
    private readonly BufferedWaveProvider buffer;
    private readonly WaveOutEvent output;
    private readonly Timer playbackTimer;
    private int bufferedFrames;
    private bool startedConsuming;

    public AudioPlayback()
    {
        buffer = new BufferedWaveProvider(new WaveFormat(OpusCodec.SampleRate, 16, OpusCodec.Channels))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(250)
        };

        output = new WaveOutEvent();
        output.Init(buffer);
        output.Play();

        playbackTimer = new Timer(OpusCodec.FrameMilliseconds)
        {
            AutoReset = true
        };
        playbackTimer.Elapsed += (_, _) => DrainNextFrame();
        playbackTimer.Start();
    }

    public void Play(byte[] data)
    {
        pendingFrames.Enqueue(data);
        Interlocked.Increment(ref bufferedFrames);
    }

    private void DrainNextFrame()
    {
        if (!startedConsuming)
        {
            startedConsuming = Volatile.Read(ref bufferedFrames) >= 3;
            if (!startedConsuming)
            {
                return;
            }
        }

        if (pendingFrames.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref bufferedFrames);
            buffer.AddSamples(frame, 0, frame.Length);
            return;
        }

        if (buffer.BufferedDuration < TimeSpan.FromMilliseconds(OpusCodec.FrameMilliseconds * 2))
        {
            buffer.AddSamples(silenceFrame, 0, silenceFrame.Length);
        }
    }

    public void Dispose()
    {
        playbackTimer.Stop();
        playbackTimer.Dispose();
        output.Stop();
        output.Dispose();
    }
}
