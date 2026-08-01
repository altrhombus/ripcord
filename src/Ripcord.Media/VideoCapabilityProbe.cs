using Ripcord.Media.Interop;

namespace Ripcord.Media;

/// <summary>
/// Managed façade over the native VideoCapabilities WinRT type, keeping Media.Interop's
/// existence an implementation detail of this project rather than something every consumer
/// needs to know how to reference.
/// </summary>
public static class VideoCapabilityProbe
{
    public static bool IsD3D12VideoDecodeSupported() => VideoCapabilities.IsD3D12VideoDecodeSupported();
}
