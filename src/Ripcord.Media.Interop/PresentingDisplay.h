#pragma once

// Which display a window is on, and whether that display is in HDR mode right now.
//
// Two callers need it and must agree: VideoCapabilities::IsHdrDisplayAvailable, which the settings page's
// readiness check reads, and VideoRenderer, which decides whether to present HDR10 or ask the driver to
// tone-map. Both used to scan for ANY output in G2084, which answered "is some display here HDR". On a laptop
// whose HDR panel is on while the window sits on an SDR external monitor, that said yes about a screen the
// video was not on: the settings page offered HDR, and the renderer would have presented PQ to an SDR panel.
//
// The window's monitor is resolved with MonitorFromWindow and matched against DXGI_OUTPUT_DESC1::Monitor,
// across every adapter. Every adapter still matters: on a hybrid laptop the display often hangs off the
// integrated GPU while the device decodes on the discrete one, whose EnumOutputs is empty.
//
// A null or stale window handle falls back to the primary monitor, which is the display a window with no
// placement yet would open on - an answer about one real display rather than a guess across all of them.
//
// Same namespace rule as CodecSubtypes.h: a top-level name, because "Ripcord" inside
// winrt::Ripcord::Media::Interop resolves to winrt::Ripcord first.

#include <dxgi1_6.h>
#include <wrl/client.h>

namespace RipcordDisplay
{
    struct PresentingDisplay
    {
        // False when no DXGI output could be matched to the window's monitor (a remote session, a display
        // that went away mid-query). Both flags below are false then too.
        bool Found = false;

        // True only while Windows' "Use HDR" is on for this display: DXGI reports the G2084 colour space in
        // that state alone, so this is a readiness answer, not a hardware one.
        bool Hdr = false;

        // DXGI's MaxLuminance for this display. Diagnostics only; see VideoRenderer.h on why nothing keys
        // off it.
        float MaxNits = 0.0f;
    };

    inline PresentingDisplay DescribeDisplayForWindow(IDXGIFactory1* factory, HWND window)
    {
        PresentingDisplay result{};
        if (!factory)
        {
            return result;
        }

        HMONITOR monitor = MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY);
        if (!monitor)
        {
            return result;
        }

        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        for (UINT a = 0; SUCCEEDED(factory->EnumAdapters1(a, &adapter)); a++)
        {
            Microsoft::WRL::ComPtr<IDXGIOutput> output;
            for (UINT o = 0; SUCCEEDED(adapter->EnumOutputs(o, &output)); o++)
            {
                Microsoft::WRL::ComPtr<IDXGIOutput6> output6;
                DXGI_OUTPUT_DESC1 desc{};
                if (SUCCEEDED(output.As(&output6)) && output6
                    && SUCCEEDED(output6->GetDesc1(&desc))
                    && desc.Monitor == monitor)
                {
                    result.Found = true;
                    result.Hdr = desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                    result.MaxNits = desc.MaxLuminance;
                    return result;
                }
                output.Reset();
            }
            adapter.Reset();
        }

        return result;
    }
}
