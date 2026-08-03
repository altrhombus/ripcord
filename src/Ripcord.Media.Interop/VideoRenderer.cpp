#include "pch.h"
#include "VideoRenderer.h"
#include "VideoCapabilities.h" // shared adapter selection + real decode-capability queries
#include "CodecSubtypes.h"  // one place for the codec -> MF subtype mapping
#include "Ripcord.Media.Interop.VideoRenderer.g.cpp"

#include <d3dcompiler.h>
#include <cstdio>

using Microsoft::WRL::ComPtr;

namespace
{
    void ThrowIfFailed(HRESULT hr)
    {
        if (FAILED(hr))
        {
            winrt::throw_hresult(hr);
        }
    }

    // Compute a centred viewport + scissor that fits a source of aspect srcW:srcH inside a dstW x dstH target
    // without distortion (letterbox/pillarbox). The area outside the viewport is left to the black clear.
    void AspectFit(uint32_t dstW, uint32_t dstH, uint32_t srcW, uint32_t srcH,
                   D3D12_VIEWPORT& viewport, D3D12_RECT& scissor)
    {
        const float dstAspect = dstH > 0 ? static_cast<float>(dstW) / static_cast<float>(dstH) : 1.0f;
        const float srcAspect = srcH > 0 ? static_cast<float>(srcW) / static_cast<float>(srcH) : dstAspect;

        float w, h;
        if (dstAspect > srcAspect) { h = static_cast<float>(dstH); w = h * srcAspect; } // pillarbox
        else                       { w = static_cast<float>(dstW); h = w / srcAspect; } // letterbox

        const float x = (static_cast<float>(dstW) - w) * 0.5f;
        const float y = (static_cast<float>(dstH) - h) * 0.5f;
        viewport = { x, y, w, h, 0.0f, 1.0f };
        scissor = { static_cast<LONG>(x), static_cast<LONG>(y),
                    static_cast<LONG>(x + w), static_cast<LONG>(y + h) };
    }

    // Fullscreen-triangle vertex shader (generates positions/UVs from SV_VertexID, no vertex buffer)
    // plus a pixel shader that samples the uploaded BGRA frame texture.
    constexpr char kShaderSource[] = R"(
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv.x * 2.0 - 1.0, 1.0 - o.uv.y * 2.0, 0.0, 1.0);
            return o;
        }
        Texture2D gTex : register(t0);
        SamplerState gSmp : register(s0);
        float4 PSMain(VSOut i) : SV_Target { return gTex.Sample(gSmp, i.uv); }
    )";

    // NV12 -> RGB in the pixel shader: sample the luma (Y, R8) at full res and the interleaved chroma
    // (UV, R8G8) at half res (the linear sampler upscales chroma for free), then BT.601 limited-range
    // YUV->RGB — the same coefficients the old CPU path used (298/409/100/208/516), yuvCoefficient "bt601"
    // in the launchSpec. Returning float4(r,g,b,1) is correct for either RGBA or BGRA render targets; the
    // format handles the channel order, so (unlike the CPU path) we do not swap R/B by hand.
    constexpr char kNv12ShaderSource[] = R"(
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv.x * 2.0 - 1.0, 1.0 - o.uv.y * 2.0, 0.0, 1.0);
            return o;
        }
        // uMatrix: 0 = BT.601, 1 = BT.709. Both limited-range in, full-range RGB out.
        cbuffer Params : register(b0) { uint uMatrix; uint3 uPad; };
        Texture2D gY : register(t0);
        Texture2D gUV : register(t1);
        SamplerState gSmp : register(s0);
        float4 PSMain(VSOut i) : SV_Target
        {
            float  yv = gY.Sample(gSmp, i.uv).r * 255.0;
            float2 cv = gUV.Sample(gSmp, i.uv).rg * 255.0;
            float c = yv - 16.0;
            float d = cv.x - 128.0;
            float e = cv.y - 128.0;

            // Coefficients x256. 601: 1.164/1.596/0.391/0.813/2.018. 709: 1.164/1.793/0.213/0.533/2.112.
            float kr = (uMatrix == 1u) ? 459.0 : 409.0;
            float kgu = (uMatrix == 1u) ?  55.0 : 100.0;
            float kgv = (uMatrix == 1u) ? 136.0 : 208.0;
            float kb = (uMatrix == 1u) ? 541.0 : 516.0;

            float r = (298.0 * c + kr * e + 128.0) / 256.0;
            float g = (298.0 * c - kgu * d - kgv * e + 128.0) / 256.0;
            float b = (298.0 * c + kb * d + 128.0) / 256.0;
            return float4(saturate(float3(r, g, b) / 255.0), 1.0);
        }
    )";

    // Spatial upscale of the decode-res BGRA frame to the panel: mode 0 = bilinear (hardware sampler),
    // mode 1 = Catmull-Rom bicubic (16 point-sampled taps) for a sharper result. uSrcSize is the source
    // (decode) texture size in texels, needed to place the bicubic taps.
    constexpr char kUpscaleShaderSource[] = R"(
        cbuffer Params : register(b0) { uint uMode; float2 uSrcSize; float uPad; };
        Texture2D gTex : register(t0);
        SamplerState gLinear : register(s0);
        SamplerState gPoint  : register(s1);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv.x * 2.0 - 1.0, 1.0 - o.uv.y * 2.0, 0.0, 1.0);
            return o;
        }

        float4 crWeights(float f)
        {
            float f2 = f * f, f3 = f2 * f;
            return float4(
                0.5 * (-f3 + 2.0 * f2 - f),
                0.5 * (3.0 * f3 - 5.0 * f2 + 2.0),
                0.5 * (-3.0 * f3 + 4.0 * f2 + f),
                0.5 * (f3 - f2));
        }

        float4 PSMain(VSOut i) : SV_Target
        {
            if (uMode == 0u)
            {
                return float4(gTex.Sample(gLinear, i.uv).rgb, 1.0);
            }

            float2 invTex = 1.0 / uSrcSize;
            float2 coord = i.uv * uSrcSize - 0.5;
            float2 basec = floor(coord);
            float2 f = coord - basec;
            float4 wx = crWeights(f.x);
            float4 wy = crWeights(f.y);

            float3 acc = float3(0.0, 0.0, 0.0);
            [unroll] for (int y = 0; y < 4; y++)
            {
                [unroll] for (int x = 0; x < 4; x++)
                {
                    float2 tc = (basec + float2(x - 1, y - 1) + 0.5) * invTex;
                    acc += gTex.SampleLevel(gPoint, tc, 0.0).rgb * wx[x] * wy[y];
                }
            }
            return float4(saturate(acc), 1.0);
        }
    )";

    ComPtr<ID3DBlob> CompileSource(const char* src, size_t len, const char* entry, const char* target)
    {
        ComPtr<ID3DBlob> blob;
        ComPtr<ID3DBlob> errors;
        UINT flags = 0;
#if defined(_DEBUG)
        flags |= D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#endif
        HRESULT hr = D3DCompile(src, len, nullptr, nullptr, nullptr, entry, target, flags, 0, &blob, &errors);
        if (FAILED(hr))
        {
            winrt::throw_hresult(hr);
        }

        return blob;
    }

    ComPtr<ID3DBlob> Compile(const char* entry, const char* target)
    {
        return CompileSource(kShaderSource, sizeof(kShaderSource) - 1, entry, target);
    }
}

namespace winrt::Ripcord::Media::Interop::implementation
{
    VideoRenderer::~VideoRenderer()
    {
        Shutdown();
    }

    winrt::hstring VideoRenderer::ActiveAdapterDescription()
    {
        return m_adapterDescription;
    }

    bool VideoRenderer::IsDeviceLost()
    {
        return m_deviceLost;
    }

    int32_t VideoRenderer::DeviceRemovedReason()
    {
        return static_cast<int32_t>(m_deviceRemovedReason);
    }

    bool VideoRenderer::NoteDeviceLoss(HRESULT hr)
    {
        if (m_deviceLost)
        {
            return true;
        }

        if (hr != DXGI_ERROR_DEVICE_REMOVED && hr != DXGI_ERROR_DEVICE_RESET)
        {
            return false;
        }

        m_deviceLost = true;
        // GetDeviceRemovedReason gives the underlying cause (hung, reset, driver upgrade), which is far more
        // useful in a bug report than the generic DEVICE_REMOVED the call site saw.
        m_deviceRemovedReason = m_device ? m_device->GetDeviceRemovedReason() : hr;
        return true;
    }

    void VideoRenderer::Initialize(
        uint32_t width,
        uint32_t height,
        Ripcord::Media::Interop::GpuSelection gpuSelection,
        uint64_t specificLuid)
    {
        m_width = width == 0 ? 1 : width;
        m_height = height == 0 ? 1 : height;

        UINT dxgiFactoryFlags = 0;
#if defined(_DEBUG)
        // The debug layer needs the "Graphics Tools" optional feature installed. It turns silent
        // GPU errors into validation messages in the debugger Output window - our shared diagnostic.
        {
            ComPtr<ID3D12Debug> debugController;
            if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debugController))))
            {
                debugController->EnableDebugLayer();
                dxgiFactoryFlags |= DXGI_CREATE_FACTORY_DEBUG;
            }
        }
