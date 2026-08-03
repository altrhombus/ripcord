#include "pch.h"
#include "VideoCapabilities.h"
#include "CodecSubtypes.h"  // one place for the codec -> MF subtype mapping
#include "Ripcord.Media.Interop.VideoCapabilities.g.cpp"

#include <d3d12.h>
#include <d3d12video.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace winrt::Ripcord::Media::Interop::implementation
{
    namespace
    {
        /// A representative streaming resolution to probe with. Capability is resolution-dependent, so a
        /// bare "does the interface exist" check can report success on hardware that cannot do 1080p.
        constexpr uint32_t kProbeWidth = 1920;
        constexpr uint32_t kProbeHeight = 1080;

        /// Create a D3D12 device on `adapter` (or the default when null) without keeping it alive.
        ComPtr<ID3D12Device> TryCreateDevice(IDXGIAdapter1* adapter)
        {
            ComPtr<ID3D12Device> device;
            if (FAILED(D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device))))
            {
                return nullptr;
            }

            return device;
        }

        bool DeviceSupportsH264Decode(ID3D12Device* device, uint32_t width, uint32_t height)
        {
            ComPtr<ID3D12VideoDevice> videoDevice;
            if (!device || FAILED(device->QueryInterface(IID_PPV_ARGS(&videoDevice))))
            {
                return false;
            }

            // The honest question: can this adapter decode *this* profile at *this* size, in NV12?
            D3D12_VIDEO_DECODE_CONFIGURATION config{};
            config.DecodeProfile = D3D12_VIDEO_DECODE_PROFILE_H264;
            config.BitstreamEncryption = D3D12_BITSTREAM_ENCRYPTION_TYPE_NONE;
            config.InterlaceType = D3D12_VIDEO_FRAME_CODED_INTERLACE_TYPE_NONE;

            D3D12_FEATURE_DATA_VIDEO_DECODE_SUPPORT support{};
            support.NodeIndex = 0;
            support.Configuration = config;
            support.Width = width;
            support.Height = height;
            support.DecodeFormat = DXGI_FORMAT_NV12;
            support.FrameRate = { 60, 1 };
            support.BitRate = 15'000'000;

            if (FAILED(videoDevice->CheckFeatureSupport(
                    D3D12_FEATURE_VIDEO_DECODE_SUPPORT, &support, sizeof(support))))
            {
                return false;
            }

            return (support.SupportFlags & D3D12_VIDEO_DECODE_SUPPORT_FLAG_SUPPORTED) != 0;
        }

        /// True for the Microsoft Basic Render Driver (WARP): enumerated like a real adapter but useless for
        /// hardware decode, and picking it would silently land us on software.
        bool IsSoftwareAdapter(const DXGI_ADAPTER_DESC1& desc)
        {
            return (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
        }
    }

    uint64_t VideoCapabilities::PackLuid(const LUID& luid)
    {
        return (static_cast<uint64_t>(static_cast<uint32_t>(luid.HighPart)) << 32)
             | static_cast<uint64_t>(luid.LowPart);
    }

    bool VideoCapabilities::AdapterDrivesADisplay(IDXGIAdapter1* adapter)
    {
        if (!adapter)
        {
            return false;
        }

        ComPtr<IDXGIOutput> output;
        return SUCCEEDED(adapter->EnumOutputs(0, &output)) && output != nullptr;
    }

    bool VideoCapabilities::AdapterSupportsH264Decode(IDXGIAdapter1* adapter, uint32_t width, uint32_t height)
    {
        ComPtr<ID3D12Device> device = TryCreateDevice(adapter);
        return device && DeviceSupportsH264Decode(device.Get(), width, height);
    }

    /// Whether a synchronous decoder MFT is registered for this codec.
    ///
    /// Counts registrations rather than activating one, so this is cheap enough to call from a settings page.
    /// SYNCMFT only, matching what the decode loop can actually drive — see CreateDecoderForCodec in
    /// VideoRenderer.cpp. HEVC availability is genuinely variable (GPU support plus the HEVC Video Extension),
    /// which is why the UI needs to ask instead of discovering it when a session fails to start.
    bool VideoCapabilities::IsCodecDecodeAvailable(VideoCodecKind codec)
    {
        // MFTEnumEx needs the MF platform up, and this is a static that a settings page can call before any
        // session exists. MFStartup/MFShutdown are reference counted, so this neither disturbs a renderer that
        // already started the platform nor leaks it if none has.
        const bool started = SUCCEEDED(MFStartup(MF_VERSION, MFSTARTUP_LITE));

        const RipcordCodecSupport::SubtypePair pair = RipcordCodecSupport::SubtypesFor(codec);
        const GUID subtypes[2]{ pair.Primary, pair.ElementaryStream };
        bool found = false;

        for (const GUID& subtype : subtypes)
        {
            MFT_REGISTER_TYPE_INFO inputInfo{ MFMediaType_Video, subtype };
            IMFActivate** activates = nullptr;
            UINT32 count = 0;

            HRESULT hr = MFTEnumEx(
                MFT_CATEGORY_VIDEO_DECODER,
                MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
                &inputInfo,
                nullptr,
                &activates,
                &count);

            if (activates)
            {
                for (UINT32 i = 0; i < count; ++i)
                {
                    activates[i]->Release();
                }

                CoTaskMemFree(activates);
            }

            if (SUCCEEDED(hr) && count > 0)
            {
                found = true;
                break;
            }
        }

        if (started)
        {
            MFShutdown();
        }

        return found;
    }

    // Deliberately "is this machine ready to show HDR right now" rather than "does the panel support HDR".
    // DXGI reports DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 only while Windows' "Use HDR" is on for the
    // display, so a capable panel with the toggle off correctly answers false - which is what a readiness
    // check should say, because that is a thing the user can go and fix.
    bool VideoCapabilities::IsHdrDisplayAvailable()
    {
        ComPtr<IDXGIFactory1> factory;
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
        {
            return false;
        }

        // Every adapter, not just the default: on a hybrid laptop the display frequently hangs off the
        // integrated GPU while the adapter we would decode on drives no output at all.
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT a = 0; SUCCEEDED(factory->EnumAdapters1(a, &adapter)); a++)
        {
            ComPtr<IDXGIOutput> output;
            for (UINT o = 0; SUCCEEDED(adapter->EnumOutputs(o, &output)); o++)
            {
                ComPtr<IDXGIOutput6> output6;
                DXGI_OUTPUT_DESC1 desc{};
                if (SUCCEEDED(output.As(&output6)) && output6
                    && SUCCEEDED(output6->GetDesc1(&desc))
                    && desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)
                {
                    return true;
                }
                output.Reset();
            }
            adapter.Reset();
        }

        return false;
    }

    bool VideoCapabilities::IsD3D12VideoDecodeSupported()
    {
        ComPtr<ID3D12Device> device = TryCreateDevice(nullptr);
        return device && DeviceSupportsH264Decode(device.Get(), kProbeWidth, kProbeHeight);
    }

    Windows::Foundation::Collections::IVectorView<Ripcord::Media::Interop::VideoAdapterInfo>
        VideoCapabilities::EnumerateAdapters()
    {
        std::vector<Ripcord::Media::Interop::VideoAdapterInfo> results;

        ComPtr<IDXGIFactory1> factory;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
        {
            ComPtr<IDXGIAdapter1> adapter;
            for (UINT i = 0; SUCCEEDED(factory->EnumAdapters1(i, &adapter)); i++)
            {
                DXGI_ADAPTER_DESC1 desc{};
                if (FAILED(adapter->GetDesc1(&desc)))
                {
                    adapter.Reset();
                    continue;
                }

                Ripcord::Media::Interop::VideoAdapterInfo info{};
                info.Description = winrt::hstring{ desc.Description };
                info.Luid = PackLuid(desc.AdapterLuid);
                info.DedicatedVideoMemory = desc.DedicatedVideoMemory;
                info.DrivesADisplay = AdapterDrivesADisplay(adapter.Get());
                info.SupportsHardwareDecode = !IsSoftwareAdapter(desc)
                    && AdapterSupportsH264Decode(adapter.Get(), kProbeWidth, kProbeHeight);

                results.push_back(info);
                adapter.Reset();
            }
        }

        return winrt::single_threaded_vector<Ripcord::Media::Interop::VideoAdapterInfo>(std::move(results))
            .GetView();
    }

    ComPtr<IDXGIAdapter1> VideoCapabilities::ChooseAdapter(
        IDXGIFactory6* factory,
        Ripcord::Media::Interop::GpuSelection selection,
        uint64_t specificLuid,
        uint32_t width,
        uint32_t height)
    {
        if (!factory)
        {
            return nullptr;
        }

        // Pinned by LUID: honour it exactly, and fall back to the default only if it is gone (eGPU unplugged,
        // driver replaced) rather than silently substituting a different GPU the user did not choose.
        if (selection == Ripcord::Media::Interop::GpuSelection::Specific)
        {
            ComPtr<IDXGIAdapter1> adapter;
            for (UINT i = 0; SUCCEEDED(factory->EnumAdapters1(i, &adapter)); i++)
            {
                DXGI_ADAPTER_DESC1 desc{};
                if (SUCCEEDED(adapter->GetDesc1(&desc)) && PackLuid(desc.AdapterLuid) == specificLuid)
                {
                    return adapter;
                }

                adapter.Reset();
            }

            return nullptr;
        }

        const DXGI_GPU_PREFERENCE preference =
            selection == Ripcord::Media::Interop::GpuSelection::PreferPerformance
                ? DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE
                : DXGI_GPU_PREFERENCE_MINIMUM_POWER;

        // Two passes for Auto. First insist the adapter also drives a display, so we present on the adapter
        // the panel is attached to and avoid a per-frame cross-adapter copy. Then relax that, because a
        // desktop with a headless-but-only GPU must still work.
        const bool requireDisplayFirst = selection == Ripcord::Media::Interop::GpuSelection::Auto;

        for (int pass = 0; pass < (requireDisplayFirst ? 2 : 1); pass++)
        {
            const bool requireDisplay = requireDisplayFirst && pass == 0;

            ComPtr<IDXGIAdapter1> adapter;
            for (UINT i = 0;
                 SUCCEEDED(factory->EnumAdapterByGpuPreference(i, preference, IID_PPV_ARGS(&adapter)));
                 i++)
            {
                DXGI_ADAPTER_DESC1 desc{};
                if (FAILED(adapter->GetDesc1(&desc)) || IsSoftwareAdapter(desc))
                {
                    adapter.Reset();
                    continue;
                }

                if (requireDisplay && !AdapterDrivesADisplay(adapter.Get()))
                {
                    adapter.Reset();
                    continue;
                }

                if (AdapterSupportsH264Decode(adapter.Get(), width, height))
                {
                    return adapter;
                }

                adapter.Reset();
            }
        }

        // Nothing matched: null means "let D3D12 choose", which is still better than refusing to start.
        return nullptr;
    }
}
