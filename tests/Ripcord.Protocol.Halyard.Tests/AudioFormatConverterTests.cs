using Ripcord.Media.Audio;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The 48 kHz-stereo → device-format adapter: passthrough when the device already matches, stereo→mono
/// downmix, and stateful linear resampling with roughly the right output length across a continuous stream.
/// </summary>
public class AudioFormatConverterTests
{
    private static float[] StereoRamp(int samplesPerChannel, float start, float slope)
    {
        var buf = new float[samplesPerChannel * 2];
        for (int n = 0; n < samplesPerChannel; n++)
        {
            float v = start + slope * n;
            buf[n * 2] = v;
            buf[n * 2 + 1] = v;
        }
        return buf;
    }

    [Fact]
    public void Passthrough_48kStereo_ReturnsIdentity()
    {
        var conv = new AudioFormatConverter(targetRate: 48000, targetChannels: 2);
        float[] input = StereoRamp(480, 0f, 0.001f);
        float[] output = conv.Convert(input, 480);
        Assert.Equal(input.Length, output.Length);
        Assert.Equal(Convert.ToHexString(MemBytes(input)), Convert.ToHexString(MemBytes(output)));
    }

    [Fact]
    public void StereoToMono_Downmixes()
    {
        var conv = new AudioFormatConverter(targetRate: 48000, targetChannels: 1);
        var input = new float[] { 1.0f, 0.0f, 0.5f, 0.5f, -1.0f, 1.0f }; // 3 stereo samples
        float[] output = conv.Convert(input, 3);
        Assert.Equal(3, output.Length);
        Assert.Equal(0.5f, output[0], 3);   // (1 + 0)/2
        Assert.Equal(0.5f, output[1], 3);   // (0.5 + 0.5)/2
        Assert.Equal(0.0f, output[2], 3);   // (-1 + 1)/2
    }

    [Fact]
    public void Downsample_48kTo24k_ProducesAboutHalf()
    {
        var conv = new AudioFormatConverter(targetRate: 24000, targetChannels: 2);
        int total = 0;
        const int frames = 50, perFrame = 480;
        for (int f = 0; f < frames; f++)
        {
            float[] output = conv.Convert(StereoRamp(perFrame, f, 0.0f), perFrame);
            total += output.Length / 2; // samples per channel
            foreach (float s in output)
            {
                Assert.True(float.IsFinite(s));
            }
        }

        int expected = frames * perFrame / 2; // 24000/48000
        Assert.InRange(total, expected - 4, expected + 4);
    }

    [Fact]
    public void Upsample_48kTo96k_ProducesAboutDouble()
    {
        var conv = new AudioFormatConverter(targetRate: 96000, targetChannels: 2);
        int total = 0;
        const int frames = 50, perFrame = 480;
        for (int f = 0; f < frames; f++)
        {
            float[] output = conv.Convert(StereoRamp(perFrame, 0f, 0.0f), perFrame);
            total += output.Length / 2;
        }

        int expected = frames * perFrame * 2; // 96000/48000
        Assert.InRange(total, expected - 4, expected + 4);
    }

    [Theory]
    [InlineData(48000, 2)]  // passthrough
    [InlineData(44100, 2)]  // downsample
    [InlineData(96000, 2)]  // upsample
    [InlineData(48000, 1)]  // stereo -> mono downmix
    [InlineData(48000, 6)]  // stereo -> 5.1 (extra channels silent)
    public void ReusedBufferOverload_MatchesAllocatingOverload(int targetRate, int targetChannels)
    {
        // The live audio path uses the ref-buffer overload to avoid per-frame allocation. It must produce
        // byte-identical output to the simple one across a sequence of frames — the resampler is stateful, so
        // this also proves the shared scratch buffers don't corrupt that state between calls.
        var allocating = new AudioFormatConverter(targetRate, targetChannels);
        var reusing = new AudioFormatConverter(targetRate, targetChannels);

        float[] shared = [];
        const int perFrame = 480;
        for (int f = 0; f < 20; f++)
        {
            // Vary the input per frame so a stale-tail bug can't hide behind identical data.
            ReadOnlySpan<float> input = StereoRamp(perFrame, f * 0.01f, 0.0f);

            float[] expected = allocating.Convert(input, perFrame);
            int written = reusing.Convert(input, perFrame, ref shared);

            Assert.Equal(expected.Length, written);
            Assert.Equal(MemBytes(expected), MemBytes(shared[..written]));
        }
    }

    [Fact]
    public void ReusedBuffer_ShorterFrameAfterLongerFrame_DoesNotLeakStaleTail()
    {
        // The regression this guards: the reused buffer keeps the previous (longer) frame's samples past
        // `written`. A caller that trusted the array length rather than the return value would play them.
        var conv = new AudioFormatConverter(targetRate: 48000, targetChannels: 2);
        float[] shared = [];

        int longWritten = conv.Convert(StereoRamp(960, 0.5f, 0f), 960, ref shared);
        int shortWritten = conv.Convert(StereoRamp(240, 0.5f, 0f), 240, ref shared);

        Assert.Equal(960 * 2, longWritten);
        Assert.Equal(240 * 2, shortWritten);
        // The buffer is still the larger capacity; only the returned count describes valid data.
        Assert.True(shared.Length >= longWritten);
    }

    private static byte[] MemBytes(float[] f)
    {
        var b = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
