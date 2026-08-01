#pragma once
#include "Ripcord.Media.Interop.VideoCapabilities.g.h"

#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

namespace winrt::Ripcord::Media::Interop::implementation
{
    struct VideoCapabilities : VideoCapabilitiesT<VideoCapabilities>
    {
        VideoCapabilities() = default;

        static bool IsD3D12VideoDecodeSupported();
        static bool IsCodecDecodeAvailable(VideoCodecKind codec);

        static Windows::Foundation::Collections::IVectorView<Ripcord::Media::Interop::VideoAdapterInfo>
            EnumerateAdapters();

        // ---- shared adapter-selection helpers (also used by VideoRenderer) ----

        /// Real decode-capability query for H.264 at `width`x`height` on a specific adapter.
        static bool AdapterSupportsH264Decode(IDXGIAdapter1* adapter, uint32_t width, uint32_t height);

        /// Whether this adapter has any attached output. On hybrid laptops a render-only discrete GPU has
        /// none, which is how we detect that presenting on it would incur a cross-adapter copy.
        static bool AdapterDrivesADisplay(IDXGIAdapter1* adapter);

        /// Pack a LUID into 64 bits so it can cross the WinRT boundary as a single value.
        static uint64_t PackLuid(const LUID& luid);

        /// Choose an adapter for `selection`. Returns nullptr to mean "let D3D12 pick the default".
        static Microsoft::WRL::ComPtr<IDXGIAdapter1> ChooseAdapter(
            IDXGIFactory6* factory,
            Ripcord::Media::Interop::GpuSelection selection,
            uint64_t specificLuid,
            uint32_t width,
            uint32_t height);
    };
}

namespace winrt::Ripcord::Media::Interop::factory_implementation
{
    struct VideoCapabilities : VideoCapabilitiesT<VideoCapabilities, implementation::VideoCapabilities>
    {
    };
}
