using Concentus;
using Concentus.Enums;
using Ripcord.Media.Audio;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Validates the Concentus-backed Opus decode wrapper against a real Opus frame (encode → decode round-trip),
/// so any Concentus API/version mismatch is caught off-device rather than at the app build.
/// </summary>
public class OpusAudioDecoderTests
{
    [Fact]
    public void RoundTrip_DecodesFrameToNonSilentPcm()
    {
        const int rate = OpusAudioDecoder.SampleRate; // 48000
        const int channels = 2;
        const int frameSamples = 480; // 10 ms @ 48 kHz — the PS5 audio frame size

        // Encode a 440 Hz stereo tone into one Opus packet.
        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(rate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        var input = new short[frameSamples * channels];
        for (int n = 0; n < frameSamples; n++)
        {
            short s = (short)(Math.Sin(2.0 * Math.PI * 440.0 * n / rate) * 8000.0);
            input[n * channels] = s;
            input[n * channels + 1] = s;
        }

        var packet = new byte[4000];
        int packetLength = encoder.Encode(input.AsSpan(), frameSamples, packet.AsSpan(), packet.Length);
        Assert.True(packetLength > 0);

        // Decode it back through our wrapper.
        var decoder = new OpusAudioDecoder(channels);
        var pcm = new float[5760 * channels];
        int decodedPerChannel = decoder.Decode(packet.AsSpan(0, packetLength), pcm);

        Assert.Equal(frameSamples, decodedPerChannel);

        float peak = 0f;
        for (int i = 0; i < decodedPerChannel * channels; i++)
        {
            peak = Math.Max(peak, Math.Abs(pcm[i]));
        }
        Assert.True(peak > 0.05f, $"decoded audio should be audible (peak={peak})");
    }

    [Fact]
    public void RejectsUnsupportedChannelCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusAudioDecoder(3));
    }

    [Fact]
    public void DecodesCeltFullbandStereoFrame()
    {
        // The console encodes CELT-only fullband (TOC config 30 = 0xF4). RESTRICTED_LOWDELAY forces CELT-only,
        // so this exercises the exact decode path the live stream uses (which the tone round-trip above did not).
        const int rate = 48000, channels = 2, frameSamples = 480;
        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(rate, channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = 128000;

        var input = new short[frameSamples * channels];
        for (int n = 0; n < frameSamples; n++)
        {
            double t = n / (double)rate;
            double v = 0.3 * Math.Sin(2 * Math.PI * 440 * t)
                     + 0.2 * Math.Sin(2 * Math.PI * 3000 * t)
                     + 0.1 * Math.Sin(2 * Math.PI * 10000 * t);
            short s = (short)(v * 20000);
            input[n * channels] = s;
            input[n * channels + 1] = s;
        }

        var packet = new byte[4000];
        int len = encoder.Encode(input.AsSpan(), frameSamples, packet.AsSpan(), packet.Length);
        Assert.True(len > 0);
        int config = packet[0] >> 3;

        var decoder = new OpusAudioDecoder(channels);
        var pcm = new float[5760 * channels];
        int spc = decoder.Decode(packet.AsSpan(0, len), pcm);

        float peak = 0f;
        for (int i = 0; i < spc * channels; i++)
        {
            peak = Math.Max(peak, Math.Abs(pcm[i]));
        }

        Assert.True(peak > 0.05f, $"CELT decode too quiet: config={config} toc=0x{packet[0]:X2} spc={spc} peak={peak:F4}");
    }
}
