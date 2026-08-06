using System.Collections.Generic;
using System.Linq;
using Ripcord.Media.Interop;
using Ripcord.Presentation.Settings;

namespace Ripcord_App.Services;

/// <summary>
/// The Windows answer to "what can this machine do?", behind the portable seam.
///
/// <para>
/// Deliberately does nothing but call through and map. Every one of these can throw — they reach a driver — and
/// the guarding belongs in <see cref="SettingsViewModel"/>, where a failure becomes a specific degraded answer
/// ("no HEVC decoder", "couldn't list the adapters") that is written once and tested.
/// </para>
/// </summary>
public sealed class NativeVideoCapabilitiesProbe : IVideoCapabilitiesProbe
{
    public bool IsHevcDecodeAvailable()
        => VideoCapabilities.IsCodecDecodeAvailable(VideoCodecKind.Hevc);

    public bool IsHdrDisplayAvailable() => VideoCapabilities.IsHdrDisplayAvailable();

    public IReadOnlyList<VideoAdapterOption> EnumerateAdapters()
        => VideoCapabilities.EnumerateAdapters()
            .Select(a => new VideoAdapterOption(
                a.Description, a.Luid, a.SupportsHardwareDecode, a.DrivesADisplay))
            .ToList();
}
