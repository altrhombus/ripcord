using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Ripcord.Core.Sessions;
using Ripcord.Diagnostics;
using Ripcord.Media.Audio;
using Ripcord.Media.Interop;

namespace Ripcord.Media;

/// <summary>
/// D3D12-backed decode pipeline over the native <see cref="VideoRenderer"/> (a composition swap
/// chain). Submissions are non-blocking: video/BGRA frames are queued and a dedicated background
/// thread does the (potentially expensive) decode + present, so a real stream never blocks the UI
/// thread. All native-renderer access is serialized under one lock, so the UI-thread resize/dispose
/// can't race the worker.
/// </summary>
/// <summary>Decode-pipeline diagnostics snapshot for the on-screen readout (see <see cref="D3D12VideoDecodePipeline.GetStats"/>).</summary>
public readonly record struct PipelineStats(
    long DecodedFrames,
    long PresentedFrames,
    int QueueDepth,
    double PipelineLatencyMs,
    int DecodeMode,
    /// <summary>UTC ticks when a frame was last presented, or 0 if none ever has. Drives the stream-liveness
    /// check — "no frames at all" and "frames stopped arriving" are different problems with different advice.</summary>
    long LastFrameUtcTicks = 0,
    /// <summary>Resolution actually being decoded, which the console chooses and need not match the request.</summary>
    int DecodedWidth = 0,
    int DecodedHeight = 0,
    /// <summary>YCbCr matrix in use and whether the stream signalled it. Surfaced so a colour cast is
    /// diagnosable rather than something to be argued about by eye.</summary>
    string ColorMatrix = "",
    /// <summary>Which decoder MFT was selected, so "HEVC requested" and "HEVC decoding" stay distinguishable —
    /// and so a change of decoder-selection strategy is visible rather than silent.</summary>
    string Decoder = "",
    /// <summary>Raw decode-loop counters. Only worth reading when frames are not appearing.</summary>
    string DecoderDiagnostic = "",
    /// <summary>The stream signals an HDR transfer function (PQ or HLG). Says nothing about what we output.</summary>
    bool IsHdrTransfer = false,
    /// <summary>The display accepts HDR10. Says nothing about what the stream carries.</summary>
    bool IsDisplayHdr = false,
    /// <summary>Both of the above, and the swap chain accepted the colour space — the only one of the three that
    /// means "you are looking at HDR". When this is false while <see cref="IsHdrTransfer"/> is true, the driver
    /// is tone-mapping to SDR. These are separate booleans rather than one because the UI previously inferred
    /// HDR by substring-matching <see cref="Decoder"/>, which lit an HDR pill while tone-mapping to SDR.</summary>
    bool IsHdrOutput = false,
    /// <summary>10-bit decode (P010). Independent of HDR — a stream can be 10-bit and SDR.</summary>
    bool IsTenBit = false,
    /// <summary>Audio frames successfully decoded and submitted, cumulative. At 480 samples per frame and 48 kHz
    /// this should advance at ~100/s, which makes it a far better liveness signal than "can I hear it".</summary>
    long AudioFramesDecoded = 0,
    /// <summary>Audio frames dropped by a decode or submit error. Nonzero here is the thing to notice.</summary>
    long AudioFramesSkipped = 0,
    /// <summary>Stream format and the device format it is being converted to, or why audio is silent.</summary>
    string AudioFormat = "");

public sealed class D3D12VideoDecodePipeline : IVideoDecodePipeline
{
    private readonly VideoRenderer _renderer = new();
    private readonly object _lock = new();

    private BlockingCollection<RenderJob>? _jobs;
    private Thread? _worker;
    private bool _initialized;
    private bool _decodeModeLogged;

