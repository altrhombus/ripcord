using Concentus;

namespace Ripcord.Media.Audio;

/// <summary>
/// Decodes the console's Opus audio frames to interleaved float PCM at 48 kHz (the stream's native rate).
/// One <see cref="Decode"/> call per source unit emitted by the demuxer. Uses Concentus (pure-managed Opus)
/// so there's no native dependency and it's testable off-device; a fixed scratch buffer avoids per-frame GC.
/// </summary>
public sealed class OpusAudioDecoder
{
    /// <summary>The Opus stream sample rate for PS5 Remote Play (always 48 kHz).</summary>
    public const int SampleRate = 48000;

    /// <summary>Largest Opus frame (120 ms @ 48 kHz) — the decode output buffer is sized to this.</summary>
    private const int MaxFrameSamples = 5760;

    private readonly IOpusDecoder _decoder;
    private readonly short[] _scratch;

    public OpusAudioDecoder(int channels = 2)
    {
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Only mono or stereo is supported.");
        }

        Channels = channels;
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, channels);
        _scratch = new short[MaxFrameSamples * channels];
    }

    public int Channels { get; }

    /// <summary>
    /// Decode one Opus frame into <paramref name="pcm"/> as interleaved float samples in [-1, 1]. Returns the
    /// number of samples per channel decoded (so the caller uses <c>result * Channels</c> floats). <paramref name="pcm"/>
    /// must hold at least <see cref="MaxFrameSamples"/> * <see cref="Channels"/> floats.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> opusFrame, float[] pcm)
    {
        int samplesPerChannel = _decoder.Decode(opusFrame, _scratch.AsSpan(), MaxFrameSamples, false);
        int total = samplesPerChannel * Channels;
        for (int i = 0; i < total; i++)
        {
            pcm[i] = _scratch[i] * (1.0f / 32768.0f);
        }

        return samplesPerChannel;
    }
}
