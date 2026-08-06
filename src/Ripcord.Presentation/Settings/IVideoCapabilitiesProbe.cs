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
/// A seam because every answer comes from native code — a decoder query, a DXGI HDR query, and an adapter
/// enumeration that creates and destroys a D3D12 device per adapter to test decode capability. What the page
/// does with the answers is judgement, and judgement is what wants testing: whether HEVC is offered at all,
/// which HDR prerequisite to point at, and whether to warn that no installed GPU can decode.
/// </para>
///
/// <para>
/// <b>Implementations may throw</b>, and callers must expect it. Each of these reaches a driver, and the
/// settings page is also where a user goes to fix a bad configuration — so it has to stay reachable when one
/// of them fails. The view-model degrades each answer independently rather than failing as a whole.
/// </para>
/// </summary>
public interface IVideoCapabilitiesProbe
{
    /// <summary>Whether an HEVC decoder exists here. HEVC is also the prerequisite for HDR.</summary>
    bool IsHevcDecodeAvailable();

    /// <summary>
    /// Whether the display is in HDR mode. True only while Windows' "Use HDR" is actually on, so this doubles
    /// as a check of the OS setting — the part users most often miss.
    /// </summary>
    bool IsHdrDisplayAvailable();

    /// <summary>
    /// Every installed adapter. Expensive — a device per adapter — so callers should ask once, and only when
    /// the answer is going to be shown.
    /// </summary>
    IReadOnlyList<VideoAdapterOption> EnumerateAdapters();
}