    // Diagnostics for the on-screen readout. Counts are written only on the decode worker; the UI reads them
    // (benignly — a stale read just skips a sample). Interlocked/Volatile keep the reads torn-free.
    private long _decodedFrames;
    private long _presentedFrames;
    private long _lastPresentLatencyTicks; // demux→present latency of the most recently presented frame
    private long _workerLastDecodedTicks;  // worker-only: demux timestamp of the newest decoded frame in a burst
    private long _lastFrameUtcTicks;       // wall-clock of the last present, for the liveness/stall check

    private UpscaleMode _upscaleMode;

    public UpscaleMode UpscaleMode
    {
        get => _upscaleMode;
        set
        {
            _upscaleMode = value;
            // Map the product mode to the renderer's spatial filter. Only bilinear (None) and a bicubic
            // "sharp" fallback are implemented today; the AutoSR/DirectML tiers land later.
            _renderer.SetUpscaleMode(value == UpscaleMode.FsrFallback ? 1 : 0);
        }
    }

    /// <summary>
    /// The active video decode path: 0 = software, 1 = hardware (DXVA) with CPU readback, 2 = hardware (DXVA)
    /// zero-copy (GPU-resident). Settles after the first few frames. A benign lock-free status read — safe to
    /// poll from the UI for an on-screen indicator without stalling the decode worker.
    /// </summary>
    public int DecodeMode => _initialized ? _renderer.DecodeMode : 0;

    /// <summary>
    /// A lock-free snapshot of decode-pipeline counters for an on-screen diagnostics readout. Cumulative frame
    /// counts (the UI derives FPS from deltas over its sample interval), the current encoded-frame backlog
    /// (a large or growing depth = the decoder falling behind → the "seconds behind live" symptom), and the
    /// demux→present latency of the most recent frame (the part of glass-to-glass we control).
    /// </summary>
    public PipelineStats GetStats() => new(
        DecodedFrames: Interlocked.Read(ref _decodedFrames),
        PresentedFrames: Interlocked.Read(ref _presentedFrames),
        QueueDepth: _jobs?.Count ?? 0,
        PipelineLatencyMs: Volatile.Read(ref _lastPresentLatencyTicks) / (double)TimeSpan.TicksPerMillisecond,
        DecodeMode: DecodeMode,
        LastFrameUtcTicks: Volatile.Read(ref _lastFrameUtcTicks),
        DecodedWidth: _initialized ? (int)_renderer.DecodedWidth : 0,
        DecodedHeight: _initialized ? (int)_renderer.DecodedHeight : 0,
        ColorMatrix: _initialized ? _renderer.ColorMatrixDescription : string.Empty,
        Decoder: _initialized ? _renderer.DecoderDescription : string.Empty,
        DecoderDiagnostic: _initialized ? _renderer.DecoderDiagnostic : string.Empty,
        IsHdrTransfer: _initialized && _renderer.IsHdrTransfer,
        IsDisplayHdr: _initialized && _renderer.IsDisplayHdr,
        IsHdrOutput: _initialized && _renderer.IsHdrOutput,
        IsTenBit: _initialized && _renderer.IsTenBit,
        AudioFramesDecoded: Interlocked.Read(ref _audioFramesDecoded),
        AudioFramesSkipped: Interlocked.Read(ref _audioFramesSkipped),
        AudioFormat: AudioFormatDescription);

    /// <summary>Audio format, or why there is none. Read without the audio lock — a torn read costs one stale
    /// sample on a diagnostics overlay, whereas contending with the audio submit path costs glitches.</summary>
    private string AudioFormatDescription
        => _audioFailed || _audioFormat.Length > 0 ? _audioFormat : "waiting for audio";

    private static string Describe(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        _ => $"{channels}ch",
    };

    /// <summary>Raw IDXGISwapChain* for ISwapChainPanelNative association; 0 until started.</summary>
    public ulong SwapChainPointer
    {
        get
        {
            lock (_lock)
            {
                return _initialized ? _renderer.SwapChainPointer : 0;
            }
        }
    }

