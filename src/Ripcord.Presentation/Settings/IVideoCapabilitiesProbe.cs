namespace Ripcord.Presentation.Settings;

/// <summary>One graphics adapter, as much of it as a settings picker needs.</summary>
/// <param name="SupportsHardwareDecode">
/// False means streaming would fall back to the CPU on this adapter. Worth saying plainly next to the entry
/// rather than letting someone pick it and wonder why their laptop is hot.
/// </param>
/// <param name="DrivesADisplay">
/// False means an extra copy per frame, because the decoded surface has to cross to whichever adapter is
/// actually presenting.
/// </param>
public readonly record struct VideoAdapterOption(
    string Description,
    ulong Luid,
    bool SupportsHardwareDecode,
    bool DrivesADisplay);

/// <summary>
/// What this machine can actually do, for the settings page to offer honestly.
///
/// <para>
/// A seam because every answer comes from native code — a decoder enumeration, a DXGI HDR query, and an adapter
/// walk that creates and destroys a D3D12 device per adapter to test decode capability. What the app does with
/// the answers is judgement, and judgement is what wants testing: whether HEVC is offered at all, which HDR
/// prerequisite to point at, and whether to warn that no installed GPU can decode.
/// </para>
///
/// <para>
/// <b>Asynchronous on purpose, and this is a correctness requirement rather than a courtesy.</b> On Windows
/// these resolve to Media Foundation and D3D12 calls that <em>cannot be made from the WinUI UI thread</em>:
/// doing so takes the whole process down with a stowed <c>E_UNEXPECTED</c> out of <c>Microsoft.UI.Xaml.dll</c>,
/// which no <c>try</c>/<c>catch</c> can intercept — the app simply vanishes. The settings page did exactly that
/// and crashed every time it was opened; the About page, which had always run the same query through
/// <c>Task.Run</c>, was fine. Returning Tasks is what stops the next caller rediscovering that: an
/// implementation is free to move the work off-thread, and no call site can accidentally block the UI thread on
/// it.
/// </para>
///
/// <para>
/// <b>Implementations may still fault</b>, and callers must expect it — each of these reaches a driver, and the
/// settings page is also where a user goes to fix a bad configuration, so it has to stay reachable. The
/// view-model degrades each answer independently rather than failing as a whole.
/// </para>
/// </summary>
public interface IVideoCapabilitiesProbe
{
    /// <summary>Whether an HEVC decoder exists here. HEVC is also the prerequisite for HDR.</summary>
    Task<bool> IsHevcDecodeAvailableAsync();

    /// <summary>
    /// Whether hardware video decoding is available at all. False means streaming falls back to the CPU, which
    /// works and costs far more power — a fact to report, not a failure.
    /// </summary>
    Task<bool> IsHardwareDecodeSupportedAsync();

    /// <summary>
    /// Whether the display is in HDR mode. True only while Windows' "Use HDR" is actually on, so this doubles
    /// as a check of the OS setting — the part users most often miss.
    /// </summary>
    Task<bool> IsHdrDisplayAvailableAsync();

    /// <summary>
    /// Every installed adapter. Expensive — a device per adapter — so callers should ask once, and only when
    /// the answer is going to be shown.
    /// </summary>
    Task<IReadOnlyList<VideoAdapterOption>> EnumerateAdaptersAsync();
}
