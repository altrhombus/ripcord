namespace Ripcord.Media.Audio;

/// <summary>
/// Adapts the decoder's 48 kHz stereo float PCM to the WASAPI endpoint's mix format (sample rate + channel
/// count). The common case (48 kHz stereo device) is a straight passthrough. Rate mismatches use a stateful
/// linear resampler (continuous across calls, so no per-frame click); channel counts are mapped simply
/// (stereo → mono downmix, or stereo into the first two of N with the rest silent). Higher-quality resampling
/// is a later refinement. Stateful — call from a single thread (the audio submit path).
/// </summary>
public sealed class AudioFormatConverter
{
    private const int SourceRate = 48000;
    private const int SourceChannels = 2;

    private readonly int _targetRate;
    private readonly int _targetChannels;
    private readonly bool _passthrough;
    private readonly double _step;      // source samples advanced per output sample
    private readonly float[] _prev = new float[SourceChannels]; // last source sample per channel (resample history)
    private double _offset = 1.0;       // next output position in the [history, buffer...] index space
    private bool _primed;

    // Reusable de-interleave/resample scratch. This runs ~50x/second for the life of a session, so allocating
    // per call handed the GC a steady stream of short-lived arrays on the audio path — and a GC pause there is
    // audible (and also what the 4 MB UDP receive buffer elsewhere exists to survive). Grown on demand, never
    // shrunk. Single-threaded by contract, like the rest of this class.
    private float[] _left = [];
    private float[] _right = [];

    public AudioFormatConverter(int targetRate, int targetChannels)
    {
        if (targetRate <= 0 || targetChannels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRate));
        }

        _targetRate = targetRate;
        _targetChannels = targetChannels;
        _step = (double)SourceRate / targetRate;
        _passthrough = targetRate == SourceRate && targetChannels == SourceChannels;
    }

    /// <summary>
    /// Convert one block of interleaved 48 kHz stereo float samples to interleaved float at the target
    /// format. Returns a newly-allocated buffer sized to the produced sample count.
    /// </summary>
    public float[] Convert(ReadOnlySpan<float> stereo48k, int samplesPerChannel)
    {
        float[] destination = [];
        int written = Convert(stereo48k, samplesPerChannel, ref destination);
        return destination.Length == written ? destination : destination[..written];
    }

    /// <summary>
    /// Allocation-free conversion for the live audio path: writes interleaved target-format samples into
    /// <paramref name="destination"/> (grown via <see cref="Array.Resize"/> only when too small, so steady state
    /// allocates nothing) and returns the number of floats written — i.e. samples-per-channel × target channels.
    /// Pass the same array back on every call.
    /// </summary>
    public int Convert(ReadOnlySpan<float> stereo48k, int samplesPerChannel, ref float[] destination)
    {
        if (_passthrough)
        {
            int count = samplesPerChannel * SourceChannels;
            EnsureCapacity(ref destination, count);
            stereo48k[..count].CopyTo(destination);
            return count;
        }

        // 1. Resample the two source channels (or pass through at 48 kHz) into the reusable stereo scratch.
        int outSamples;
        if (_targetRate == SourceRate)
        {
            outSamples = samplesPerChannel;
            EnsureCapacity(ref _left, outSamples);
            EnsureCapacity(ref _right, outSamples);
            for (int n = 0; n < outSamples; n++)
            {
                _left[n] = stereo48k[n * 2];
                _right[n] = stereo48k[n * 2 + 1];
            }
        }
        else
        {
            outSamples = Resample(stereo48k, samplesPerChannel);
        }

        // 2. Map stereo → target channel layout and interleave into the caller's buffer.
        int total = outSamples * _targetChannels;
        EnsureCapacity(ref destination, total);

        // Channels 2..N-1 must be silent rather than whatever the buffer held last call, now that it is reused.
        if (_targetChannels > SourceChannels)
        {
            destination.AsSpan(0, total).Clear();
        }

        for (int n = 0; n < outSamples; n++)
        {
            float l = _left[n];
            float r = _right[n];
            int baseIdx = n * _targetChannels;
            if (_targetChannels == 1)
            {
                destination[baseIdx] = 0.5f * (l + r);
            }
            else
            {
                destination[baseIdx] = l;
                destination[baseIdx + 1] = r;
                // channels 2..N-1 left silent (front L/R only) — surround upmix is a later refinement
            }
        }

        return total;
    }

    private static void EnsureCapacity(ref float[] buffer, int required)
    {
        if (buffer.Length < required)
        {
            Array.Resize(ref buffer, required);
        }
    }

    /// <summary>Resamples into <see cref="_left"/>/<see cref="_right"/>; returns the sample count produced.</summary>
    private int Resample(ReadOnlySpan<float> stereo48k, int n)
    {
        // Index space S: S[0] = previous last sample (history), S[1..n] = this buffer's samples.
        if (!_primed)
        {
            _prev[0] = stereo48k[0];
            _prev[1] = stereo48k[1];
            _primed = true;
        }

        // Upper bound on outputs this call (grows/shrinks buffers exactly via a list-free two-pass count).
        int capacity = (int)((n - _offset) / _step) + 2;
        if (capacity < 0)
        {
            capacity = 0;
        }

        EnsureCapacity(ref _left, capacity);
        EnsureCapacity(ref _right, capacity);
        int count = 0;

        // Stop strictly before n so S[i+1] stays in-buffer (i+1 <= n); the boundary sample is interpolated on
        // the next call via the carried history.
        double p = _offset;
        while (p < n && count < capacity)
        {
            int i = (int)Math.Floor(p);
            float f = (float)(p - i);
            _left[count] = Sample(stereo48k, i, 0) * (1f - f) + Sample(stereo48k, i + 1, 0) * f;
            _right[count] = Sample(stereo48k, i, 1) * (1f - f) + Sample(stereo48k, i + 1, 1) * f;
            count++;
            p += _step;
        }

        _offset = p - n;                 // rebase the next position: next S[0] = this buffer's last sample
        _prev[0] = stereo48k[(n - 1) * 2];
        _prev[1] = stereo48k[(n - 1) * 2 + 1];
        return count;
    }

    // S[index]: index 0 = history (_prev), index k>=1 = stereo48k[(k-1)].
    private float Sample(ReadOnlySpan<float> stereo48k, int index, int channel)
        => index <= 0 ? _prev[channel] : stereo48k[(index - 1) * 2 + channel];
}
