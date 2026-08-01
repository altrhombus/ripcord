using Ripcord.Media.Interop;

namespace Ripcord.Media;

/// <summary>
/// Managed façade over the native WASAPI <see cref="AudioRenderer"/>. Open <see cref="Start"/> first,
/// then read <see cref="SampleRate"/>/<see cref="Channels"/> and push interleaved float PCM at that
/// format. Later, the session's audio decoder feeds it; for now a synthetic tone verifies output.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    private readonly AudioRenderer _renderer = new();
    private bool _started;

    public int SampleRate => (int)_renderer.SampleRate;
    public int Channels => (int)_renderer.Channels;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _renderer.Initialize();
        _renderer.Start();
        _started = true;
    }

    /// <summary>Submit a full buffer of interleaved float PCM at <see cref="SampleRate"/>/<see cref="Channels"/>.</summary>
    public void SubmitFloatPcm(float[] interleaved) => SubmitFloatPcm(interleaved, interleaved.Length);

    /// <summary>
    /// Submit the first <paramref name="sampleCount"/> entries of a reused buffer. This overload exists so the
    /// live audio path can hand over a pooled buffer without slicing it — slicing would allocate on every
    /// audio frame, and a GC pause on the audio path is audible.
    /// </summary>
    public void SubmitFloatPcm(float[] interleaved, int sampleCount)
    {
        if (_started && sampleCount > 0)
        {
            _renderer.SubmitFloatPcm(interleaved, (uint)sampleCount);
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            _renderer.Stop();
            _started = false;
        }
    }
}