#endif

        ComPtr<IDXGIFactory4> factory;
        ThrowIfFailed(CreateDXGIFactory2(dxgiFactoryFlags, IID_PPV_ARGS(&factory)));

        // Pick the adapter deliberately instead of taking DXGI's default. See GpuSelection in the IDL for why
        // "highest performance" is the wrong default for a decode-and-blit workload on a hybrid laptop.
        ComPtr<IDXGIAdapter1> adapter;
        ComPtr<IDXGIFactory6> factory6;
        if (SUCCEEDED(factory.As(&factory6)))
        {
            adapter = VideoCapabilities::ChooseAdapter(
                factory6.Get(), gpuSelection, specificLuid, m_width, m_height);
        }

        HRESULT deviceHr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&m_device));
        if (FAILED(deviceHr) && adapter)
        {
            // The chosen adapter refused; rather than failing outright, fall back to DXGI's default so the
            // user still gets a stream (a pinned adapter that has since been removed lands here).
            adapter.Reset();
            deviceHr = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&m_device));
        }
        ThrowIfFailed(deviceHr);

        // Record which adapter we ended up on. Reading it back from the device's LUID (rather than trusting
        // the requested one) means the diagnostics report reality, including after the fallback above.
        {
            LUID activeLuid = m_device->GetAdapterLuid();
            ComPtr<IDXGIAdapter1> active;
            for (UINT i = 0; SUCCEEDED(factory->EnumAdapters1(i, &active)); i++)
            {
                DXGI_ADAPTER_DESC1 desc{};
                if (SUCCEEDED(active->GetDesc1(&desc))
                    && desc.AdapterLuid.LowPart == activeLuid.LowPart
                    && desc.AdapterLuid.HighPart == activeLuid.HighPart)
                {
                    m_adapterDescription = winrt::hstring{ desc.Description };
                    break;
                }

                active.Reset();
            }
        }

        D3D12_COMMAND_QUEUE_DESC queueDesc = {};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        ThrowIfFailed(m_device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&m_commandQueue)));

        DXGI_SWAP_CHAIN_DESC1 swapChainDesc = {};
        swapChainDesc.Width = m_width;
        swapChainDesc.Height = m_height;
        swapChainDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        swapChainDesc.SampleDesc.Count = 1;
        swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapChainDesc.BufferCount = FrameCount;
        swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swapChainDesc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED; // XAML SwapChainPanel composition
        swapChainDesc.Scaling = DXGI_SCALING_STRETCH;
        // The waitable object is how a low-latency present loop paces itself: DXGI signals it when the
        // presentation engine is ready for another frame, so we block there (briefly, at most one frame ahead)
        // instead of draining the GPU after every Present.
        swapChainDesc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        m_swapChainFlags = swapChainDesc.Flags;

        ComPtr<IDXGISwapChain1> swapChain1;
        ThrowIfFailed(factory->CreateSwapChainForComposition(
            m_commandQueue.Get(), &swapChainDesc, nullptr, &swapChain1));
        ThrowIfFailed(swapChain1.As(&m_swapChain));

        // One frame of latency: present the frame we just drew, don't let DXGI queue a backlog ahead of it.
        ThrowIfFailed(m_swapChain->SetMaximumFrameLatency(1));
        m_frameLatencyWaitable = m_swapChain->GetFrameLatencyWaitableObject();

        D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc = {};
        rtvHeapDesc.NumDescriptors = FrameCount;
        rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        ThrowIfFailed(m_device->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&m_rtvHeap)));
        m_rtvDescriptorSize = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

        CreateRenderTargets();

        for (uint32_t i = 0; i < FrameCount; i++)
        {
            ThrowIfFailed(m_device->CreateCommandAllocator(
                D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&m_commandAllocators[i])));
            m_frameFenceValues[i] = 0;
        }

        ThrowIfFailed(m_device->CreateCommandList(
            0, D3D12_COMMAND_LIST_TYPE_DIRECT, m_commandAllocators[0].Get(), nullptr, IID_PPV_ARGS(&m_commandList)));
        ThrowIfFailed(m_commandList->Close());

        ThrowIfFailed(m_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&m_fence)));
        // Values are allocated by pre-increment, so the first signalled value is 1 and 0 stays reserved as
        // "this slot has never been submitted".
        m_fenceValue = 0;
        m_fenceEvent = CreateEvent(nullptr, FALSE, FALSE, nullptr);
        if (m_fenceEvent == nullptr)
        {
            ThrowIfFailed(HRESULT_FROM_WIN32(GetLastError()));
        }

        CreatePipeline();
        CreateNv12Pipeline();
        CreateUpscalePipeline();
    }

    void VideoRenderer::CreatePipeline()
    {
        D3D12_DESCRIPTOR_RANGE srvRange = {};
        srvRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRange.NumDescriptors = 1;
        srvRange.BaseShaderRegister = 0;
        srvRange.OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;

        D3D12_ROOT_PARAMETER rootParam = {};
        rootParam.ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        rootParam.DescriptorTable.NumDescriptorRanges = 1;
        rootParam.DescriptorTable.pDescriptorRanges = &srvRange;
        rootParam.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC sampler = {};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.ShaderRegister = 0;
        sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        sampler.MaxLOD = D3D12_FLOAT32_MAX;

        D3D12_ROOT_SIGNATURE_DESC rsDesc = {};
        rsDesc.NumParameters = 1;
        rsDesc.pParameters = &rootParam;
        rsDesc.NumStaticSamplers = 1;
        rsDesc.pStaticSamplers = &sampler;
        rsDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;

        ComPtr<ID3DBlob> signature;
        ComPtr<ID3DBlob> error;
        ThrowIfFailed(D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error));
        ThrowIfFailed(m_device->CreateRootSignature(
            0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&m_rootSignature)));

        ComPtr<ID3DBlob> vs = Compile("VSMain", "vs_5_1");
        ComPtr<ID3DBlob> ps = Compile("PSMain", "ps_5_1");

        D3D12_RASTERIZER_DESC rast = {};
        rast.FillMode = D3D12_FILL_MODE_SOLID;
        rast.CullMode = D3D12_CULL_MODE_NONE;
        rast.DepthClipEnable = TRUE;

        D3D12_BLEND_DESC blend = {};
        blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

        D3D12_GRAPHICS_PIPELINE_STATE_DESC psoDesc = {};
        psoDesc.pRootSignature = m_rootSignature.Get();
        psoDesc.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
        psoDesc.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
        psoDesc.RasterizerState = rast;
        psoDesc.BlendState = blend;
        psoDesc.DepthStencilState.DepthEnable = FALSE;
        psoDesc.DepthStencilState.StencilEnable = FALSE;
        psoDesc.SampleMask = UINT_MAX;
        psoDesc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        psoDesc.NumRenderTargets = 1;
        psoDesc.RTVFormats[0] = DXGI_FORMAT_B8G8R8A8_UNORM;
        psoDesc.SampleDesc.Count = 1;
        ThrowIfFailed(m_device->CreateGraphicsPipelineState(&psoDesc, IID_PPV_ARGS(&m_pipelineState)));

        D3D12_DESCRIPTOR_HEAP_DESC srvHeapDesc = {};
        srvHeapDesc.NumDescriptors = 1;
        srvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        srvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ThrowIfFailed(m_device->CreateDescriptorHeap(&srvHeapDesc, IID_PPV_ARGS(&m_srvHeap)));
    }

    void VideoRenderer::CreateRenderTargets()
    {
        D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        for (uint32_t i = 0; i < FrameCount; i++)
        {
            ThrowIfFailed(m_swapChain->GetBuffer(i, IID_PPV_ARGS(&m_renderTargets[i])));
            m_device->CreateRenderTargetView(m_renderTargets[i].Get(), nullptr, rtvHandle);
            rtvHandle.ptr += m_rtvDescriptorSize;
        }
    }

    void VideoRenderer::EnsureFrameTexture(uint32_t width, uint32_t height)
    {
        if (m_frameTexture && width == m_frameTextureWidth && height == m_frameTextureHeight)
        {
            return;
        }

        WaitForGpu();
        m_frameTexture.Reset();
        m_frameUpload.Reset();
        m_frameTextureWidth = width;
        m_frameTextureHeight = height;

        D3D12_HEAP_PROPERTIES defaultHeap = {};
        defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;

        D3D12_RESOURCE_DESC texDesc = {};
        texDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        texDesc.Width = width;
        texDesc.Height = height;
        texDesc.DepthOrArraySize = 1;
        texDesc.MipLevels = 1;
        texDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        texDesc.SampleDesc.Count = 1;
        texDesc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;

        ThrowIfFailed(m_device->CreateCommittedResource(
            &defaultHeap, D3D12_HEAP_FLAG_NONE, &texDesc,
            D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&m_frameTexture)));
        m_frameTextureState = D3D12_RESOURCE_STATE_COPY_DEST;

        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint = {};
        UINT numRows = 0;
        UINT64 rowSizeBytes = 0;
        UINT64 totalBytes = 0;
        m_device->GetCopyableFootprints(&texDesc, 0, 1, 0, &footprint, &numRows, &rowSizeBytes, &totalBytes);
        m_frameUploadRowPitch = footprint.Footprint.RowPitch;

        D3D12_HEAP_PROPERTIES uploadHeap = {};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;

        D3D12_RESOURCE_DESC bufDesc = {};
        bufDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bufDesc.Width = totalBytes;
        bufDesc.Height = 1;
        bufDesc.DepthOrArraySize = 1;
        bufDesc.MipLevels = 1;
        bufDesc.Format = DXGI_FORMAT_UNKNOWN;
        bufDesc.SampleDesc.Count = 1;
        bufDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

        ThrowIfFailed(m_device->CreateCommittedResource(
            &uploadHeap, D3D12_HEAP_FLAG_NONE, &bufDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_frameUpload)));

        m_device->CreateShaderResourceView(
            m_frameTexture.Get(), nullptr, m_srvHeap->GetCPUDescriptorHandleForHeapStart());
    }

    void VideoRenderer::UploadFrameTexture(const uint8_t* bgra, uint32_t width, uint32_t height)
    {
        const uint32_t srcRowBytes = width * 4;
        uint8_t* mapped = nullptr;
        D3D12_RANGE readRange = { 0, 0 };
        ThrowIfFailed(m_frameUpload->Map(0, &readRange, reinterpret_cast<void**>(&mapped)));
        for (uint32_t row = 0; row < height; row++)
        {
            const uint8_t* src = bgra + static_cast<size_t>(row) * srcRowBytes;
            uint8_t* dst = mapped + static_cast<size_t>(row) * m_frameUploadRowPitch;
            memcpy(dst, src, srcRowBytes);
        }

        m_frameUpload->Unmap(0, nullptr);
    }

    void VideoRenderer::PresentBgra(winrt::array_view<uint8_t const> bgra, uint32_t width, uint32_t height)
    {
        if (width == 0 || height == 0 || bgra.size() < static_cast<size_t>(width) * height * 4)
        {
            return;
        }

        PresentBgraInternal(bgra.data(), width, height);
    }

    void VideoRenderer::PresentBgraInternal(const uint8_t* bgra, uint32_t width, uint32_t height)
    {
        if (!m_swapChain || m_deviceLost)
        {
            return;
        }

        // This is the Layer-2 test-pattern path, not the live video path, and it keeps a single (unringed)
        // upload buffer. Drain before rewriting it so the CPU can't overwrite bytes a previous frame's copy is
        // still reading. The cost is irrelevant here (a 30 Hz diagnostic tick); the live paths are pipelined.
        WaitForGpu();

        EnsureFrameTexture(width, height);
        UploadFrameTexture(bgra, width, height);

        const UINT frameIndex = BeginFrame();

        auto transition = [this](ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
        {
            D3D12_RESOURCE_BARRIER barrier = {};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = resource;
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrier.Transition.StateBefore = before;
            barrier.Transition.StateAfter = after;
            m_commandList->ResourceBarrier(1, &barrier);
        };

        // Upload -> frame texture.
        if (m_frameTextureState != D3D12_RESOURCE_STATE_COPY_DEST)
        {
            transition(m_frameTexture.Get(), m_frameTextureState, D3D12_RESOURCE_STATE_COPY_DEST);
        }

        D3D12_RESOURCE_DESC texDesc = m_frameTexture->GetDesc();
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint = {};
        m_device->GetCopyableFootprints(&texDesc, 0, 1, 0, &footprint, nullptr, nullptr, nullptr);

        D3D12_TEXTURE_COPY_LOCATION dst = {};
        dst.pResource = m_frameTexture.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.SubresourceIndex = 0;

        D3D12_TEXTURE_COPY_LOCATION src = {};
        src.pResource = m_frameUpload.Get();
        src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        src.PlacedFootprint = footprint;

        m_commandList->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        transition(m_frameTexture.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        m_frameTextureState = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;

        // Draw the frame texture to the current back buffer.
        transition(m_renderTargets[frameIndex].Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);

        D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtvHandle.ptr += static_cast<SIZE_T>(frameIndex) * m_rtvDescriptorSize;
        m_commandList->OMSetRenderTargets(1, &rtvHandle, FALSE, nullptr);

        const float clearColor[] = { 0.0f, 0.0f, 0.0f, 1.0f };
        m_commandList->ClearRenderTargetView(rtvHandle, clearColor, 0, nullptr);

        m_commandList->SetGraphicsRootSignature(m_rootSignature.Get());
        ID3D12DescriptorHeap* heaps[] = { m_srvHeap.Get() };
        m_commandList->SetDescriptorHeaps(1, heaps);
        m_commandList->SetGraphicsRootDescriptorTable(0, m_srvHeap->GetGPUDescriptorHandleForHeapStart());
        m_commandList->SetPipelineState(m_pipelineState.Get());

        D3D12_VIEWPORT viewport = { 0.0f, 0.0f, static_cast<float>(m_width), static_cast<float>(m_height), 0.0f, 1.0f };
        D3D12_RECT scissor = { 0, 0, static_cast<LONG>(m_width), static_cast<LONG>(m_height) };
        m_commandList->RSSetViewports(1, &viewport);
        m_commandList->RSSetScissorRects(1, &scissor);
        m_commandList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        m_commandList->DrawInstanced(3, 1, 0, 0);

        transition(m_renderTargets[frameIndex].Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);

        // Was Present(1, 0) — a full vsync wait, which on top of the drain above made this path cost two
        // stalls per frame. EndFrame presents with sync interval 0 like every other path.
        EndFrame(frameIndex);
    }

    /// Enumerate and activate a SYNCHRONOUS decoder MFT for the codec, recording the subtype that bound and the
    /// transform's friendly name.
    ///
    /// Synchronous is not a preference, it is a requirement: the decode loop calls ProcessInput/ProcessOutput
    /// directly, whereas an async MFT (what MFT_ENUM_FLAG_HARDWARE returns — the vendor "hardware category"
    /// transforms) only produces output in response to METransformHaveOutput events and would silently never
    /// yield a frame here. GPU decode is still obtained the same way the H.264 path already gets it: a sync MFT
    /// plus MFT_MESSAGE_SET_D3D_MANAGER, i.e. DXVA. MFT_ENUM_FLAG_SORTANDFILTER puts the preferred transform
    /// first and drops ones disabled by policy.
    bool VideoRenderer::CreateDecoderForCodec(VideoCodecKind codec)
    {
        const RipcordCodecSupport::SubtypePair pair = RipcordCodecSupport::SubtypesFor(codec);
        const GUID subtypes[2]{ pair.Primary, pair.ElementaryStream };

        for (const GUID& subtype : subtypes)
        {
            MFT_REGISTER_TYPE_INFO inputInfo{ MFMediaType_Video, subtype };
            IMFActivate** activates = nullptr;
            UINT32 count = 0;

            HRESULT hrEnum = MFTEnumEx(
                MFT_CATEGORY_VIDEO_DECODER,
                MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
                &inputInfo,
                nullptr,
                &activates,
                &count);

            if (FAILED(hrEnum) || count == 0)
            {
                if (activates)
                {
                    CoTaskMemFree(activates);
                }
                continue;
            }

            ComPtr<IMFTransform> transform;
            std::wstring name;

            for (UINT32 i = 0; i < count; ++i)
            {
                if (!transform)
                {
                    ComPtr<IMFTransform> candidate;
                    if (SUCCEEDED(activates[i]->ActivateObject(IID_PPV_ARGS(&candidate))) && candidate)
                    {
                        WCHAR* friendly = nullptr;
                        UINT32 friendlyLength = 0;
                        if (SUCCEEDED(activates[i]->GetAllocatedString(
                                MFT_FRIENDLY_NAME_Attribute, &friendly, &friendlyLength)) && friendly)
                        {
                            name.assign(friendly);
                            CoTaskMemFree(friendly);
                        }

                        transform = candidate;
                    }
                }

                activates[i]->Release();
            }

            CoTaskMemFree(activates);

            if (transform)
            {
                m_decoder = transform;
                m_inputSubtype = subtype;
                m_decoderName = name.empty() ? L"unnamed decoder MFT" : name;
                return true;
            }
        }

        // Last resort for H.264 only: the in-box decoder by CLSID. This is the transform the pipeline used
        // exclusively before codec selection existed, so falling back to it cannot be worse than the previous
        // behaviour if enumeration is unavailable for some reason.
        if (codec == VideoCodecKind::H264
            && SUCCEEDED(CoCreateInstance(
                CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&m_decoder))))
        {
            m_inputSubtype = MFVideoFormat_H264;
            m_decoderName = L"Microsoft H264 Video Decoder MFT (by CLSID)";
            return true;
        }

        return false;
    }

    void VideoRenderer::SetCodec(VideoCodecKind codec)
    {
        // Only meaningful before the MFT exists: its input type is fixed at creation.
        if (!m_decoder)
        {
            m_codec = codec;
        }
    }

    /// A compact, printable name for a video subtype GUID. Most MF video subtypes embed a FourCC in the first
    /// four bytes, so unknown ones still render as something recognisable rather than a raw GUID.
    static std::wstring SubtypeName(const GUID& subtype)
    {
        if (subtype == MFVideoFormat_NV12) return L"NV12";
        if (subtype == MFVideoFormat_P010) return L"P010";  // 10-bit — NOT handled by the NV12 present path
        if (subtype == MFVideoFormat_YUY2) return L"YUY2";
        if (subtype == GUID_NULL)          return L"none";

        wchar_t fourcc[5]{};
        for (int i = 0; i < 4; ++i)
        {
            const auto ch = static_cast<wchar_t>(reinterpret_cast<const uint8_t*>(&subtype.Data1)[i]);
            fourcc[i] = (ch >= 32 && ch < 127) ? ch : L'?';
        }

        return std::wstring{ fourcc };
    }

    hstring VideoRenderer::DecoderDiagnostic()
    {
        // What the decode loop is actually seeing. Added because a black screen with healthy-looking decode and
        // present counters has several distinct causes — no sample ever produced, a sample in a pixel format the
        // present path does not handle, or a sample whose dimensions were never reported — and guessing between
        // them from the outside costs a test cycle each time.
        GUID currentSubtype = GUID_NULL;
        if (m_decoder)
        {
            ComPtr<IMFMediaType> current;
            if (SUCCEEDED(m_decoder->GetOutputCurrentType(0, &current)) && current)
            {
                current->GetGUID(MF_MT_SUBTYPE, &currentSubtype);
            }
        }

        wchar_t buffer[256]{};
        swprintf_s(
            buffer,
            L"samples=%llu hr=0x%08X out=%s coded=%ux%u geom=%d nal=%02X,%02X head=%02X%02X%02X%02X tries=%d",
            static_cast<unsigned long long>(m_producedSamples),
            static_cast<unsigned int>(m_lastOutputHr),
            SubtypeName(currentSubtype).c_str(),
            m_decodeWidth,
            m_decodeHeight,
            m_geomStage,
            m_firstNal0,
            m_firstNal1,
            m_payloadHead[0],
            m_payloadHead[1],
            m_payloadHead[2],
            m_payloadHead[3],
            m_detectAttempts);

        return hstring{ buffer };
    }

    hstring VideoRenderer::DecoderDescription()
    {
        // Non-ASCII in these literals MUST use universal character names (\u00B7, \u2014), not raw UTF-8 bytes:
        // MSVC decodes narrow and wide literals with the system codepage unless /utf-8 is on the command line, so
        // a literal middle dot rendered on screen as "Ã‚Â·". Escapes are codepage-independent.
        if (m_decoderName.empty())
        {
            return hstring{ L"no decoder created yet" };
        }

        std::wstring desc = m_decoderName;
        desc += m_hardwareDecode ? L" (DXVA)" : L" (software)";
        // Always say what the pixel format is, not only in the 10-bit case. An SDR session used to add nothing
        // at all here, so "HEVC (DXVA)" alone left you unable to tell a working SDR stream from one where the
        // format probe had silently not run - and made A/B-ing an HDR toggle needlessly hard.
        desc += m_tenBitOutput ? L" \u00B7 10-bit P010" : L" \u00B7 8-bit NV12";

        // What the STREAM signalled, which is not always what we asked for - the console ignores
        // yuvCoefficient outright, so treat dynamicRange the same way and report the measurement.
        desc += L" \u00B7 ";
        if (m_transferFunctionSignalled)
        {
            switch (m_transferFunction)
            {
            case MFVideoTransFunc_2084: desc += L"PQ"; break;
            case MFVideoTransFunc_HLG:  desc += L"HLG"; break;
            case MFVideoTransFunc_709:  desc += L"BT.709 gamma"; break;
            case MFVideoTransFunc_sRGB: desc += L"sRGB gamma"; break;
            default:
                desc += L"transfer " + std::to_wstring(m_transferFunction);
                break;
            }
        }
        else
        {
            desc += m_tenBitOutput ? L"transfer unsignalled (assuming PQ)" : L"transfer unsignalled";
        }

        if (m_videoPrimariesSignalled)
        {
            desc += m_videoPrimaries == MFVideoPrimaries_BT2020 ? L" BT.2020" : L" BT.709";
        }

        if (m_toneMappedByDriver)
        {
            desc += L" \u00B7 tone-mapped to SDR";
        }
        if (m_tenBitUnrenderable)
        {
            desc += L" \u2014 10-bit needs the zero-copy path, which is unavailable here";
        }
        if (m_codecMismatch)
        {
            // The console did not send what was requested. Say so plainly: silently substituting the decoder
            // would leave a "HEVC" setting that visibly does nothing.
            desc += m_codec == VideoCodecKind::Hevc
                ? L" \u2014 stream is HEVC, overriding the H.264 request"
                : L" \u2014 console ignored the HEVC request and sent H.264";
        }

        return hstring{ desc };
    }

    void VideoRenderer::EnsureDecoder()
    {
        if (m_decoder)
        {
            return;
        }

        if (!m_mfStarted)
        {
            ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_LITE));
            m_mfStarted = true;
        }

        if (!CreateDecoderForCodec(m_codec))
        {
            throw winrt::hresult_error(
                MF_E_TOPO_CODEC_NOT_FOUND,
                m_codec == VideoCodecKind::Hevc
                    ? L"No synchronous HEVC decoder MFT is available on this system."
                    : L"No synchronous H.264 decoder MFT is available on this system.");
        }

        // Low-latency mode: emit each frame as soon as it is decoded instead of holding a multi-frame
        // reorder/throughput window. The MS H.264 decoder otherwise buffers several frames internally before
        // it will output ANY of them, which for a live stream is pure added latency (~150-250 ms at 30 fps)
        // on top of the console's own encode buffering. Set on the transform's attribute store before
        // streaming begins; best-effort (a decoder that ignores it just behaves as before).
        ComPtr<IMFAttributes> decoderAttributes;
        if (SUCCEEDED(m_decoder->GetAttributes(&decoderAttributes)) && decoderAttributes)
        {
            decoderAttributes->SetUINT32(MF_LOW_LATENCY, TRUE);
        }

        // Best-effort: hand the MFT a D3D11 device manager so it decodes on the GPU (DXVA) instead of the CPU,
        // while staying a synchronous transform. Must precede SetInputType. On any failure we simply proceed
        // in software — correct, just more CPU (and the source of the occasional decode-overrun stutter).
        if (TryEnableHardwareDecode())
        {
            HRESULT hrManager = m_decoder->ProcessMessage(
                MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(m_dxgiDeviceManager.Get()));
            m_hardwareDecode = SUCCEEDED(hrManager);
        }

        ComPtr<IMFMediaType> inputType;
        ThrowIfFailed(MFCreateMediaType(&inputType));
        ThrowIfFailed(inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        ThrowIfFailed(inputType->SetGUID(MF_MT_SUBTYPE, m_inputSubtype));
        ThrowIfFailed(inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        // A frame size on the input type gives the output types a valid size so SetOutputType can
        // succeed before any input is processed. The decoder corrects it via a stream-change once it
        // reads the stream's actual SPS (see DrainDecoderToLatest).
        ThrowIfFailed(MFSetAttributeSize(inputType.Get(), MF_MT_FRAME_SIZE, 1280, 720));
        ThrowIfFailed(MFSetAttributeRatio(inputType.Get(), MF_MT_FRAME_RATE, 30, 1));
        ThrowIfFailed(m_decoder->SetInputType(0, inputType.Get(), 0));

        // The MS H.264 decoder requires an output type set before it will accept input.
        if (!ConfigureDecoderOutput())
        {
            throw winrt::hresult_error(MF_E_TRANSFORM_TYPE_NOT_SET, L"No NV12 decoder output type available.");
        }

        ThrowIfFailed(m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
        ThrowIfFailed(m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
    }

    bool VideoRenderer::TryEnableHardwareDecode()
    {
        // A dedicated D3D11 device (MF/DXVA has no D3D12 backend) used ONLY as the decoder's acceleration
        // device — it never renders. VIDEO_SUPPORT is required for DXVA; BGRA_SUPPORT is harmless and commonly
        // expected. Any failure here is non-fatal: we return false and the caller stays on software decode.
        const UINT creationFlags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };

        HRESULT hr = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, creationFlags,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
            &m_decodeD3d11Device, nullptr, &m_decodeD3d11Context);
        if (FAILED(hr))
        {
            return false;
        }

        // MF may touch the device from its own worker threads; the device manager arbitrates access, but the
        // device must be flagged multithread-protected first.
        ComPtr<ID3D11Multithread> multithread;
        if (SUCCEEDED(m_decodeD3d11Device.As(&multithread)))
        {
            multithread->SetMultithreadProtected(TRUE);
        }

        hr = MFCreateDXGIDeviceManager(&m_dxgiResetToken, &m_dxgiDeviceManager);
        if (FAILED(hr))
        {
            m_dxgiDeviceManager.Reset();
            return false;
        }

        hr = m_dxgiDeviceManager->ResetDevice(m_decodeD3d11Device.Get(), m_dxgiResetToken);
        if (FAILED(hr))
        {
            m_dxgiDeviceManager.Reset();
            return false;
        }

        // Prepare the cross-API fence for zero-copy. If it can't be set up (e.g. decode + render devices land
        // on different adapters), we simply keep the CPU readback path — still hardware-decoded, just copied.
        m_zeroCopyDisabled = !TryCreateSharedFence();
        return true;
    }

    bool VideoRenderer::ConfigureDecoderOutput()
    {
        ComPtr<IMFMediaType> outputType;
        // Prefer NV12; accept P010 as a fallback. A 10-bit stream (HEVC Main10, which is what an HDR request
        // would produce) offers P010 and NOT NV12, so an NV12-only search would fail outright and take the whole
        // session with it. Preference order matters: when both are offered the stream is 8-bit and NV12 avoids a
        // pointless widening.
        ComPtr<IMFMediaType> tenBitFallback;
        for (DWORD i = 0;; i++)
        {
            ComPtr<IMFMediaType> candidate;
            HRESULT hr = m_decoder->GetOutputAvailableType(0, i, &candidate);
            if (hr == MF_E_NO_MORE_TYPES)
            {
                break;
            }
            ThrowIfFailed(hr);

            GUID subtype{};
            if (FAILED(candidate->GetGUID(MF_MT_SUBTYPE, &subtype)))
            {
                continue;
            }

            if (subtype == MFVideoFormat_NV12)
            {
                outputType = candidate;
                break;
            }

            if (subtype == MFVideoFormat_P010 && !tenBitFallback)
            {
                tenBitFallback = candidate;
            }
        }

        if (!outputType && tenBitFallback)
        {
            outputType = tenBitFallback;
        }

        m_tenBitOutput = false;
        if (outputType)
        {
            GUID chosen{};
            if (SUCCEEDED(outputType->GetGUID(MF_MT_SUBTYPE, &chosen)))
            {
                m_tenBitOutput = chosen == MFVideoFormat_P010;
            }
        }

        if (!outputType)
        {
            return false;
        }

        ThrowIfFailed(m_decoder->SetOutputType(0, outputType.Get(), 0));

        AdoptOutputGeometry(outputType.Get());
        return true;
    }

    /// Adopt frame size, display aperture, colour matrix, stride and output-buffer sizing from a decoder output
    /// media type.
    ///
    /// Split out of ConfigureDecoderOutput so it can also be applied to the decoder's CURRENT output type after
    /// a frame has been produced. That matters because not every decoder announces its real geometry the same
    /// way: the in-box H.264 MFT raises MF_E_TRANSFORM_STREAM_CHANGE once it reads the SPS, which drives a
    /// re-negotiation, whereas the HEVC Video Extension was observed to publish no usable MF_MT_FRAME_SIZE up
    /// front and never raise a stream change — leaving the cached size at zero, so every decoded frame was
    /// discarded by the "do we know the geometry" gate and the screen stayed black while the decode and present
    /// counters both ticked at full rate.
    /// Last-resort geometry: measure the decoded frame itself.
    ///
    /// Used when a decoder produces frames without ever publishing a usable MF_MT_FRAME_SIZE (observed with the
    /// HEVC Video Extension). A DXVA sample is backed by a D3D11 texture whose descriptor is the real coded size,
    /// which cannot be wrong; a system-memory NV12 buffer can be measured from its pitch and length instead,
    /// since NV12 occupies exactly stride * height * 3/2 bytes.
    ///
    /// Sets the display extent equal to the coded extent because no aperture is available here. That is the same
    /// assumption the pipeline made before aperture handling existed, so at worst it shows a few padding rows —
    /// visibly imperfect, but a picture rather than a black screen.
    bool VideoRenderer::TryGeometryFromSample(IMFSample* produced)
    {
        m_geomStage = 3;

        ComPtr<IMFMediaBuffer> buffer;
        if (FAILED(produced->GetBufferByIndex(0, &buffer)) || !buffer)
        {
            m_geomStage = 4;
            return false;
        }

        uint32_t width = 0;
        uint32_t height = 0;
        uint32_t stride = 0;

        ComPtr<IMFDXGIBuffer> dxgiBuffer;
        if (FAILED(buffer.As(&dxgiBuffer)))
        {
            m_geomStage = 5;   // system-memory buffer, not DXGI-backed
        }
        else
        {
            ComPtr<ID3D11Texture2D> decoded;
            if (FAILED(dxgiBuffer->GetResource(IID_PPV_ARGS(&decoded))) || !decoded)
            {
                m_geomStage = 6;   // DXGI buffer but no D3D11 texture
            }
            else
            {
                D3D11_TEXTURE2D_DESC desc{};
                decoded->GetDesc(&desc);
                width = desc.Width;
                height = desc.Height;
                stride = desc.Width;
            }
        }

        if (width == 0 || height == 0)
        {
            ComPtr<IMF2DBuffer> buffer2d;
            if (SUCCEEDED(buffer.As(&buffer2d)))
            {
                BYTE* scan0 = nullptr;
                LONG pitch = 0;
                if (SUCCEEDED(buffer2d->Lock2D(&scan0, &pitch)) && pitch != 0)
                {
                    const uint32_t rowBytes = static_cast<uint32_t>(pitch < 0 ? -pitch : pitch);
                    DWORD length = 0;
                    if (rowBytes > 0 && SUCCEEDED(buffer->GetCurrentLength(&length)) && length > 0)
                    {
                        // NV12: length = stride * height * 3/2.
                        const uint32_t rows = static_cast<uint32_t>((static_cast<uint64_t>(length) * 2) / (rowBytes * 3));
                        if (rows > 0)
                        {
                            width = rowBytes;
                            height = rows;
                            stride = rowBytes;
                        }
                    }

                    buffer2d->Unlock2D();
                }
            }
        }

        if (width == 0 || height == 0)
        {
            m_geomStage = 7;   // measured nothing usable
            return false;
        }

        m_geomStage = 8;       // recovered
        m_decodeWidth = width;
        m_decodeHeight = height;
        m_decodeStride = stride > 0 ? stride : width;
        m_displayWidth = width & ~1u;
        m_displayHeight = height & ~1u;
        m_displayOffsetX = 0;
        m_displayOffsetY = 0;

        MFT_OUTPUT_STREAM_INFO info{};
        if (SUCCEEDED(m_decoder->GetOutputStreamInfo(0, &info)))
        {
            m_decoderProvidesSamples =
                (info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
            const uint32_t generous = m_decodeWidth * m_decodeHeight * 2;
            m_outputBufferSize = info.cbSize > generous ? info.cbSize : generous;
            m_outputAlignment = info.cbAlignment;
        }

        return true;
    }

    void VideoRenderer::AdoptOutputGeometry(IMFMediaType* type)
    {
        UINT32 width = 0, height = 0;
        if (SUCCEEDED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height)) && width > 0 && height > 0)
        {
            m_decodeWidth = width;
            m_decodeHeight = height;
        }

        // MF_MT_FRAME_SIZE is the *coded* size, which H.264 rounds up to whole 16-pixel macroblocks: 1080 is not
        // a multiple of 16, so a 1920x1080 stream is coded as 1920x1088 and the SPS carries a cropping rectangle
        // saying "show only the top 1080 rows". Those extra 8 rows hold encoder padding, not picture — using
        // them shows as a band of smeared/repeated pixels along the bottom edge plus a slight vertical stretch,
        // because the whole 1088 gets aspect-fitted as if it were the real frame.
        //
        // MF exposes that rectangle as MF_MT_MINIMUM_DISPLAY_APERTURE. Default to the coded size so a stream
        // without an aperture (or an older decoder) behaves exactly as before.
        m_displayWidth = m_decodeWidth;
        m_displayHeight = m_decodeHeight;
        m_displayOffsetX = 0;
        m_displayOffsetY = 0;

        MFVideoArea aperture{};
        UINT32 blobSize = 0;
        if (SUCCEEDED(type->GetBlob(
                MF_MT_MINIMUM_DISPLAY_APERTURE,
                reinterpret_cast<UINT8*>(&aperture),
                sizeof(aperture),
                &blobSize))
            && blobSize >= sizeof(aperture)
            && aperture.Area.cx > 0
            && aperture.Area.cy > 0)
        {
            // MFOffset is a 16.16 fixed-point pair; the integer part is all we need for a pixel offset.
            const uint32_t offsetX = static_cast<uint32_t>(aperture.OffsetX.value);
            const uint32_t offsetY = static_cast<uint32_t>(aperture.OffsetY.value);
            const uint32_t apertureW = static_cast<uint32_t>(aperture.Area.cx);
            const uint32_t apertureH = static_cast<uint32_t>(aperture.Area.cy);

            // Never trust it past the coded bounds — a bogus aperture would read outside the decoded buffer.
            if (offsetX + apertureW <= m_decodeWidth && offsetY + apertureH <= m_decodeHeight)
            {
                m_displayWidth = apertureW;
                m_displayHeight = apertureH;
                m_displayOffsetX = offsetX;
                m_displayOffsetY = offsetY;
            }
        }

        // Which YCbCr matrix the stream actually signals (H.264 SPS VUI matrix_coefficients, surfaced by MF as
        // MF_MT_YUV_MATRIX). Reading it beats assuming: BT.601 is correct for SD but BT.709 is standard for HD,
        // and decoding 709 content with 601 coefficients shifts every colour (visibly oversaturated reds).
        // Absent signalling keeps today's known-good BT.601 behaviour rather than guessing by resolution.
        UINT32 signalledMatrix = 0;
        if (SUCCEEDED(type->GetUINT32(MF_MT_YUV_MATRIX, &signalledMatrix)))
        {
            // MFVideoTransferMatrix: 1 = BT.709, 2 = BT.601, 3 = SMPTE240M.
            m_yuvMatrix = signalledMatrix == MFVideoTransferMatrix_BT709 ? 1 : 0;
            m_yuvMatrixSignalled = true;
        }

        // Transfer function and primaries, read for the same reason and from the same place. The renderer used
        // to infer "PQ" from 10-bit output alone, which is not sound - HEVC Main10 is a bit depth, not a
        // transfer function, and a 10-bit BT.709 stream declared as PQ gets tone-mapped when it should be left
        // alone. Reading also settles what the console actually does with a dynamicRange:"HDR" request, which
        // until now was only ever inferred.
        UINT32 signalledTransfer = 0;
        if (SUCCEEDED(type->GetUINT32(MF_MT_TRANSFER_FUNCTION, &signalledTransfer))
            && signalledTransfer != MFVideoTransFunc_Unknown)
        {
            m_transferFunction = signalledTransfer;
            m_transferFunctionSignalled = true;
        }

        UINT32 signalledPrimaries = 0;
        if (SUCCEEDED(type->GetUINT32(MF_MT_VIDEO_PRIMARIES, &signalledPrimaries))
            && signalledPrimaries != MFVideoPrimaries_Unknown)
        {
            m_videoPrimaries = signalledPrimaries;
            m_videoPrimariesSignalled = true;
        }
        else
        {
            // Nothing signalled. Default by resolution rather than always BT.601, because this console
            // demonstrably encodes BT.709 — its H.264 stream says so explicitly at 1080p — while its HEVC stream
            // signals no matrix at all. Defaulting HD to 601 therefore mis-converted the HEVC path in exactly the
            // way that was just fixed for H.264: same encoder, same colours, no signalling. SD keeps BT.601,
            // which is the correct default there.
            m_yuvMatrix = m_displayHeight >= 720 ? 1 : 0;
            m_yuvMatrixSignalled = false;
        }

        // NV12 chroma is subsampled 2x vertically and horizontally, so the visible region has to land on even
        // boundaries or the planes disagree about which pixel is which.
        m_displayWidth &= ~1u;
        m_displayHeight &= ~1u;
        m_displayOffsetX &= ~1u;
        m_displayOffsetY &= ~1u;

        // The NV12 row stride. DXVA/GPU output is frequently padded (stride > width), so read it explicitly
        // rather than assuming tight packing; the upload path is already stride-aware. MF_MT_DEFAULT_STRIDE is
        // a signed value (negative = bottom-up, which NV12 output is not); fall back to width if absent.
        m_decodeStride = m_decodeWidth;
        UINT32 defaultStride = 0;
        if (SUCCEEDED(type->GetUINT32(MF_MT_DEFAULT_STRIDE, &defaultStride)) && defaultStride > 0)
        {
            m_decodeStride = defaultStride;
        }

        MFT_OUTPUT_STREAM_INFO info{};
        ThrowIfFailed(m_decoder->GetOutputStreamInfo(0, &info));
        m_decoderProvidesSamples =
            (info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        // NV12 needs w*h*3/2; give generous slack (2x) so an internally stride-padded decoded frame
        // never overflows the output buffer (a cause of the decoder's "CopyDecodedFrame failed").
        const uint32_t generous = m_decodeWidth * m_decodeHeight * 2;
        m_outputBufferSize = info.cbSize > generous ? info.cbSize : generous;
        m_outputAlignment = info.cbAlignment;
    }

    void VideoRenderer::SubmitH264(winrt::array_view<uint8_t const> annexB)
    {
        // Convenience for the single-shot test hooks: decode one unit and immediately present it.
        DecodeH264(annexB, 0);
        PresentLatestFrame();
    }

    /// Identify the codec of an Annex B access unit from its NAL headers.
    ///
    /// Ground truth beats configuration here. The console has already been observed to ignore a launchSpec field
    /// it does not care for (yuvCoefficient), so "we asked for HEVC" is not evidence that HEVC is arriving — and
    /// feeding H.264 to an HEVC decoder produces exactly the failure seen in the wild: every packet accepted,
    /// MF_E_TRANSFORM_NEED_MORE_INPUT forever, no frame, no error.
    ///
    /// The two codecs differ in their NAL header, which makes a parameter set unambiguous:
    ///   H.264 — 1 byte,  nal_unit_type = b0 & 0x1F        -> 7 = SPS, 8 = PPS
    ///   HEVC  — 2 bytes, nal_unit_type = (b0 >> 1) & 0x3F -> 32 = VPS, 33 = SPS, 34 = PPS
    /// Types 32-34 cannot occur in H.264 (its field is 5 bits, max 31), so finding one settles it. Frames alone
    /// are ambiguous, so an access unit with no parameter set yields no verdict rather than a guess.
    static bool DetectAnnexBCodec(
        const uint8_t* data, size_t length, VideoCodecKind& detected, uint8_t& firstNal0, uint8_t& firstNal1)
    {
        bool sawAny = false;

        for (size_t i = 0; i + 3 < length; ++i)
        {
            if (data[i] != 0x00 || data[i + 1] != 0x00)
            {
                continue;
            }

            size_t payload = 0;
            if (data[i + 2] == 0x01)
            {
                payload = i + 3;
            }
            else if (data[i + 2] == 0x00 && i + 4 < length && data[i + 3] == 0x01)
            {
                payload = i + 4;
            }
            else
            {
                continue;
            }

            if (payload + 1 >= length)
            {
                break;
            }

            const uint8_t b0 = data[payload];
            const uint8_t b1 = data[payload + 1];

            if (!sawAny)
            {
                firstNal0 = b0;
                firstNal1 = b1;
                sawAny = true;
            }

            const uint8_t hevcType = static_cast<uint8_t>((b0 >> 1) & 0x3F);
            if (hevcType == 32 || hevcType == 33 || hevcType == 34)
            {
                detected = VideoCodecKind::Hevc;
                return true;
            }

            const uint8_t h264Type = static_cast<uint8_t>(b0 & 0x1F);
            if ((b0 & 0x80) == 0 && (h264Type == 7 || h264Type == 8))
            {
                detected = VideoCodecKind::H264;
                return true;
            }

            i = payload;
        }

        return false;
    }

    void VideoRenderer::DecodeH264(winrt::array_view<uint8_t const> annexB, uint32_t byteCount)
    {
        // 0 means "the whole array"; otherwise honour the valid prefix, because the caller may hand us a
        // pooled buffer whose tail holds bytes from an unrelated, longer access unit.
        const uint32_t available = static_cast<uint32_t>(annexB.size());
        const uint32_t length = (byteCount == 0 || byteCount > available) ? available : byteCount;
        if (length == 0 || !m_swapChain || m_deviceLost)
        {
            return;
        }

        // Record the head of the first payload unconditionally. Previously this was only captured when a start
        // code was found, so "nothing recognised" and "detector never ran" both reported 00,00 — indistinguishable.
        if (!m_sawFirstPayload)
        {
            m_sawFirstPayload = true;
            m_payloadHead[0] = length > 0 ? annexB.data()[0] : 0;
            m_payloadHead[1] = length > 1 ? annexB.data()[1] : 0;
            m_payloadHead[2] = length > 2 ? annexB.data()[2] : 0;
            m_payloadHead[3] = length > 3 ? annexB.data()[3] : 0;
        }

        // Choose the decoder from what is actually arriving. Retried on every submit until a verdict is reached,
        // NOT only on the first: an access unit carries no parameter sets unless it is a keyframe, so the first
        // one to arrive is frequently unclassifiable. Getting one attempt meant a single inconclusive frame
        // locked in whatever codec was configured.
        if (!m_codecDetected)
        {
            ++m_detectAttempts;
            VideoCodecKind detected = m_codec;
            if (DetectAnnexBCodec(annexB.data(), length, detected, m_firstNal0, m_firstNal1))
            {
                m_codecDetected = true;
                if (detected != m_codec)
                {
                    // The bitstream disagrees with the request. Rebuild for what is actually arriving: an
                    // already-created decoder is bound to the wrong input type and will never emit a frame.
                    m_codecMismatch = true;
                    m_codec = detected;
                    m_decoder.Reset();
                    m_decoderName.clear();
                    m_decodeWidth = 0;
                    m_decodeHeight = 0;
                }
            }
        }

        EnsureDecoder();

        ComPtr<IMFMediaBuffer> buffer;
        ThrowIfFailed(MFCreateMemoryBuffer(static_cast<DWORD>(length), &buffer));
        BYTE* data = nullptr;
        DWORD maxLen = 0;
        ThrowIfFailed(buffer->Lock(&data, &maxLen, nullptr));
        memcpy(data, annexB.data(), length);
        ThrowIfFailed(buffer->Unlock());
        ThrowIfFailed(buffer->SetCurrentLength(static_cast<DWORD>(length)));

        ComPtr<IMFSample> inputSample;
        ThrowIfFailed(MFCreateSample(&inputSample));
        ThrowIfFailed(inputSample->AddBuffer(buffer.Get()));
        ThrowIfFailed(inputSample->SetSampleTime(m_sampleTime));
        ThrowIfFailed(inputSample->SetSampleDuration(166666)); // ~60 fps, 100ns units
        m_sampleTime += 166666;

        HRESULT hr = m_decoder->ProcessInput(0, inputSample.Get(), 0);
        if (hr == MF_E_NOTACCEPTING)
        {
            DrainDecoderToLatest();
            hr = m_decoder->ProcessInput(0, inputSample.Get(), 0);
        }
        ThrowIfFailed(hr);

        DrainDecoderToLatest();
    }

    void VideoRenderer::DrainDecoderToLatest()
    {
        for (;;)
        {
            // Allocate a fresh output sample unless the MFT provides its own. H.264 decoders need a
            // SIMD-aligned buffer (>=16 bytes), so honour the reported alignment and never go below.
            ComPtr<IMFSample> outSample;
            if (!m_decoderProvidesSamples && m_outputBufferSize > 0)
            {
                const DWORD alignMask = m_outputAlignment > 16 ? (m_outputAlignment - 1) : 0xFu;
                ComPtr<IMFMediaBuffer> buffer;
                ThrowIfFailed(MFCreateAlignedMemoryBuffer(m_outputBufferSize, alignMask, &buffer));
                ThrowIfFailed(MFCreateSample(&outSample));
                ThrowIfFailed(outSample->AddBuffer(buffer.Get()));
            }

            MFT_OUTPUT_DATA_BUFFER output{};
            output.pSample = outSample.Get(); // null when the MFT provides its own sample
            DWORD status = 0;
            HRESULT hr = m_decoder->ProcessOutput(0, 1, &output, &status);
            m_lastOutputHr = hr;
            if (SUCCEEDED(hr) && output.pSample != nullptr)
            {
                ++m_producedSamples;
            }

            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                break;
            }
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                ConfigureDecoderOutput();
                continue;
            }
            ThrowIfFailed(hr);

            IMFSample* produced = output.pSample;

            // A frame arrived but we still do not know its geometry, so the gate below would discard it and we
            // would present black at full frame rate. Recover, cheapest-and-best first.
            if (produced != nullptr && (m_decodeWidth == 0 || m_decodeHeight == 0))
            {
                // 1) Re-negotiate. This re-enumerates GetOutputAvailableType, which the decoder populates once
                //    it has parsed the stream, and is the only route that also yields the display aperture.
                //    Guarded: SetOutputType can legitimately refuse while a sample is outstanding, and that must
                //    not tear down the decode loop — it just means we fall through to the texture below.
                //    (Reading GetOutputCurrentType instead would be useless: it hands back the very type we set,
                //    which is the one missing the frame size.)
                m_geomStage = 1;
                try
                {
                    ConfigureDecoderOutput();
                }
                catch (...)
                {
                    m_geomStage = 2;
                }

                // 2) Failing that, ask the decoded surface how big it is. Authoritative for DXVA output, but
                //    coded-size only — a stream whose height is not a multiple of the coding unit will include
                //    padding rows until a type carrying an aperture turns up.
                if (m_decodeWidth == 0 || m_decodeHeight == 0)
                {
                    TryGeometryFromSample(produced);
                }
            }

            if (produced != nullptr && m_decodeWidth > 0 && m_decodeHeight > 0)
            {
                // Retain the newest decoded frame at its true row stride. In a decode burst only the last frame
                // survives here — earlier ones are overwritten — so PresentLatestFrame shows the newest while we
                // still decoded every frame, keeping the H.264 reference chain intact.

                // Preferred: keep the frame on the GPU (zero-copy) by copying the decoder's texture into a
                // shared texture. On any failure, permanently fall back to the CPU readback path below.
                bool stored = false;
                if (m_hardwareDecode && !m_zeroCopyDisabled)
                {
                    stored = TryStoreZeroCopyFrame(produced);
                    if (!stored)
                    {
                        m_zeroCopyDisabled = true;
                    }
                }

                ComPtr<IMFMediaBuffer> outBuffer;
                ComPtr<IMF2DBuffer> buffer2d;
                if (stored)
                {
                    // Frame already retained on the GPU — nothing further to read back.
                }
                else if (m_tenBitOutput)
                {
                    // 10-bit (P010) with no zero-copy path available. The readback present path samples R8/R8G8
                    // planes, so pushing P010 through it would draw noise rather than a picture. Refuse, and let
                    // the diagnostic say why — a blank screen with an explanation beats a corrupted one.
                    m_tenBitUnrenderable = true;
                }
                else if (SUCCEEDED(produced->GetBufferByIndex(0, &outBuffer)) && SUCCEEDED(outBuffer.As(&buffer2d)))
                {
                    // Preferred path, and REQUIRED for GPU/DXVA output: Lock2D reports the real row pitch and,
                    // for a DXGI-backed buffer, transparently copies the surface back to CPU memory. NV12 lays
                    // the UV plane immediately after the Y plane at the same pitch.
                    BYTE* scan0 = nullptr;
                    LONG pitch = 0;
                    if (SUCCEEDED(buffer2d->Lock2D(&scan0, &pitch)) && pitch != 0)
                    {
                        const uint32_t stride = static_cast<uint32_t>(pitch < 0 ? -pitch : pitch);
                        const size_t needed = static_cast<size_t>(stride) * m_decodeHeight * 3 / 2;
                        m_latestNv12.resize(needed);
                        memcpy(m_latestNv12.data(), scan0, needed);
                        m_latestStride = stride;
                        // Retain the VISIBLE dimensions; the coded height is kept separately so the upload can
                        // still find the UV plane, which follows the full coded Y plane.
                        m_latestCodedHeight = m_decodeHeight;
                        m_latestWidth = m_displayWidth;
                        m_latestHeight = m_displayHeight;
                        m_hasLatestFrame = true;
                        m_zeroCopyDecode = false; // this frame is CPU-resident → present via the upload path
                        buffer2d->Unlock2D();
                    }
                }
                else if (SUCCEEDED(produced->ConvertToContiguousBuffer(&outBuffer)))
                {
                    // Fallback for a plain (non-2D) system-memory buffer: assume the negotiated stride.
                    BYTE* nv12 = nullptr;
                    DWORD length = 0;
                    if (SUCCEEDED(outBuffer->Lock(&nv12, nullptr, &length)))
                    {
                        const size_t needed = static_cast<size_t>(m_decodeStride) * m_decodeHeight * 3 / 2;
                        if (length >= needed)
                        {
                            m_latestNv12.resize(needed);
                            memcpy(m_latestNv12.data(), nv12, needed);
                            m_latestStride = m_decodeStride;
                            m_latestCodedHeight = m_decodeHeight;
                            m_latestWidth = m_displayWidth;
                            m_latestHeight = m_displayHeight;
                            m_hasLatestFrame = true;
                            m_zeroCopyDecode = false;
                        }
                        outBuffer->Unlock();
                    }
                }
            }

            // If the MFT allocated its own sample (we passed null), release its reference.
            if (m_decoderProvidesSamples && produced != nullptr)
            {
                produced->Release();
            }
        }
    }

    int32_t VideoRenderer::DecodeMode()
    {
        if (!m_hardwareDecode)
        {
            return 0; // software
        }
        return m_zeroCopyDecode ? 2 : 1; // 2 = zero-copy, 1 = DXVA + CPU readback
    }

    // Dimensions of the most recently retained frame — i.e. what is actually on screen. Set by every decode
    // path (CPU readback and zero-copy alike), and 0 until a frame has arrived.
    uint32_t VideoRenderer::DecodedWidth()
    {
        return m_latestWidth;
    }

    uint32_t VideoRenderer::DecodedHeight()
    {
        return m_latestHeight;
    }

    hstring VideoRenderer::ColorMatrixDescription()
    {
        // Distinguish "the stream told us" from "we picked a default", and name the rule behind the default —
        // an unsignalled HEVC stream now reports BT.709 because HD content from this console is BT.709, which is
        // not something the earlier "matches vendor request" wording could express (the console ignores that
        // request outright).
        if (m_yuvMatrix == 1)
        {
            return m_yuvMatrixSignalled
                ? hstring{ L"BT.709 (signalled by stream)" }
                : hstring{ L"BT.709 (assumed: HD default, stream signalled none)" };
        }

        return m_yuvMatrixSignalled
            ? hstring{ L"BT.601 (signalled by stream)" }
            : hstring{ L"BT.601 (assumed: SD default, stream signalled none)" };
    }

    bool VideoRenderer::HasDecodedFrame()
    {
        return m_hasLatestFrame;
    }

    void VideoRenderer::PresentLatestFrame()
    {
        if (!m_hasLatestFrame)
        {
            return;
        }

        if (m_zeroCopyDecode)
        {
            PresentSharedLatest();
        }
        else
        {
            PresentNv12Latest();
        }

        // Announce the settled decode path once, AFTER presenting (so a present-time fallback is reflected).
        if (!m_decodePathLogged)
        {
            m_decodePathLogged = true;
            switch (DecodeMode())
            {
                case 2:  OutputDebugStringW(L"[Ripcord] video decode path: hardware DXVA, zero-copy (GPU-resident)\n"); break;
                case 1:  OutputDebugStringW(L"[Ripcord] video decode path: hardware DXVA, CPU readback\n"); break;
                default: OutputDebugStringW(L"[Ripcord] video decode path: software\n"); break;
            }
        }
    }

    void VideoRenderer::RecordZeroCopyFailure(int stage, HRESULT hr)
    {
        if (m_zeroCopyFailStage == 0) // keep the first failure
        {
            m_zeroCopyFailStage = stage;
            m_zeroCopyFailHr = hr;
        }
    }

    winrt::hstring VideoRenderer::ZeroCopyDiagnostic()
    {
        const wchar_t* stage = L"none";
        switch (m_zeroCopyFailStage)
        {
            case 1:  stage = L"D3D12 CreateFence(SHARED)"; break;
            case 2:  stage = L"CreateSharedHandle(fence)"; break;
            case 3:  stage = L"D3D11 OpenSharedFence"; break;
            case 4:  stage = L"D3D12 CreateCommittedResource(BGRA shared)"; break;
            case 5:  stage = L"D3D12 CreateSharedHandle(BGRA)"; break;
            case 6:  stage = L"D3D11 OpenSharedResource1(BGRA)"; break;
            case 7:  stage = L"IMFSample::GetBufferByIndex"; break;
            case 8:  stage = L"QI IMFDXGIBuffer (decoder output not D3D-backed)"; break;
            case 9:  stage = L"IMFDXGIBuffer::GetResource"; break;
            case 10: stage = L"QI ID3D11DeviceContext4"; break;
            case 11: stage = L"D3D11 Signal(shared fence)"; break;
            case 12: stage = L"PresentSharedLatest (D3D12 draw/present of shared texture)"; break;
            case 13: stage = L"QI ID3D11VideoDevice/VideoContext"; break;
            case 14: stage = L"CreateVideoProcessorEnumerator"; break;
            case 15: stage = L"CreateVideoProcessor"; break;
            case 16: stage = L"CreateVideoProcessorOutputView(BGRA)"; break;
            case 17: stage = L"CreateVideoProcessorInputView(NV12)"; break;
            case 18: stage = L"VideoProcessorBlt(NV12->BGRA)"; break;
            case 19: stage = L"D3D12 CreateFence(SHARED, read-complete)"; break;
            case 20: stage = L"CreateSharedHandle(read-complete fence)"; break;
            case 21: stage = L"D3D11 OpenSharedFence(read-complete)"; break;
            case 22: stage = L"D3D11 Wait(read-complete fence)"; break;
            case 99: stage = L"exception"; break;
            default: break;
        }

        wchar_t buf[160];
        swprintf_s(buf, L"%s (hr=0x%08X)", stage, static_cast<unsigned int>(m_zeroCopyFailHr));
        return winrt::hstring(buf);
    }

    bool VideoRenderer::TryCreateSharedFence()
    {
        ComPtr<ID3D11Device5> device5;
        HRESULT hr = m_decodeD3d11Device.As(&device5);
        if (FAILED(hr)) { RecordZeroCopyFailure(3, hr); return false; }

        // Create a D3D12 fence and open the same underlying fence on the D3D11 decode device.
        auto share = [&](ComPtr<ID3D12Fence>& fence12, ComPtr<ID3D11Fence>& fence11,
                         int createStage, int handleStage, int openStage) -> bool
        {
            HRESULT r = m_device->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12));
            if (FAILED(r)) { RecordZeroCopyFailure(createStage, r); return false; }

            HANDLE handle = nullptr;
            r = m_device->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle);
            if (FAILED(r)) { RecordZeroCopyFailure(handleStage, r); return false; }

            r = device5->OpenSharedFence(handle, IID_PPV_ARGS(&fence11));
            if (handle)
            {
                CloseHandle(handle);
            }
            if (FAILED(r)) { RecordZeroCopyFailure(openStage, r); return false; }
            return true;
        };

        // Forward: D3D11 signals when the NV12->BGRA conversion lands in the shared texture.
        if (!share(m_sharedFence, m_sharedFence11, 1, 2, 3))
        {
            return false;
        }

        // Reverse: D3D12 signals when the render path has finished sampling the shared texture, so the next
        // conversion can safely overwrite it. This replaces the per-frame GPU drain as the write/read barrier.
        return share(m_readFence, m_readFence11, 19, 20, 21);
    }

    bool VideoRenderer::EnsureSharedDecodeTexture(ID3D11Texture2D* decoded)
    {
        D3D11_TEXTURE2D_DESC desc{};
        decoded->GetDesc(&desc);

        // The shared target is the VISIBLE size, not the decoder texture's coded size. The VideoProcessor blit
        // below is given a source rect of the display aperture and a destination of this whole texture, so the
        // encoder's macroblock padding is cropped away on the GPU and never reaches the screen.
        const uint32_t targetWidth = m_displayWidth > 0 ? m_displayWidth : desc.Width;
        const uint32_t targetHeight = m_displayHeight > 0 ? m_displayHeight : desc.Height;

        if (m_sharedDecodeTex && targetWidth == m_sharedTexWidth && targetHeight == m_sharedTexHeight)
        {
            return true;
        }

        WaitForGpu();
        m_sharedDecodeTex.Reset();
        m_sharedDecodeTex11.Reset();
        m_vpOutputView.Reset();
        m_videoProcessor.Reset();
        m_vpEnumerator.Reset();

        // Create the shared BGRA target in D3D12 (ALLOW_RENDER_TARGET so the D3D11 view can be a VideoProcessor
        // output) and open it on the decode device. BGRA shares cleanly D3D12->D3D11 (unlike planar NV12).
        D3D12_HEAP_PROPERTIES heap{};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;

        D3D12_RESOURCE_DESC td{};
        td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = targetWidth;
        td.Height = targetHeight;
        td.DepthOrArraySize = 1;
        td.MipLevels = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        td.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;

        HRESULT hr = m_device->CreateCommittedResource(
            &heap, D3D12_HEAP_FLAG_SHARED, &td, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&m_sharedDecodeTex));
        if (FAILED(hr)) { RecordZeroCopyFailure(4, hr); return false; }

        HANDLE handle = nullptr;
        hr = m_device->CreateSharedHandle(m_sharedDecodeTex.Get(), nullptr, GENERIC_ALL, nullptr, &handle);
        if (FAILED(hr)) { RecordZeroCopyFailure(5, hr); return false; }

        ComPtr<ID3D11Device1> device1;
        hr = m_decodeD3d11Device.As(&device1);
        if (SUCCEEDED(hr))
        {
            hr = device1->OpenSharedResource1(handle, IID_PPV_ARGS(&m_sharedDecodeTex11));
        }
        if (handle)
        {
            CloseHandle(handle);
        }
        if (FAILED(hr)) { RecordZeroCopyFailure(6, hr); return false; }

        // Set up the VideoProcessor (NV12 in -> BGRA out, same size; scaling happens later in the upscale pass).
        hr = m_decodeD3d11Device.As(&m_videoDevice);
        if (SUCCEEDED(hr)) hr = m_decodeD3d11Context.As(&m_videoContext);
        if (FAILED(hr)) { RecordZeroCopyFailure(13, hr); return false; }

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd{};
        cd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        cd.InputFrameRate = { 60, 1 };
        // Input is the coded frame; output is the cropped visible frame.
        cd.InputWidth = desc.Width;
        cd.InputHeight = desc.Height;
        cd.OutputFrameRate = { 60, 1 };
        cd.OutputWidth = targetWidth;
        cd.OutputHeight = targetHeight;
        cd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        hr = m_videoDevice->CreateVideoProcessorEnumerator(&cd, &m_vpEnumerator);
        if (FAILED(hr)) { RecordZeroCopyFailure(14, hr); return false; }

        hr = m_videoDevice->CreateVideoProcessor(m_vpEnumerator.Get(), 0, &m_videoProcessor);
        if (FAILED(hr)) { RecordZeroCopyFailure(15, hr); return false; }

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
        ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        ovd.Texture2D.MipSlice = 0;
        hr = m_videoDevice->CreateVideoProcessorOutputView(
            m_sharedDecodeTex11.Get(), m_vpEnumerator.Get(), &ovd, &m_vpOutputView);
        if (FAILED(hr)) { RecordZeroCopyFailure(16, hr); return false; }

        // Colour space. For 10-bit output, prefer the DXGI colour-space API: it can express BT.2020 primaries
        // with the PQ (ST.2084) transfer function, which the legacy struct cannot — it only carries a BT.601/709
        // matrix flag. Declaring PQ in and plain SDR sRGB out asks the driver to tone-map for us, which is the
        // cheapest route to a correct-looking picture on an SDR display. Best-effort: if either the interface or
        // the call is unavailable we fall through to the legacy path, which will look flat but still render.
        bool colorSpaceSet = false;
        if (m_tenBitOutput)
        {
            ComPtr<ID3D11VideoContext1> videoContext1;
            if (SUCCEEDED(m_videoContext.As(&videoContext1)) && videoContext1)
            {
                // Pick the INPUT space from what the stream signalled, not from its bit depth. 10-bit is a bit
                // depth; PQ is a transfer function; they are independent. Declaring PQ over a 10-bit BT.709
                // stream would tone-map a picture that needs none, flattening it.
                //
                // Absent signalling we keep the previous behaviour (assume PQ for 10-bit), so this cannot
                // regress a working picture - same conservative rule the YUV-matrix read above uses.
                const bool wideGamut = !m_videoPrimariesSignalled || m_videoPrimaries == MFVideoPrimaries_BT2020;
                DXGI_COLOR_SPACE_TYPE inputSpace;
                if (m_transferFunctionSignalled && m_transferFunction == MFVideoTransFunc_HLG)
                {
                    inputSpace = DXGI_COLOR_SPACE_YCBCR_STUDIO_GHLG_TOPLEFT_P2020;
                    m_hdrTransfer = true;
                }
                else if (!m_transferFunctionSignalled || m_transferFunction == MFVideoTransFunc_2084)
                {
                    inputSpace = DXGI_COLOR_SPACE_YCBCR_STUDIO_G2084_LEFT_P2020;
                    m_hdrTransfer = true;
                }
                else
                {
                    inputSpace = wideGamut ? DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P2020
                                           : DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709;
                    m_hdrTransfer = false;
                }

                videoContext1->VideoProcessorSetStreamColorSpace1(m_videoProcessor.Get(), 0, inputSpace);
                videoContext1->VideoProcessorSetOutputColorSpace1(
                    m_videoProcessor.Get(), DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
                colorSpaceSet = true;

                // Only genuinely HDR sources are being tone-mapped; a 10-bit SDR source passes through.
                m_toneMappedByDriver = m_hdrTransfer;
            }
        }

        if (!colorSpaceSet)
        {
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE inCs{};
            inCs.YCbCr_Matrix = m_yuvMatrix;   // 0 = BT.601, 1 = BT.709 (from MF_MT_YUV_MATRIX)
            inCs.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
            m_videoContext->VideoProcessorSetStreamColorSpace(m_videoProcessor.Get(), 0, &inCs);
            m_toneMappedByDriver = false;
        }

        D3D11_VIDEO_PROCESSOR_COLOR_SPACE outCs{};
        outCs.RGB_Range = 0;     // 0 = full range (0-255)
        outCs.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255;
        m_videoContext->VideoProcessorSetOutputColorSpace(m_videoProcessor.Get(), &outCs);

        // Single BGRA SRV into the BGRA-pipeline's heap; the shared present reuses that pipeline's shader.
        m_device->CreateShaderResourceView(
            m_sharedDecodeTex.Get(), nullptr, m_srvHeap->GetCPUDescriptorHandleForHeapStart());

        // Crop on the GPU: take only the display aperture from the coded frame and write it to the whole
        // (visible-sized) destination. Without this the padding rows would be blitted through and then
        // aspect-fitted as if they were picture.
        const RECT sourceRect
        {
            static_cast<LONG>(m_displayOffsetX),
            static_cast<LONG>(m_displayOffsetY),
            static_cast<LONG>(m_displayOffsetX + targetWidth),
            static_cast<LONG>(m_displayOffsetY + targetHeight),
        };
        m_videoContext->VideoProcessorSetStreamSourceRect(m_videoProcessor.Get(), 0, TRUE, &sourceRect);

        const RECT destRect{ 0, 0, static_cast<LONG>(targetWidth), static_cast<LONG>(targetHeight) };
        m_videoContext->VideoProcessorSetStreamDestRect(m_videoProcessor.Get(), 0, TRUE, &destRect);

        m_sharedTexWidth = targetWidth;
        m_sharedTexHeight = targetHeight;
        return true;
    }

    bool VideoRenderer::TryStoreZeroCopyFrame(IMFSample* produced)
    {
        try
        {
            ComPtr<IMFMediaBuffer> buffer;
            HRESULT hr = produced->GetBufferByIndex(0, &buffer);
            if (FAILED(hr)) { RecordZeroCopyFailure(7, hr); return false; }

            ComPtr<IMFDXGIBuffer> dxgiBuffer;
            hr = buffer.As(&dxgiBuffer);
            if (FAILED(hr)) { RecordZeroCopyFailure(8, hr); return false; } // not D3D-backed → readback

            ComPtr<ID3D11Texture2D> decoded;
            hr = dxgiBuffer->GetResource(IID_PPV_ARGS(&decoded));
            if (FAILED(hr)) { RecordZeroCopyFailure(9, hr); return false; }

            UINT subresource = 0;
            dxgiBuffer->GetSubresourceIndex(&subresource);

            if (!EnsureSharedDecodeTexture(decoded.Get()))
            {
                return false; // stage already recorded inside
            }

            // Convert the decoder's NV12 output → the shared BGRA texture via the VideoProcessor, then signal
            // the shared fence so the render queue can wait on the conversion completing.
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
            ivd.FourCC = 0;
            ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            ivd.Texture2D.MipSlice = 0;
            ivd.Texture2D.ArraySlice = subresource;
            ComPtr<ID3D11VideoProcessorInputView> inputView;
            hr = m_videoDevice->CreateVideoProcessorInputView(decoded.Get(), m_vpEnumerator.Get(), &ivd, &inputView);
            if (FAILED(hr)) { RecordZeroCopyFailure(17, hr); return false; }

            ComPtr<ID3D11DeviceContext4> context4;
            hr = m_decodeD3d11Context.As(&context4);
            if (FAILED(hr)) { RecordZeroCopyFailure(10, hr); return false; }

            // The conversion below overwrites the shared texture, so it must not begin until the render path
            // has finished sampling the PREVIOUS frame out of it. Previously the full GPU drain after Present
            // guaranteed that implicitly; now the render path signals m_readFence and we wait on it here.
            // A GPU-side wait, so it costs no CPU stall. (m_readFenceValue is only touched from the decode
            // worker, which is also the thread that presents — see D3D12VideoDecodePipeline's WorkerLoop.)
            if (m_readFenceValue != 0)
            {
                hr = context4->Wait(m_readFence11.Get(), m_readFenceValue);
                if (FAILED(hr)) { RecordZeroCopyFailure(22, hr); return false; }
            }

            D3D11_VIDEO_PROCESSOR_STREAM stream{};
            stream.Enable = TRUE;
            stream.pInputSurface = inputView.Get();
            hr = m_videoContext->VideoProcessorBlt(m_videoProcessor.Get(), m_vpOutputView.Get(), 0, 1, &stream);
            if (FAILED(hr)) { RecordZeroCopyFailure(18, hr); return false; }

            UINT64 value = ++m_sharedFenceValue;
            hr = context4->Signal(m_sharedFence11.Get(), value);
            if (FAILED(hr)) { RecordZeroCopyFailure(11, hr); return false; }
            context4->Flush(); // submit conversion + signal so the render queue's Wait observes it

            m_latestFenceValue = value;
            m_latestWidth = m_sharedTexWidth;
            m_latestHeight = m_sharedTexHeight;
            m_hasLatestFrame = true;
            m_zeroCopyDecode = true;
            return true;
        }
        catch (winrt::hresult_error const& e)
        {
            RecordZeroCopyFailure(99, e.code());
            return false;
        }
        catch (...)
        {
            RecordZeroCopyFailure(99, E_FAIL);
            return false;
        }
    }

    void VideoRenderer::PresentSharedLatest()
    {
        if (!m_swapChain || m_deviceLost || !m_sharedDecodeTex || !m_hasLatestFrame)
        {
            return;
        }

        try
        {
            const UINT frameIndex = BeginFrame();

            auto transition = [this](ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
            {
                D3D12_RESOURCE_BARRIER barrier = {};
                barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
                barrier.Transition.pResource = resource;
                barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
                barrier.Transition.StateBefore = before;
                barrier.Transition.StateAfter = after;
                m_commandList->ResourceBarrier(1, &barrier);
            };

            // A cross-API shared resource sits in COMMON between uses; read it as a pixel-shader resource.
            transition(m_sharedDecodeTex.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

            transition(m_renderTargets[frameIndex].Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);

            D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
            rtvHandle.ptr += static_cast<SIZE_T>(frameIndex) * m_rtvDescriptorSize;
            m_commandList->OMSetRenderTargets(1, &rtvHandle, FALSE, nullptr);

            const float clearColor[] = { 0.0f, 0.0f, 0.0f, 1.0f };
            m_commandList->ClearRenderTargetView(rtvHandle, clearColor, 0, nullptr);

            // The shared texture is BGRA (VideoProcessor output) — draw it through the upscale pipeline, which
            // samples the decode-res source into the panel-res swap chain using the selected spatial filter.
            m_commandList->SetGraphicsRootSignature(m_upscaleRootSignature.Get());
            ID3D12DescriptorHeap* heaps[] = { m_srvHeap.Get() };
            m_commandList->SetDescriptorHeaps(1, heaps);
            m_commandList->SetGraphicsRootDescriptorTable(0, m_srvHeap->GetGPUDescriptorHandleForHeapStart());
            struct { int32_t mode; float srcW; float srcH; float pad; } upscaleParams{
                m_upscaleMode, static_cast<float>(m_sharedTexWidth), static_cast<float>(m_sharedTexHeight), 0.0f };
            m_commandList->SetGraphicsRoot32BitConstants(1, 4, &upscaleParams, 0);
            m_commandList->SetPipelineState(m_upscalePipeline.Get());

            // Fit the frame to the panel preserving aspect ratio (black bars fill the rest via the clear above).
            D3D12_VIEWPORT viewport;
            D3D12_RECT scissor;
            AspectFit(m_width, m_height, m_sharedTexWidth, m_sharedTexHeight, viewport, scissor);
            m_commandList->RSSetViewports(1, &viewport);
            m_commandList->RSSetScissorRects(1, &scissor);
            m_commandList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            m_commandList->DrawInstanced(3, 1, 0, 0);

            transition(m_sharedDecodeTex.Get(), D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
            transition(m_renderTargets[frameIndex].Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);

            // GPU-side wait for the D3D11 VideoProcessor conversion to land before the draw reads it. Queued
            // ahead of the command list, so it costs no CPU stall.
            ThrowIfFailed(m_commandQueue->Wait(m_sharedFence.Get(), m_latestFenceValue));

            EndFrame(frameIndex);

            // Reverse-direction sync: tell the D3D11 side that this frame's read of the shared texture is
            // retired, so the next VideoProcessorBlt may overwrite it. This is what makes it safe for
            // EndFrame to return without draining the GPU (the drain used to serve this purpose).
            const UINT64 readComplete = ++m_readFenceValue;
            ThrowIfFailed(m_commandQueue->Signal(m_readFence.Get(), readComplete));
        }
        catch (winrt::hresult_error const& e)
        {
            // Zero-copy present faulted — record why, then drop back to the CPU readback path for the session.
            RecordZeroCopyFailure(12, e.code());
            m_zeroCopyDisabled = true;
            m_zeroCopyDecode = false;
        }
        catch (...)
        {
            RecordZeroCopyFailure(12, E_FAIL);
            m_zeroCopyDisabled = true;
            m_zeroCopyDecode = false;
        }
    }

    void VideoRenderer::CreateNv12Pipeline()
    {
        // Descriptor table of two SRVs: t0 = Y (R8), t1 = UV (R8G8), plus one linear clamp sampler.
        D3D12_DESCRIPTOR_RANGE srvRange = {};
        srvRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRange.NumDescriptors = 2;
        srvRange.BaseShaderRegister = 0;
        srvRange.OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;

        D3D12_ROOT_PARAMETER rootParams[2] = {};
        rootParams[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        rootParams[0].DescriptorTable.NumDescriptorRanges = 1;
        rootParams[0].DescriptorTable.pDescriptorRanges = &srvRange;
        rootParams[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        // Root constants carry the colour matrix selector, so the readback path honours the stream's signalled
        // matrix instead of baking in BT.601.
        rootParams[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        rootParams[1].Constants.ShaderRegister = 0;
        rootParams[1].Constants.RegisterSpace = 0;
        rootParams[1].Constants.Num32BitValues = 4;
        rootParams[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC sampler = {};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.ShaderRegister = 0;
        sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        sampler.MaxLOD = D3D12_FLOAT32_MAX;

        D3D12_ROOT_SIGNATURE_DESC rsDesc = {};
        rsDesc.NumParameters = 2;
        rsDesc.pParameters = rootParams;
        rsDesc.NumStaticSamplers = 1;
        rsDesc.pStaticSamplers = &sampler;
        rsDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;

        ComPtr<ID3DBlob> signature;
        ComPtr<ID3DBlob> error;
        ThrowIfFailed(D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error));
        ThrowIfFailed(m_device->CreateRootSignature(
            0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&m_nv12RootSignature)));

        ComPtr<ID3DBlob> vs = CompileSource(kNv12ShaderSource, sizeof(kNv12ShaderSource) - 1, "VSMain", "vs_5_1");
        ComPtr<ID3DBlob> ps = CompileSource(kNv12ShaderSource, sizeof(kNv12ShaderSource) - 1, "PSMain", "ps_5_1");

        D3D12_RASTERIZER_DESC rast = {};
        rast.FillMode = D3D12_FILL_MODE_SOLID;
        rast.CullMode = D3D12_CULL_MODE_NONE;
        rast.DepthClipEnable = TRUE;

        D3D12_BLEND_DESC blend = {};
        blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

        D3D12_GRAPHICS_PIPELINE_STATE_DESC psoDesc = {};
        psoDesc.pRootSignature = m_nv12RootSignature.Get();
        psoDesc.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
        psoDesc.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
        psoDesc.RasterizerState = rast;
        psoDesc.BlendState = blend;
        psoDesc.DepthStencilState.DepthEnable = FALSE;
        psoDesc.DepthStencilState.StencilEnable = FALSE;
        psoDesc.SampleMask = UINT_MAX;
        psoDesc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        psoDesc.NumRenderTargets = 1;
        psoDesc.RTVFormats[0] = DXGI_FORMAT_B8G8R8A8_UNORM;
        psoDesc.SampleDesc.Count = 1;
        ThrowIfFailed(m_device->CreateGraphicsPipelineState(&psoDesc, IID_PPV_ARGS(&m_nv12Pipeline)));

        D3D12_DESCRIPTOR_HEAP_DESC srvHeapDesc = {};
        srvHeapDesc.NumDescriptors = 2;
        srvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        srvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ThrowIfFailed(m_device->CreateDescriptorHeap(&srvHeapDesc, IID_PPV_ARGS(&m_nv12SrvHeap)));
        m_srvDescriptorSize = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    }

    void VideoRenderer::SetUpscaleMode(int32_t mode)
    {
        m_upscaleMode = (mode == 1) ? 1 : 0; // only bilinear (0) / bicubic (1) for now
    }

    void VideoRenderer::SetCompositionScale(float scaleX, float scaleY)
    {
        if (!m_swapChain)
        {
            return;
        }

        // The panel composites the swap chain at the DPI scale; with a physical-pixel back buffer we apply the
        // inverse so the net mapping is 1:1 (crisp, no double-scaling). Best-effort — ignore on failure.
        DXGI_MATRIX_3X2_F matrix = {};
        matrix._11 = scaleX > 0.0f ? 1.0f / scaleX : 1.0f;
        matrix._22 = scaleY > 0.0f ? 1.0f / scaleY : 1.0f;
        m_swapChain->SetMatrixTransform(&matrix);
    }

    void VideoRenderer::CreateUpscalePipeline()
    {
        // Root param 0: one SRV (the source colour texture, t0). Root param 1: 4 32-bit constants (mode +
        // source size). Two static samplers: s0 linear (bilinear mode), s1 point (bicubic taps).
        D3D12_DESCRIPTOR_RANGE srvRange = {};
        srvRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRange.NumDescriptors = 1;
        srvRange.BaseShaderRegister = 0;
        srvRange.OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;

        D3D12_ROOT_PARAMETER params[2] = {};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[0].DescriptorTable.NumDescriptorRanges = 1;
        params[0].DescriptorTable.pDescriptorRanges = &srvRange;
        params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        params[1].Constants.ShaderRegister = 0;
        params[1].Constants.Num32BitValues = 4;
        params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC samplers[2] = {};
        samplers[0].Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        samplers[0].AddressU = samplers[0].AddressV = samplers[0].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samplers[0].ShaderRegister = 0;
        samplers[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        samplers[0].MaxLOD = D3D12_FLOAT32_MAX;
        samplers[1].Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
        samplers[1].AddressU = samplers[1].AddressV = samplers[1].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samplers[1].ShaderRegister = 1;
        samplers[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        samplers[1].MaxLOD = D3D12_FLOAT32_MAX;

        D3D12_ROOT_SIGNATURE_DESC rsDesc = {};
        rsDesc.NumParameters = 2;
        rsDesc.pParameters = params;
        rsDesc.NumStaticSamplers = 2;
        rsDesc.pStaticSamplers = samplers;
        rsDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;

        ComPtr<ID3DBlob> signature;
        ComPtr<ID3DBlob> error;
        ThrowIfFailed(D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error));
        ThrowIfFailed(m_device->CreateRootSignature(
            0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&m_upscaleRootSignature)));

        ComPtr<ID3DBlob> vs = CompileSource(kUpscaleShaderSource, sizeof(kUpscaleShaderSource) - 1, "VSMain", "vs_5_1");
        ComPtr<ID3DBlob> ps = CompileSource(kUpscaleShaderSource, sizeof(kUpscaleShaderSource) - 1, "PSMain", "ps_5_1");

        D3D12_RASTERIZER_DESC rast = {};
        rast.FillMode = D3D12_FILL_MODE_SOLID;
        rast.CullMode = D3D12_CULL_MODE_NONE;
        rast.DepthClipEnable = TRUE;

        D3D12_BLEND_DESC blend = {};
        blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

        D3D12_GRAPHICS_PIPELINE_STATE_DESC psoDesc = {};
        psoDesc.pRootSignature = m_upscaleRootSignature.Get();
        psoDesc.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
        psoDesc.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
        psoDesc.RasterizerState = rast;
        psoDesc.BlendState = blend;
        psoDesc.DepthStencilState.DepthEnable = FALSE;
        psoDesc.DepthStencilState.StencilEnable = FALSE;
        psoDesc.SampleMask = UINT_MAX;
        psoDesc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        psoDesc.NumRenderTargets = 1;
        psoDesc.RTVFormats[0] = DXGI_FORMAT_B8G8R8A8_UNORM;
        psoDesc.SampleDesc.Count = 1;
        ThrowIfFailed(m_device->CreateGraphicsPipelineState(&psoDesc, IID_PPV_ARGS(&m_upscalePipeline)));
    }

    void VideoRenderer::EnsureNv12Textures(uint32_t width, uint32_t height)
    {
        if (m_texY && width == m_nv12TexWidth && height == m_nv12TexHeight)
        {
            return;
        }

        WaitForGpu();
        m_texY.Reset();
        m_texUV.Reset();
        for (uint32_t i = 0; i < FrameCount; i++)
        {
            m_uploadY[i].Reset();
            m_uploadUV[i].Reset();
        }
        m_nv12TexWidth = width;
        m_nv12TexHeight = height;

        D3D12_HEAP_PROPERTIES defaultHeap = {};
        defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_HEAP_PROPERTIES uploadHeap = {};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;

        // `uploads` is the per-in-flight-frame ring for this plane (FrameCount buffers, identical layout).
        auto makePlane = [&](uint32_t w, uint32_t h, DXGI_FORMAT fmt,
                             ComPtr<ID3D12Resource>& tex, ComPtr<ID3D12Resource>* uploads,
                             uint32_t& rowPitch, D3D12_CPU_DESCRIPTOR_HANDLE srv)
        {
            D3D12_RESOURCE_DESC texDesc = {};
            texDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            texDesc.Width = w;
            texDesc.Height = h;
            texDesc.DepthOrArraySize = 1;
            texDesc.MipLevels = 1;
            texDesc.Format = fmt;
            texDesc.SampleDesc.Count = 1;
            texDesc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;

            ThrowIfFailed(m_device->CreateCommittedResource(
                &defaultHeap, D3D12_HEAP_FLAG_NONE, &texDesc,
                D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&tex)));

            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint = {};
            UINT numRows = 0;
            UINT64 rowSize = 0;
            UINT64 totalBytes = 0;
            m_device->GetCopyableFootprints(&texDesc, 0, 1, 0, &footprint, &numRows, &rowSize, &totalBytes);
            rowPitch = footprint.Footprint.RowPitch;

            D3D12_RESOURCE_DESC bufDesc = {};
            bufDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            bufDesc.Width = totalBytes;
            bufDesc.Height = 1;
            bufDesc.DepthOrArraySize = 1;
            bufDesc.MipLevels = 1;
            bufDesc.Format = DXGI_FORMAT_UNKNOWN;
            bufDesc.SampleDesc.Count = 1;
            bufDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            for (uint32_t i = 0; i < FrameCount; i++)
            {
                ThrowIfFailed(m_device->CreateCommittedResource(
                    &uploadHeap, D3D12_HEAP_FLAG_NONE, &bufDesc,
                    D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&uploads[i])));
            }

            D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
            srvDesc.Format = fmt;
            srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            srvDesc.Texture2D.MipLevels = 1;
            m_device->CreateShaderResourceView(tex.Get(), &srvDesc, srv);
        };

        D3D12_CPU_DESCRIPTOR_HANDLE srv0 = m_nv12SrvHeap->GetCPUDescriptorHandleForHeapStart();
        D3D12_CPU_DESCRIPTOR_HANDLE srv1 = srv0;
        srv1.ptr += m_srvDescriptorSize;

        // NV12: full-res luma plane, half-res interleaved chroma plane.
        makePlane(width, height, DXGI_FORMAT_R8_UNORM, m_texY, m_uploadY, m_uploadYRowPitch, srv0);
        makePlane(width / 2, height / 2, DXGI_FORMAT_R8G8_UNORM, m_texUV, m_uploadUV, m_uploadUVRowPitch, srv1);

        m_nv12TexState = D3D12_RESOURCE_STATE_COPY_DEST;
    }

    void VideoRenderer::UploadNv12Textures(
        uint32_t frameIndex,
        const uint8_t* nv12,
        uint32_t stride,
        uint32_t codedHeight,
        uint32_t width,
        uint32_t height)
    {
        const uint8_t* yPlane = nv12;

        // The UV plane begins after the FULL coded Y plane. Using the visible height here would land partway
        // into luma and produce the classic green/magenta chroma smear.
        const uint8_t* uvPlane = nv12 + static_cast<size_t>(stride) * codedHeight;

        uint8_t* mapped = nullptr;
        D3D12_RANGE readRange = { 0, 0 };

        // Y: `width` bytes/row, `height` rows, starting at the visible origin.
        ThrowIfFailed(m_uploadY[frameIndex]->Map(0, &readRange, reinterpret_cast<void**>(&mapped)));
        for (uint32_t row = 0; row < height; row++)
        {
            const size_t srcRow = static_cast<size_t>(row + m_displayOffsetY) * stride + m_displayOffsetX;
            memcpy(mapped + static_cast<size_t>(row) * m_uploadYRowPitch, yPlane + srcRow, width);
        }
        m_uploadY[frameIndex]->Unmap(0, nullptr);

        // UV: `width` bytes/row (= width/2 interleaved R8G8 texels), `height/2` rows; the UV plane shares the
        // Y plane's row stride in NV12, and its vertical offset is halved by the 2x chroma subsampling.
        ThrowIfFailed(m_uploadUV[frameIndex]->Map(0, &readRange, reinterpret_cast<void**>(&mapped)));
        for (uint32_t row = 0; row < height / 2; row++)
        {
            const size_t srcRow = static_cast<size_t>(row + (m_displayOffsetY / 2)) * stride + m_displayOffsetX;
            memcpy(mapped + static_cast<size_t>(row) * m_uploadUVRowPitch, uvPlane + srcRow, width);
        }
        m_uploadUV[frameIndex]->Unmap(0, nullptr);
    }

    void VideoRenderer::PresentNv12Latest()
    {
        if (!m_swapChain || m_deviceLost || !m_hasLatestFrame)
        {
            return;
        }

        const uint32_t width = m_latestWidth;
        const uint32_t height = m_latestHeight;

        EnsureNv12Textures(width, height);

        // BeginFrame first: it waits for this slot's previous GPU work to retire, which is precisely what makes
        // this slot's upload buffers safe for the CPU to rewrite below.
        const UINT frameIndex = BeginFrame();
        UploadNv12Textures(
            frameIndex, m_latestNv12.data(), m_latestStride, m_latestCodedHeight, width, height);

        auto transition = [this](ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
        {
            D3D12_RESOURCE_BARRIER barrier = {};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = resource;
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrier.Transition.StateBefore = before;
            barrier.Transition.StateAfter = after;
            m_commandList->ResourceBarrier(1, &barrier);
        };

        if (m_nv12TexState != D3D12_RESOURCE_STATE_COPY_DEST)
        {
            transition(m_texY.Get(), m_nv12TexState, D3D12_RESOURCE_STATE_COPY_DEST);
            transition(m_texUV.Get(), m_nv12TexState, D3D12_RESOURCE_STATE_COPY_DEST);
        }

        auto copyPlane = [this](ID3D12Resource* tex, ID3D12Resource* upload)
        {
            D3D12_RESOURCE_DESC texDesc = tex->GetDesc();
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint = {};
            m_device->GetCopyableFootprints(&texDesc, 0, 1, 0, &footprint, nullptr, nullptr, nullptr);

            D3D12_TEXTURE_COPY_LOCATION dst = {};
            dst.pResource = tex;
            dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            dst.SubresourceIndex = 0;

            D3D12_TEXTURE_COPY_LOCATION src = {};
            src.pResource = upload;
            src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            src.PlacedFootprint = footprint;

            m_commandList->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        };

        copyPlane(m_texY.Get(), m_uploadY[frameIndex].Get());
        copyPlane(m_texUV.Get(), m_uploadUV[frameIndex].Get());

        transition(m_texY.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        transition(m_texUV.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        m_nv12TexState = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;

        transition(m_renderTargets[frameIndex].Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);

        D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtvHandle.ptr += static_cast<SIZE_T>(frameIndex) * m_rtvDescriptorSize;
        m_commandList->OMSetRenderTargets(1, &rtvHandle, FALSE, nullptr);

        const float clearColor[] = { 0.0f, 0.0f, 0.0f, 1.0f };
        m_commandList->ClearRenderTargetView(rtvHandle, clearColor, 0, nullptr);

        m_commandList->SetGraphicsRootSignature(m_nv12RootSignature.Get());
        ID3D12DescriptorHeap* heaps[] = { m_nv12SrvHeap.Get() };
        m_commandList->SetDescriptorHeaps(1, heaps);
        m_commandList->SetGraphicsRootDescriptorTable(0, m_nv12SrvHeap->GetGPUDescriptorHandleForHeapStart());
        const uint32_t matrixParams[4] = { static_cast<uint32_t>(m_yuvMatrix), 0, 0, 0 };
        m_commandList->SetGraphicsRoot32BitConstants(1, 4, matrixParams, 0);
        m_commandList->SetPipelineState(m_nv12Pipeline.Get());

        // Aspect-ratio-preserving fit to the panel (readback fallback path).
        D3D12_VIEWPORT viewport;
        D3D12_RECT scissor;
        // The retained (visible) size, not the coded size — fitting 1088 rows would squash the picture slightly
        // and letterbox it wrongly.
        AspectFit(m_width, m_height, m_latestWidth, m_latestHeight, viewport, scissor);
        m_commandList->RSSetViewports(1, &viewport);
        m_commandList->RSSetScissorRects(1, &scissor);
        m_commandList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        m_commandList->DrawInstanced(3, 1, 0, 0);

        transition(m_renderTargets[frameIndex].Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);

        EndFrame(frameIndex);
    }

    uint64_t VideoRenderer::SwapChainPointer()
    {
        return reinterpret_cast<uint64_t>(m_swapChain.Get());
    }

    void VideoRenderer::RenderClear(float red, float green, float blue)
    {
        if (!m_swapChain || m_deviceLost)
        {
            return;
        }

        const UINT frameIndex = BeginFrame();

        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = m_renderTargets[frameIndex].Get();
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        m_commandList->ResourceBarrier(1, &barrier);

        D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtvHandle.ptr += static_cast<SIZE_T>(frameIndex) * m_rtvDescriptorSize;

        const float clearColor[] = { red, green, blue, 1.0f };
        m_commandList->ClearRenderTargetView(rtvHandle, clearColor, 0, nullptr);

        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        m_commandList->ResourceBarrier(1, &barrier);

        EndFrame(frameIndex);
    }

    void VideoRenderer::WaitForGpu()
    {
        if (!m_commandQueue || !m_fence)
        {
            return;
        }

        const uint64_t fenceToWaitFor = ++m_fenceValue;
        ThrowIfFailed(m_commandQueue->Signal(m_fence.Get(), fenceToWaitFor));

        if (m_fence->GetCompletedValue() < fenceToWaitFor)
        {
            ThrowIfFailed(m_fence->SetEventOnCompletion(fenceToWaitFor, m_fenceEvent));
            WaitForSingleObject(m_fenceEvent, INFINITE);
        }

        // The pipeline is fully drained, so no slot has outstanding work. Clearing these matters: a stale
        // value would make BeginFrame wait on a fence point that is already in the past (harmless) or, after
        // resource recreation, on work that no longer relates to the slot.
        for (uint32_t i = 0; i < FrameCount; i++)
        {
            m_frameFenceValues[i] = 0;
        }
    }

    uint32_t VideoRenderer::BeginFrame()
    {
        // 0. A lost device can never render again. Surface it and let the caller decide; the render paths
        //    check IsDeviceLost before calling in, so this is a belt-and-braces guard.
        if (m_deviceLost)
        {
            throw winrt::hresult_error(DXGI_ERROR_DEVICE_REMOVED);
        }

        // 1. Pace against the presentation engine rather than the GPU. Bounded wait so a lost/abandoned
        //    signal degrades to a dropped frame instead of wedging the decode worker forever.
        if (m_frameLatencyWaitable)
        {
            WaitForSingleObjectEx(m_frameLatencyWaitable, 1000, FALSE);
        }

        const uint32_t frameIndex = m_swapChain->GetCurrentBackBufferIndex();

        // 2. Wait only for the work previously submitted for THIS slot. The other FrameCount-1 frames remain
        //    in flight, which is exactly the overlap the old per-present WaitForGpu() destroyed.
        const uint64_t slotFence = m_frameFenceValues[frameIndex];
        if (slotFence != 0 && m_fence->GetCompletedValue() < slotFence)
        {
            ThrowIfFailed(m_fence->SetEventOnCompletion(slotFence, m_fenceEvent));
            WaitForSingleObject(m_fenceEvent, INFINITE);
        }

        // 3. Safe now: the GPU is done with everything recorded from this slot's allocator, and this slot's
        //    upload buffers are free for the CPU to rewrite.
        ThrowIfFailed(m_commandAllocators[frameIndex]->Reset());
        ThrowIfFailed(m_commandList->Reset(m_commandAllocators[frameIndex].Get(), nullptr));
        return frameIndex;
    }

    void VideoRenderer::EndFrame(uint32_t frameIndex)
    {
        ThrowIfFailed(m_commandList->Close());
        ID3D12CommandList* lists[] = { m_commandList.Get() };
        m_commandQueue->ExecuteCommandLists(1, lists);

        // Sync interval 0: a flip-model composition swap chain cannot tear (the compositor owns vsync), so
        // this only avoids stalling us a refresh interval per frame.
        //
        // Present is where device loss actually surfaces (driver update, TDR, eGPU unplug). Record it and
        // return quietly instead of throwing: the managed side polls IsDeviceLost and rebuilds, whereas
        // throwing here just produced "frame skipped" in a loop behind a frozen picture.
        HRESULT presentHr = m_swapChain->Present(0, 0);
        if (NoteDeviceLoss(presentHr))
        {
            return;
        }
        ThrowIfFailed(presentHr);

        // Remember where this slot's work ends instead of blocking on it. BeginFrame waits on this value the
        // next time the same slot comes around, by which point it has usually long since retired.
        const uint64_t signalValue = ++m_fenceValue;
        ThrowIfFailed(m_commandQueue->Signal(m_fence.Get(), signalValue));
        m_frameFenceValues[frameIndex] = signalValue;
    }

    void VideoRenderer::Resize(uint32_t width, uint32_t height)
    {
        const uint32_t newWidth = width == 0 ? 1 : width;
        const uint32_t newHeight = height == 0 ? 1 : height;
        if (!m_swapChain || m_deviceLost || (newWidth == m_width && newHeight == m_height))
        {
            return;
        }

        WaitForGpu();
        for (uint32_t i = 0; i < FrameCount; i++)
        {
            m_renderTargets[i].Reset();
        }

        m_width = newWidth;
        m_height = newHeight;
        // Preserve the creation flags: dropping FRAME_LATENCY_WAITABLE_OBJECT here would leave
        // m_frameLatencyWaitable dangling and silently break the pacing in BeginFrame.
        ThrowIfFailed(m_swapChain->ResizeBuffers(
            FrameCount, m_width, m_height, DXGI_FORMAT_B8G8R8A8_UNORM, m_swapChainFlags));
        CreateRenderTargets();
    }

    void VideoRenderer::Shutdown()
    {
        WaitForGpu();
        if (m_fenceEvent != nullptr)
        {
            CloseHandle(m_fenceEvent);
            m_fenceEvent = nullptr;
        }

        if (m_frameLatencyWaitable != nullptr)
        {
            CloseHandle(m_frameLatencyWaitable);
            m_frameLatencyWaitable = nullptr;
        }

        m_decoder.Reset();
        m_dxgiDeviceManager.Reset(); // release MF objects before MFShutdown
        if (m_mfStarted)
        {
            MFShutdown();
            m_mfStarted = false;
        }
        // Release shared + VideoProcessor resources (and the D3D11 views) before the decode device.
        m_vpOutputView.Reset();
        m_videoProcessor.Reset();
        m_vpEnumerator.Reset();
        m_videoContext.Reset();
        m_videoDevice.Reset();
        m_sharedFence11.Reset();
        m_readFence11.Reset();
        m_sharedDecodeTex11.Reset();
        m_sharedDecodeTex.Reset();
        m_sharedFence.Reset();
        m_readFence.Reset();
        m_decodeD3d11Context.Reset();
        m_decodeD3d11Device.Reset();
        // Remaining resources release via ComPtr destructors.
    }
}
