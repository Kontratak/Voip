using Concentus.Enums;
using Concentus.Structs;

namespace Voip.Client.Core.Audio;

public sealed class OpusCodec
{
    private readonly OpusEncoder encoder;
    private readonly OpusDecoder decoder;

    public OpusCodec()
    {
        encoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        decoder = new OpusDecoder(48000, 1);
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
        var pcm = new short[1920];
        var samples = decoder.Decode(data, pcm, pcm.Length, false);

        var buffer = new byte[samples * 2];
        Buffer.BlockCopy(pcm, 0, buffer, 0, buffer.Length);
        return buffer;
    }
}
