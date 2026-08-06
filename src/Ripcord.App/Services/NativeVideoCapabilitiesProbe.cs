using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ripcord.Media.Interop;
using Ripcord.Presentation.Settings;

namespace Ripcord_App.Services;

/// <summary>
/// The Windows answer to "what can this machine do?", behind the portable seam.
///
/// <para>
/// <b>Every call goes through <see cref="Task.Run"/>, and that is the entire point of this class.</b> These
/// resolve to Media Foundation and D3D12 work — <c>MFStartup</c> + <c>MFTEnumEx</c>, and a D3D12 device per
/// adapter — and calling them on the WinUI UI thread kills the process outright: a stowed
/// <c>E_UNEXPECTED</c> raised from <c>Microsoft.UI.Xaml.dll</c>, with no managed exception to catch and nothing
/// written to the crash log. The app just disappears.
/// </para>
///
/// <para>
/// The settings page did precisely this and crashed on every open, while the About page — which had always
/// wrapped the identical query in <c>Task.Run</c> — worked. Two call sites, one hazard, and only one of them
/// knew about it. The seam is asynchronous now so the knowledge lives in the type rather than in whichever
/// caller happened to have it.
/// </para>
///
/// <para>
/// Beyond the marshalling, this does nothing but call through and map. Guarding belongs in
/// <see cref="SettingsViewModel"/>, where a failure becomes a specific degraded answer ("no HEVC decoder",
/// "couldn't list the adapters") that is written once and tested.
/// </para>
///
/// <para>
/// <b>This is the only place in the app permitted to name <c>VideoCapabilities</c></b>, and
/// <c>NativeCapabilityAccessTests</c> enforces it. One call site is what makes the marshalling rule above
/// checkable at all — with two, the second was correct only because whoever wrote it happened to know.
/// </para>
/// </summary>
public sealed class NativeVideoCapabilitiesProbe : IVideoCapabilitiesProbe
{
    public Task<bool> IsHevcDecodeAvailableAsync()
        => Task.Run(() => VideoCapabilities.IsCodecDecodeAvailable(VideoCodecKind.Hevc));

    public Task<bool> IsHardwareDecodeSupportedAsync()
        => Task.Run(VideoCapabilities.IsD3D12VideoDecodeSupported);

    public Task<bool> IsHdrDisplayAvailableAsync()
        => Task.Run(VideoCapabilities.IsHdrDisplayAvailable);

    public Task<IReadOnlyList<VideoAdapterOption>> EnumerateAdaptersAsync()
        => Task.Run<IReadOnlyList<VideoAdapterOption>>(() =>
            VideoCapabilities.EnumerateAdapters()
                .Select(a => new VideoAdapterOption(
                    a.Description, a.Luid, a.SupportsHardwareDecode, a.DrivesADisplay))
                .ToList());
}