    /// <summary>
    /// Which GPU to create the device on. Set before <see cref="StartAsync"/>; changing it afterwards has no
    /// effect until the pipeline is recreated (switching adapters means a new device and swap chain, so the
    /// panel has to be re-bound — that is a session-boundary operation, not a live toggle).
    /// </summary>
    public GpuSelection GpuSelection { get; set; } = GpuSelection.Auto;

    /// <summary>LUID of the adapter to pin when <see cref="GpuSelection"/> is <see cref="GpuSelection.Specific"/>.</summary>
    public ulong SpecificAdapterLuid { get; set; }

    /// <summary>Description of the adapter actually in use, once started.</summary>
    public string ActiveAdapterDescription { get; private set; } = string.Empty;

    /// <summary>
    /// Raised once when the graphics device is lost (driver update, TDR, external GPU unplugged). The pipeline
    /// stops rendering at that point and cannot recover in place; the owner must rebuild it and re-bind the
    /// swap chain. Previously this condition threw on every frame forever behind a frozen picture.
    /// </summary>
    public event Action<int>? DeviceLost;

    /// <summary>True once the graphics device has been lost. Nothing will render until recreated.</summary>
    public bool IsDeviceLost => _deviceLostSignalled;

    private bool _deviceLostSignalled;

    public Task StartAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _renderer.Initialize(
                (uint)Math.Max(1, config.Width),
                (uint)Math.Max(1, config.Height),
                GpuSelection,
                SpecificAdapterLuid);

