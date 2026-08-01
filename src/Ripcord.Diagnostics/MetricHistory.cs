namespace Ripcord.Diagnostics;

/// <summary>
/// A fixed-size rolling window of samples for one metric, plus the normalisation needed to plot it.
///
/// <para>
/// Exists because instantaneous readouts hide exactly the behaviour that matters most: a stream that alternates
/// between "healthy" and "below target" several times a second shows up as a flickering verdict and a number
/// that will not sit still, which tells you something is wrong but nothing about what. A short history makes
/// oscillation, spikes and slow drift visually distinguishable.
/// </para>
///
/// <para>Pure and allocation-light: a preallocated ring, no per-sample allocation.</para>
/// </summary>
public sealed class MetricHistory
{
    private readonly double[] _samples;
    private int _next;

    /// <param name="capacity">How many samples to retain. At a 500 ms cadence, 60 is 30 seconds.</param>
    public MetricHistory(int capacity = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _samples = new double[capacity];
    }

    /// <summary>Number of samples retained (the window size).</summary>
    public int Capacity => _samples.Length;

    /// <summary>How many samples have actually been recorded, up to <see cref="Capacity"/>.</summary>
    public int Count { get; private set; }

    /// <summary>The most recent sample, or 0 if none.</summary>
    public double Latest { get; private set; }

    public void Add(double value)
    {
        _samples[_next] = value;
        _next = (_next + 1) % _samples.Length;
        Latest = value;
        if (Count < _samples.Length)
        {
            Count++;
        }
    }

    public void Clear()
    {
        Array.Clear(_samples);
        _next = 0;
        Count = 0;
        Latest = 0;
    }

    /// <summary>Highest retained sample, or 0 when empty. Useful for auto-scaling an axis.</summary>
    public double Max()
    {
        double max = 0;
        for (int i = 0; i < Count; i++)
        {
            double v = this[i];
            if (v > max)
            {
                max = v;
            }
        }

        return max;
    }

    /// <summary>
    /// Samples in chronological order: index 0 is the oldest retained sample, <see cref="Count"/>-1 the newest.
    /// Indexing rather than an iterator so a renderer can walk it without allocating.
    /// </summary>
    public double this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            // Once full, the oldest sample sits at the write cursor; before that, at 0.
            int start = Count == _samples.Length ? _next : 0;
            return _samples[(start + index) % _samples.Length];
        }
    }

    /// <summary>
    /// Map sample <paramref name="index"/> to a normalised height in 0..1 against <paramref name="fullScale"/>,
    /// clamped. Values above full scale flatten at the top rather than escaping the plot area, which is the
    /// right trade for a diagnostic strip: an off-scale spike should still read as "at maximum".
    /// </summary>
    public double NormalisedAt(int index, double fullScale)
    {
        if (fullScale <= 0)
        {
            return 0;
        }

        return Math.Clamp(this[index] / fullScale, 0, 1);
    }
}
