using Concentus.Enums;
using Concentus.Structs;

namespace Voip.Client.Core.Audio;

public sealed class OpusCodec
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameMilliseconds = 20;
    public const int SamplesPerFrame = SampleRate / 1000 * FrameMilliseconds;

    private readonly OpusEncoder encoder;
    private readonly OpusDecoder decoder;

    public OpusCodec()
    {
        encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = 16000,
            Complexity = 5,
            SignalType = OpusSignal.OPUS_SIGNAL_VOICE,
            UseInbandFEC = true,
            PacketLossPercent = 10
        };

        decoder = new OpusDecoder(SampleRate, Channels);
    }

    public byte[] Encode(byte[] pcm)
    {
        var pcmSamples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, pcmSamples, 0, pcm.Length);

        var output = new byte[4000];
        var length = encoder.Encode(pcmSamples, pcmSamples.Length, output, output.Length);

        var result = new byte[length];
        Array.Copy(output, result, length);
        return result;
    }

    public byte[] Decode(byte[] data)
    {
        var pcm = new short[SamplesPerFrame];
        var samples = decoder.Decode(data, pcm, pcm.Length, false);

        var buffer = new byte[samples * 2];
        Buffer.BlockCopy(pcm, 0, buffer, 0, buffer.Length);
        return buffer;
    }
}