            // Before any decode call: the MFT is created lazily on first use and its input type is fixed then,
            // so the codec has to be known now. This must agree with the launchSpec's videoCodec — asking the
            // console for HEVC while building an H.264 decoder would yield a stream we cannot decode at all.
            _renderer.SetCodec(
                config.CodecPreference == VideoCodec.Hevc ? VideoCodecKind.Hevc : VideoCodecKind.H264);
            ActiveAdapterDescription = _renderer.ActiveAdapterDescription;
            _initialized = true;
        }

        // Unbounded: the worker decodes EVERY queued frame (dropping one breaks the H.264 reference chain and
        // shows as glitching), draining the whole backlog each pass and presenting only the newest. A hard
        // safety cap in SubmitEncodedVideo bounds memory if the decoder ever falls catastrophically behind.
        _jobs = new BlockingCollection<RenderJob>();
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "RipcordDecode" };
        _worker.Start();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Backlog depth at which we stop trying to catch up frame-by-frame and resynchronise instead (~0.5 s at
    /// 60 fps). Previously this was 90 frames (~1.5 s) and the response was to decode the entire backlog —
    /// but if the decoder is that far behind, every one of those frames is stale by the time it appears, and
    /// once any frame is dropped the reference chain is broken anyway. Asking for a fresh IDR and skipping to
    /// it costs one brief freeze instead of a second and a half of late, corrupt video.
    /// </summary>
    private const int MaxQueuedFrames = 30;

    /// <summary>
    /// Raised when the backlog indicates the decoder cannot catch up and a fresh IDR is the fastest way back to
    /// a clean picture. The owner wires this to the session's keyframe-request path. Throttled by the consumer
    /// (the session already rate-limits IDR requests).
    /// </summary>
    public event Action? KeyFrameRequested;

    /// <summary>
    /// True while we are discarding frames until the next IDR. Set when the backlog blows out; cleared by the
    /// first keyframe. Decoding non-keyframes in this state is pure waste — their references were dropped.
    /// </summary>
    private volatile bool _awaitingKeyFrame;

    public void SubmitEncodedVideo(EncodedVideoFrame frame)
    {
        BlockingCollection<RenderJob>? jobs = _jobs;
        if (jobs is null)
        {
            return;
        }

        // Catastrophic backlog: shed it entirely and resynchronise on the next IDR rather than grinding
        // through frames that are already too late to show.
        if (jobs.Count >= MaxQueuedFrames)
        {
            while (jobs.TryTake(out RenderJob shed))
            {
                shed.Release();
            }

            if (!_awaitingKeyFrame)
            {
                _awaitingKeyFrame = true;
                RipcordEventSource.Log.BacklogResync(jobs.Count);
                KeyFrameRequested?.Invoke();
            }
        }

        // While resynchronising, drop everything up to the IDR. Cheaper than decoding it, and the picture
        // snaps back in one step instead of degrading through a broken reference chain.
        if (_awaitingKeyFrame)
        {
            if (!frame.IsKeyFrame)
            {
                return;
            }

            _awaitingKeyFrame = false;
        }

        // Pooled copy rather than ToArray(): at 60 fps this path allocated a fresh (often >100 KB, i.e.
        // large-object-heap) array per frame. The worker returns the buffer to the pool once decoded.
        int length = frame.Payload.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        frame.Payload.Span.CopyTo(buffer);

        if (!jobs.TryAdd(RenderJob.Decode(buffer, length, frame.TimestampTicks, frame.IsKeyFrame)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Audio path (Opus -> float PCM -> device-format -> WASAPI). Lazily initialised on the first audio frame
    // and driven from the demuxer's audio callback thread (single-threaded via _audioLock).
    private readonly object _audioLock = new();
    private OpusAudioDecoder? _audioDecoder;
    private AudioFormatConverter? _audioConverter;
    private AudioOutput? _audioOutput;
    private readonly float[] _audioScratch = new float[5760 * 2]; // max Opus frame (120 ms @ 48 kHz), stereo
    private float[] _audioConverted = [];                          // reused device-format buffer (see AudioFormatConverter)
    private bool _audioReady;
    private bool _audioFailed;

    // Audio observability. There was none: after declaring audioChannels in the launchSpec the only available
    // signal was whether it sounded different, which cannot distinguish "working" from "silently degraded".
    private long _audioFramesDecoded;
    private long _audioFramesSkipped;
    private string _audioFormat = string.Empty;

    public void SubmitEncodedAudio(EncodedAudioFrame frame)
    {
        lock (_audioLock)
        {
            if (_audioFailed)
            {
                return;
            }

            if (!_audioReady)
            {
                try
                {
                    _audioOutput = new AudioOutput();
                    _audioOutput.Start();
                    _audioDecoder = new OpusAudioDecoder(channels: 2); // PS5 audio is Opus stereo
                    _audioConverter = new AudioFormatConverter(_audioOutput.SampleRate, _audioOutput.Channels);
                    _audioReady = true;

                    // Both sides matter: the console always sends 48 kHz stereo Opus, but the render device may
                    // want something else, and that difference is a resample nobody would otherwise see.
                    // Compare the FORMATS, not the rendered strings: the source string carries an "opus " prefix
                    // the device string does not, so a string comparison never matched and the arrow was shown
                    // even when nothing was being converted.
                    bool converting = _audioOutput.SampleRate != OpusAudioDecoder.SampleRate
                                      || _audioOutput.Channels != _audioDecoder!.Channels;

                    string source = $"opus {OpusAudioDecoder.SampleRate / 1000} kHz {Describe(_audioDecoder!.Channels)}";
                    _audioFormat = converting
                        ? $"{source} \u2192 {_audioOutput.SampleRate / 1000} kHz {Describe(_audioOutput.Channels)}"
                        : source;
                }
                catch (Exception ex)
                {
                    _audioFailed = true; // don't retry every frame
                    _audioFormat = $"unavailable \u2014 {ex.Message}";
                    RipcordEventSource.Log.AudioInitFailed(ex.Message);
                    Debug.WriteLine($"[Ripcord] audio init failed: {ex.Message}");
                    return;
                }
            }

            try
            {
                int samplesPerChannel = _audioDecoder!.Decode(frame.Payload.Span, _audioScratch);
                if (samplesPerChannel <= 0)
                {
                    // Counted, not silently ignored: a stream that decodes to nothing looks identical to no stream.
                    Interlocked.Increment(ref _audioFramesSkipped);
                    return;
                }

                // Converts into the reusable buffer: no per-frame allocation on the audio path.
                int written = _audioConverter!.Convert(
                    _audioScratch.AsSpan(0, samplesPerChannel * 2), samplesPerChannel, ref _audioConverted);
                _audioOutput!.SubmitFloatPcm(_audioConverted, written);
                Interlocked.Increment(ref _audioFramesDecoded);
            }
            catch (Exception ex)
            {
                // A single bad packet shouldn't kill audio; surface it for debugging.
                Interlocked.Increment(ref _audioFramesSkipped);
                RipcordEventSource.Log.AudioFrameSkipped(ex.Message);
                Debug.WriteLine($"[Ripcord] audio frame skipped: {ex.Message}");
            }
        }
    }

    /// <summary>Layer 2 test hook: queue a BGRA frame to be drawn scaled to fill the surface.</summary>
    public void PresentBgra(byte[] bgra, int width, int height)
        => _jobs?.TryAdd(RenderJob.Bgra((byte[])bgra.Clone(), width, height));

    /// <summary>Layer 1 test hook: clear the surface to a colour (runs on the worker).</summary>
    public void RenderClear(float red, float green, float blue)
        => _jobs?.TryAdd(RenderJob.Clear(red, green, blue));

    // Pending panel geometry, published by the UI thread and applied by the decode worker.
    //
    // These used to call into the renderer directly under _lock — the same lock the worker holds across a whole
    // decode + present. A window resize or a monitor DPI change therefore blocked the UI thread for a full
    // frame's GPU work, which is exactly the kind of hitch that makes an app feel broken while dragging.
    // Publishing two ints and letting the worker pick them up costs nothing and never blocks.
    private int _pendingWidth;
    private int _pendingHeight;
    private long _pendingScaleBits;   // two floats packed, so scale is published atomically as one write
    private int _geometryDirty;

    public void Resize(int width, int height)
    {
        Volatile.Write(ref _pendingWidth, Math.Max(1, width));
        Volatile.Write(ref _pendingHeight, Math.Max(1, height));
        Volatile.Write(ref _geometryDirty, 1);
    }

    /// <summary>Apply the panel's DPI composition scale so a physical-pixel back buffer presents 1:1.</summary>
    public void SetCompositionScale(float scaleX, float scaleY)
    {
        long packed = ((long)(uint)BitConverter.SingleToInt32Bits(scaleX) << 32)
                      | (uint)BitConverter.SingleToInt32Bits(scaleY);
        Volatile.Write(ref _pendingScaleBits, packed);
        Volatile.Write(ref _geometryDirty, 1);
    }

    /// <summary>
    /// Apply any pending resize/scale on the worker thread, where native access is already serialized. Called
    /// with <see cref="_lock"/> held, at a frame boundary rather than mid-frame.
    /// </summary>
    private void ApplyPendingGeometry()
    {
        if (Interlocked.Exchange(ref _geometryDirty, 0) == 0)
        {
            return;
        }

        int width = Volatile.Read(ref _pendingWidth);
        int height = Volatile.Read(ref _pendingHeight);
        if (width > 0 && height > 0)
        {
            _renderer.Resize((uint)width, (uint)height);
        }

        long packed = Volatile.Read(ref _pendingScaleBits);
        if (packed != 0)
        {
            float scaleX = BitConverter.Int32BitsToSingle((int)(packed >> 32));
            float scaleY = BitConverter.Int32BitsToSingle((int)(packed & 0xFFFFFFFF));
            if (scaleX > 0 && scaleY > 0)
            {
                _renderer.SetCompositionScale(scaleX, scaleY);
            }
        }
    }

    private void WorkerLoop()
    {
        BlockingCollection<RenderJob>? jobs = _jobs;
        if (jobs is null)
        {
            return;
        }

        try
        {
            foreach (RenderJob job in jobs.GetConsumingEnumerable())
            {
                lock (_lock)
                {
                    if (!_initialized)
                    {
                        job.Release();
                        return;
                    }

                    if (_deviceLostSignalled)
                    {
                        job.Release(); // nothing can render; drain without decoding so memory stays bounded
                        continue;
                    }

                    try
                    {
                        // Pick up any resize/DPI change published by the UI thread, at a frame boundary.
                        ApplyPendingGeometry();

                        // Decode this frame plus everything else already queued in the burst (each Decode job
                        // advances the decoder without presenting), then present only the newest. This keeps
                        // the reference chain intact under load while never letting the backlog add latency.
                        bool decoded = RunJob(job);
                        while (_initialized && jobs.TryTake(out RenderJob more))
                        {
                            decoded |= RunJob(more);
                        }

                        // Present may be where device loss first shows up, and the native side records it
                        // rather than throwing.
                        if (_renderer.IsDeviceLost)
                        {
                            SignalDeviceLost();
                            continue;
                        }

                        if (decoded && _initialized)
                        {
                            _renderer.PresentLatestFrame();
                            Interlocked.Increment(ref _presentedFrames);
                            Volatile.Write(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
                            if (_workerLastDecodedTicks != 0)
                            {
                                Volatile.Write(ref _lastPresentLatencyTicks, DateTime.UtcNow.Ticks - _workerLastDecodedTicks);
                            }

                            // Report the settled decode path once, from managed code so it's reliably visible
                            // in the VS Output/Debug window even when the native OutputDebugString isn't. Gate on
                            // an actually-decoded frame — the decoder's first submits produce none, and zero-copy
                            // can only be judged after a real frame has flowed.
                            if (!_decodeModeLogged && _renderer.HasDecodedFrame)
                            {
                                _decodeModeLogged = true;
                                int mode = _renderer.DecodeMode;
                                string path = mode switch
                                {
                                    2 => "hardware DXVA, zero-copy (GPU-resident)",
                                    1 => $"hardware DXVA, CPU readback (zero-copy fell back at: {_renderer.ZeroCopyDiagnostic()})",
                                    _ => "software",
                                };

                                // Traced as well as logged: which decode path settled, on which GPU, at which
                                // resolution is the first thing worth knowing from any capture.
                                RipcordEventSource.Log.DecodePathSettled(
                                    path,
                                    ActiveAdapterDescription,
                                    (int)_renderer.DecodedWidth,
                                    (int)_renderer.DecodedHeight);
                                Debug.WriteLine($"[Ripcord] video decode path: {path}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Device loss is terminal for this pipeline: report it once and stop, rather than
                        // logging "frame skipped" forever behind a picture that will never update again.
                        if (_renderer.IsDeviceLost)
                        {
                            SignalDeviceLost();
                        }
                        else
                        {
                            // A single bad frame shouldn't kill playback; surface it for debugging.
                            RipcordEventSource.Log.FrameSkipped(ex.Message);
                            Debug.WriteLine($"[RipcordDecode] frame skipped: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // pipeline disposed
        }
    }

    /// <summary>
    /// Report device loss exactly once. Called from the decode worker; the event fires on that thread, so a UI
    /// consumer must marshal.
    /// </summary>
    private void SignalDeviceLost()
    {
        if (_deviceLostSignalled)
        {
            return;
        }

        _deviceLostSignalled = true;
        int reason = _renderer.DeviceRemovedReason;
        RipcordEventSource.Log.DeviceLost(reason);
        Debug.WriteLine($"[Ripcord] graphics device lost (0x{reason:X8}); pipeline must be recreated.");
        DeviceLost?.Invoke(reason);
    }

    /// <summary>Run one job. Decode jobs decode-only (returns true so the caller presents the latest once
    /// after draining the burst); BGRA/Clear test hooks present themselves. Always releases the job's pooled
    /// payload, including on failure — otherwise a run of bad frames would leak the pool dry.</summary>
    private bool RunJob(RenderJob job)
    {
        try
        {
            job.Execute(_renderer);
            if (job.Kind != JobKind.Decode)
            {
                return false;
            }

            Interlocked.Increment(ref _decodedFrames);
            _workerLastDecodedTicks = job.TimestampTicks; // newest decoded in the burst = the one we present
            return true;
        }
        finally
        {
            job.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _jobs?.CompleteAdding();
        Thread? worker = _worker;
        if (worker is not null)
        {
            await Task.Run(worker.Join).ConfigureAwait(false);
            _worker = null;
        }

        lock (_lock)
        {
            if (_initialized)
            {
                _renderer.Shutdown();
                _initialized = false;
            }
        }

        lock (_audioLock)
        {
            _audioOutput?.Dispose();
            _audioOutput = null;
            _audioReady = false;
        }

        // Return any payloads still queued at shutdown so the pool isn't left holding them.
        if (_jobs is not null)
        {
            while (_jobs.TryTake(out RenderJob pending))
            {
                pending.Release();
            }
        }

        _jobs?.Dispose();
        _jobs = null;
    }

    private enum JobKind { Decode, Bgra, Clear }

    private readonly struct RenderJob
    {
        private readonly JobKind _kind;
        private readonly byte[]? _data;
        private readonly int _length;   // valid bytes in _data (it may be a pooled buffer that is larger)
        private readonly bool _pooled;  // _data came from ArrayPool and must be returned after use
        private readonly int _width;
        private readonly int _height;
        private readonly float _r;
        private readonly float _g;
        private readonly float _b;
        private readonly long _timestampTicks;
        private readonly bool _isKeyFrame;

        private RenderJob(
            JobKind kind, byte[]? data, int length, bool pooled, int width, int height,
            float r, float g, float b, long timestampTicks, bool isKeyFrame)
        {
            _kind = kind;
            _data = data;
            _length = length;
            _pooled = pooled;
            _width = width;
            _height = height;
            _r = r;
            _g = g;
            _b = b;
            _timestampTicks = timestampTicks;
            _isKeyFrame = isKeyFrame;
        }

        public static RenderJob Decode(byte[] annexB, int length, long timestampTicks, bool isKeyFrame)
            => new(JobKind.Decode, annexB, length, pooled: true, 0, 0, 0, 0, 0, timestampTicks, isKeyFrame);

        public static RenderJob Bgra(byte[] bgra, int w, int h)
            => new(JobKind.Bgra, bgra, bgra.Length, pooled: false, w, h, 0, 0, 0, 0, false);

        public static RenderJob Clear(float r, float g, float b)
            => new(JobKind.Clear, null, 0, pooled: false, 0, 0, r, g, b, 0, false);

        public JobKind Kind => _kind;
        public byte[]? Data => _data;

        /// <summary>Demux timestamp of this frame (DateTime ticks), for measuring demux→present latency.</summary>
        public long TimestampTicks => _timestampTicks;

        /// <summary>Whether this access unit is an IDR — a safe resynchronisation point after a drop.</summary>
        public bool IsKeyFrame => _isKeyFrame;

        /// <summary>Return a pooled payload buffer. Safe to call on any job; a no-op for non-pooled ones.</summary>
        public void Release()
        {
            if (_pooled && _data is not null)
            {
                ArrayPool<byte>.Shared.Return(_data);
            }
        }

        public void Execute(VideoRenderer renderer)
        {
            switch (_kind)
            {
                case JobKind.Decode:
                    // Pass the valid length: _data may be a larger pooled buffer.
                    renderer.DecodeH264(_data!, (uint)_length); // decode-only; pipeline presents latest after draining
                    break;
                case JobKind.Bgra:
                    renderer.PresentBgra(_data!, (uint)_width, (uint)_height);
                    break;
                case JobKind.Clear:
                    renderer.RenderClear(_r, _g, _b);
                    break;
            }
        }
    }
}
