#pragma once
#include "Ripcord.Media.Interop.VideoRenderer.g.h"

#include <d3d12.h>
#include <d3d12sdklayers.h> // ID3D12Debug (not pulled in by d3d12.h)
#include <dxgi1_6.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wrl/client.h>
#include <vector>

namespace winrt::Ripcord::Media::Interop::implementation
{
    struct VideoRenderer : VideoRendererT<VideoRenderer>
    {
        VideoRenderer() = default;
        ~VideoRenderer();

        void Initialize(
            uint32_t width,
            uint32_t height,
            Ripcord::Media::Interop::GpuSelection gpuSelection,
            uint64_t specificLuid);
        winrt::hstring ActiveAdapterDescription();
        bool IsDeviceLost();
        int32_t DeviceRemovedReason();
        uint64_t SwapChainPointer();
        void RenderClear(float red, float green, float blue);
        void PresentBgra(winrt::array_view<uint8_t const> bgra, uint32_t width, uint32_t height);
        void SubmitH264(winrt::array_view<uint8_t const> annexB);
        void DecodeH264(winrt::array_view<uint8_t const> annexB, uint32_t byteCount);
        void PresentLatestFrame();
        void Resize(uint32_t width, uint32_t height);
        void Shutdown();

        // Spatial upscale filter for the final present: 0 = bilinear, 1 = bicubic (sharper).
        void SetUpscaleMode(int32_t mode);

        // Counter the SwapChainPanel's DPI composition scale so a physical-pixel back buffer maps 1:1.
        void SetCompositionScale(float scaleX, float scaleY);

        // Which decode path is currently active: 0 = software, 1 = hardware (DXVA) with CPU readback,
        // 2 = hardware (DXVA) zero-copy (GPU-resident). Reflects the settled state after a few frames.
        int32_t DecodeMode();

        // Resolution the decoder is actually producing (0 before the first decoded frame).
        uint32_t DecodedWidth();
        uint32_t DecodedHeight();
        hstring ColorMatrixDescription();
        void SetCodec(VideoCodecKind codec);
        hstring DecoderDescription();
        hstring DecoderDiagnostic();

        // If zero-copy fell back, a human-readable "<stage> (hr=0x........)" describing the first failing call;
        // empty/"none" if zero-copy never failed.
        winrt::hstring ZeroCopyDiagnostic();

        // True once at least one frame has actually been decoded and retained. Used to defer decode-path
        // reporting past the decoder's startup latency (the first submits produce no frame yet).
        bool HasDecodedFrame();

    private:
        // Three buffers so the presentation engine always has one to show, one queued, and one for us to
        // draw into. With two, the CPU stalls on the compositor for a large part of every frame.
        static constexpr uint32_t FrameCount = 3;

        void CreateRenderTargets();
        void CreatePipeline();

        // Whether the display we are presenting to can accept HDR10. Probed once at device creation; a user
        // toggling "Use HDR" or dragging the window to another monitor mid-session will not be noticed yet.
        void ProbeDisplayHdr(IDXGIFactory1* factory, IDXGIAdapter1* renderAdapter);
        void EnsureFrameTexture(uint32_t width, uint32_t height);
        void UploadFrameTexture(const uint8_t* bgra, uint32_t width, uint32_t height);
        void PresentBgraInternal(const uint8_t* bgra, uint32_t width, uint32_t height);

        // Full pipeline drain. Correct for resize/teardown/resource recreation ONLY — never per frame.
        void WaitForGpu();

        // Per-frame bracket replacing the old "Present then WaitForGpu" pattern. BeginFrame throttles on the
        // swap chain's frame-latency waitable object, waits for only THIS slot's prior GPU work to retire (the
        // other frames stay in flight), and resets that slot's allocator. EndFrame submits, presents, and
        // records the slot's completion fence value. Together they let CPU and GPU overlap instead of running
        // strictly serially — the single biggest latency win in the render path.
        uint32_t BeginFrame();
        void EndFrame(uint32_t frameIndex);

        // Layer 3 decode (Media Foundation decoder MFT, DXVA-accelerated when possible).
        void EnsureDecoder();
        bool TryEnableHardwareDecode();         // stand up a D3D11 device manager so the MFT decodes on the GPU
        bool CreateDecoderForCodec(VideoCodecKind codec);  // pick a SYNC decoder MFT for the codec (see the .cpp)
        bool ConfigureDecoderOutput();          // (re)negotiate NV12 output + size/stride after a stream change
        void AdoptOutputGeometry(IMFMediaType* type);  // size/aperture/matrix/stride from an output media type
        bool TryGeometryFromSample(IMFSample* produced); // measure the decoded surface when the type says nothing
        void DrainDecoderToLatest();            // pull out decoded frames, retain the newest NV12 (no present)

        // GPU NV12->RGB present path (the live video path): two planar textures (Y = R8, UV = R8G8)
        // sampled and colour-converted in the pixel shader, so no CPU conversion per frame.
        void CreateNv12Pipeline();
        void CreateUpscalePipeline();           // final-present pipeline with a selectable spatial filter
        void EnsureNv12Textures(uint32_t width, uint32_t height);
        // Writes into the upload buffers belonging to `frameIndex`. The buffers are ringed per in-flight frame:
        // with frames overlapping, a single shared upload buffer could be rewritten by the CPU while a previous
        // frame's copy was still reading it.
        //
        // `codedHeight` locates the UV plane (which follows the full coded Y plane); `width`/`height` are the
        // visible extent to copy, offset by m_displayOffsetX/Y. Cropping here means every texture and every
        // aspect-fit downstream deals only in real picture pixels.
        void UploadNv12Textures(
            uint32_t frameIndex,
            const uint8_t* nv12,
            uint32_t stride,
            uint32_t codedHeight,
            uint32_t width,
            uint32_t height);
        void PresentNv12Latest();               // draw the retained (CPU-uploaded) NV12 frame via the GPU shader

        // Zero-copy path: keep the DXVA-decoded NV12 on the GPU. The D3D11 decode device copies the decoder's
        // output texture into a texture SHARED with the D3D12 render device, synchronised by a shared fence, so
        // the render path samples it directly with no CPU readback+upload. Best-effort: any failure sticks the
        // pipeline on the CPU readback path for the rest of the session.
        bool TryCreateSharedFence();                        // one-time cross-API fence setup
        bool TryStoreZeroCopyFrame(IMFSample* produced);    // extract decoder texture, copy to shared, signal
        bool EnsureSharedDecodeTexture(ID3D11Texture2D* decoded); // (re)create the shared NV12 texture + SRVs
        void PresentSharedLatest();                         // draw the shared NV12 texture (waits on the fence)
        void RecordZeroCopyFailure(int stage, HRESULT hr);  // capture the first zero-copy failure for diagnostics

        Microsoft::WRL::ComPtr<ID3D12Device> m_device;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> m_commandQueue;
        Microsoft::WRL::ComPtr<IDXGISwapChain3> m_swapChain;
        Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> m_rtvHeap;
        Microsoft::WRL::ComPtr<ID3D12Resource> m_renderTargets[FrameCount];
        // One allocator per in-flight frame: a single shared allocator cannot be reset while the GPU may still
        // be executing commands recorded from it, which is what forced the per-frame full drain.
        Microsoft::WRL::ComPtr<ID3D12CommandAllocator> m_commandAllocators[FrameCount];
        Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> m_commandList;
        Microsoft::WRL::ComPtr<ID3D12Fence> m_fence;

        // Fence value marking the end of each slot's submitted work; 0 = slot has nothing outstanding.
        uint64_t m_frameFenceValues[FrameCount] = {};

        // Signalled by DXGI when the presentation engine can accept another frame. This is what we block on
        // (at most one frame ahead) instead of draining the GPU.
        HANDLE m_frameLatencyWaitable = nullptr;

        // Creation flags, remembered so ResizeBuffers preserves them (dropping the waitable-object flag on
        // resize silently invalidates the waitable handle).
        UINT m_swapChainFlags = 0;

        /// Note a possible device-lost HRESULT. Returns true if the device is now considered lost, in which
        /// case the render paths become no-ops rather than throwing on every frame.
        bool NoteDeviceLoss(HRESULT hr);

        winrt::hstring m_adapterDescription;
        bool m_deviceLost = false;
        HRESULT m_deviceRemovedReason = S_OK;

        // Layer 2 graphics pipeline: fullscreen-triangle draw sampling a BGRA frame texture.
        Microsoft::WRL::ComPtr<ID3D12RootSignature> m_rootSignature;
        Microsoft::WRL::ComPtr<ID3D12PipelineState> m_pipelineState;
        Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> m_srvHeap;
        Microsoft::WRL::ComPtr<ID3D12Resource> m_frameTexture;
        Microsoft::WRL::ComPtr<ID3D12Resource> m_frameUpload;
        D3D12_RESOURCE_STATES m_frameTextureState = D3D12_RESOURCE_STATE_COMMON;
        uint32_t m_frameTextureWidth = 0;
        uint32_t m_frameTextureHeight = 0;
        uint32_t m_frameUploadRowPitch = 0;

        // Layer 3 decode state.
        Microsoft::WRL::ComPtr<IMFTransform> m_decoder;
        bool m_mfStarted = false;
        bool m_decoderProvidesSamples = false;
        uint32_t m_outputBufferSize = 0;
        uint32_t m_outputAlignment = 0;
        uint32_t m_decodeWidth = 0;             // CODED size: rounded up to whole 16px macroblocks (1080 -> 1088)
        uint32_t m_decodeHeight = 0;
        uint32_t m_decodeStride = 0;            // NV12 row stride (MF_MT_DEFAULT_STRIDE; >= width, may be padded)

        // VISIBLE region within the coded frame (MF_MT_MINIMUM_DISPLAY_APERTURE). This — not the coded size — is
        // the real picture; everything downstream of the decoder works in these dimensions.
        // Which YCbCr matrix the stream signalled: 0 = BT.601, 1 = BT.709. Read from MF_MT_YUV_MATRIX rather
        // than assumed, and false-by-default so an unsignalled stream keeps the known-good BT.601 behaviour.
        // Codec and the resolved decoder. m_inputSubtype records which subtype GUID the MFT actually accepted,
        // since the Annex B and elementary-stream variants are distinct GUIDs and only one will bind.
        VideoCodecKind m_codec = VideoCodecKind::H264;
        GUID m_inputSubtype = GUID_NULL;
        std::wstring m_decoderName;

        // Decode-loop observability: how many samples ProcessOutput actually yielded, its last result, and how
        // far geometry recovery got (see DecoderDiagnostic).
        uint64_t m_producedSamples = 0;
        HRESULT m_lastOutputHr = S_OK;
        int32_t m_geomStage = 0;

        // Codec actually detected in the bitstream, versus what was requested.
        bool m_codecDetected = false;
        bool m_codecMismatch = false;
        uint8_t m_firstNal0 = 0;
        uint8_t m_firstNal1 = 0;
        bool m_sawFirstPayload = false;
        uint8_t m_payloadHead[4] = {};
        int32_t m_detectAttempts = 0;

        // True when the decoder is producing P010 (10-bit). The zero-copy path handles it — the D3D11
        // VideoProcessor converts P010 to BGRA natively — but the CPU readback path's shader is 8-bit NV12 only,
        // so it must refuse rather than render garbage.
        bool m_tenBitOutput = false;
        bool m_toneMappedByDriver = false;
        bool m_tenBitUnrenderable = false;

        int32_t m_yuvMatrix = 0;
        bool m_yuvMatrixSignalled = false;

        // Transfer function and primaries the stream actually signals, read rather than inferred. Bit depth is
        // NOT a transfer function: a 10-bit stream can be plain BT.709, and declaring it PQ would tone-map a
        // picture that needs no tone-mapping. This is the same trap the yuvCoefficient work already hit once -
        // the console ignores what we ask for and signals what it likes, so read the decoder output type.
        // MFVideoTransFunc_Unknown / MFVideoPrimaries_Unknown mean "nothing signalled"; callers then fall back
        // to the previous behaviour rather than guessing something new.
        uint32_t m_transferFunction = 0;   // MFVideoTransferFunction
        uint32_t m_videoPrimaries = 0;     // MFVideoPrimaries
        bool m_transferFunctionSignalled = false;
        bool m_videoPrimariesSignalled = false;

        // True when the stream's transfer function is genuinely an HDR one (PQ or HLG) rather than merely
        // 10-bit. This, not m_tenBitOutput, is what an HDR swap chain should be gated on.
        bool m_hdrTransfer = false;

        // What the display can accept, as opposed to what the stream carries. Presenting HDR needs both.
        bool m_displayHdrCapable = false;
        float m_displayMaxNits = 0.0f;

        uint32_t m_displayWidth = 0;
        uint32_t m_displayHeight = 0;
        uint32_t m_displayOffsetX = 0;
        uint32_t m_displayOffsetY = 0;

        // Coded height of the retained readback frame, needed to locate its UV plane (which sits after the FULL
        // coded Y plane, not after the visible part).
        uint32_t m_latestCodedHeight = 0;
        int64_t m_sampleTime = 0;

        // DXVA hardware-decode backend for the MFT (D3D11-only; MF has no D3D12 backend). Purely a decode
        // device — never used for rendering. Absent => software decode.
        Microsoft::WRL::ComPtr<ID3D11Device> m_decodeD3d11Device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_decodeD3d11Context;
        Microsoft::WRL::ComPtr<IMFDXGIDeviceManager> m_dxgiDeviceManager;
        UINT m_dxgiResetToken = 0;
        bool m_hardwareDecode = false;

        // Zero-copy (GPU-resident) decode state. m_zeroCopyDecode flips on once a frame is delivered this way;
        // m_zeroCopyDisabled sticks on after any failure so we stay on the CPU readback path.
        bool m_zeroCopyDecode = false;
        bool m_zeroCopyDisabled = false;
        Microsoft::WRL::ComPtr<ID3D12Resource> m_sharedDecodeTex;    // D3D12-side BGRA (VP output), shared
        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_sharedDecodeTex11; // same resource opened on the decode device

        // D3D11 VideoProcessor converts the decoder's NV12 output to BGRA (into the shared texture) — BGRA
        // shares cleanly D3D12<->D3D11 where planar NV12 did not, and yields a render-ready RGB texture.
        Microsoft::WRL::ComPtr<ID3D11VideoDevice> m_videoDevice;
        Microsoft::WRL::ComPtr<ID3D11VideoContext> m_videoContext;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> m_vpEnumerator;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessor> m_videoProcessor;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> m_vpOutputView;

        // Forward direction, D3D11 -> D3D12: "the VideoProcessor conversion for this frame is complete."
        Microsoft::WRL::ComPtr<ID3D12Fence> m_sharedFence;
        Microsoft::WRL::ComPtr<ID3D11Fence> m_sharedFence11;
        UINT64 m_sharedFenceValue = 0;   // monotonic value signalled by the D3D11 copy
        UINT64 m_latestFenceValue = 0;   // value the render path must wait for

        // Reverse direction, D3D12 -> D3D11: "the render path has finished reading the shared texture."
        // Without this, the only thing stopping the next VideoProcessorBlt from overwriting the texture
        // mid-read was the full GPU drain after Present — so removing that drain requires this fence.
        Microsoft::WRL::ComPtr<ID3D12Fence> m_readFence;
        Microsoft::WRL::ComPtr<ID3D11Fence> m_readFence11;
        UINT64 m_readFenceValue = 0;     // monotonic value signalled by the D3D12 render path
        bool m_decodePathLogged = false; // one-time debug log of the settled decode path
        int m_zeroCopyFailStage = 0;     // which zero-copy step first failed (0 = none)
        HRESULT m_zeroCopyFailHr = 0;    // its HRESULT
        uint32_t m_sharedTexWidth = 0;
        uint32_t m_sharedTexHeight = 0;

        // GPU NV12 present path resources + the most-recently-decoded frame (decode-all, present-latest).
        Microsoft::WRL::ComPtr<ID3D12RootSignature> m_nv12RootSignature;
        Microsoft::WRL::ComPtr<ID3D12PipelineState> m_nv12Pipeline;
        Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> m_nv12SrvHeap; // [0]=Y (R8), [1]=UV (R8G8)

        // Final-present spatial upscale (samples the decode-res BGRA into the panel-res swap chain).
        Microsoft::WRL::ComPtr<ID3D12RootSignature> m_upscaleRootSignature;
        Microsoft::WRL::ComPtr<ID3D12PipelineState> m_upscalePipeline;
        int32_t m_upscaleMode = 0; // 0 = bilinear, 1 = bicubic
        Microsoft::WRL::ComPtr<ID3D12Resource> m_texY;
        Microsoft::WRL::ComPtr<ID3D12Resource> m_texUV;
        // One upload buffer pair per in-flight frame (see UploadNv12Textures).
        Microsoft::WRL::ComPtr<ID3D12Resource> m_uploadY[FrameCount];
        Microsoft::WRL::ComPtr<ID3D12Resource> m_uploadUV[FrameCount];
        uint32_t m_nv12TexWidth = 0;
        uint32_t m_nv12TexHeight = 0;
        D3D12_RESOURCE_STATES m_nv12TexState = D3D12_RESOURCE_STATE_COMMON;
        uint32_t m_uploadYRowPitch = 0;
        uint32_t m_uploadUVRowPitch = 0;
        uint32_t m_srvDescriptorSize = 0;
        std::vector<uint8_t> m_latestNv12;      // retained newest decoded frame (Y plane then UV plane)
        uint32_t m_latestStride = 0;
        uint32_t m_latestWidth = 0;
        uint32_t m_latestHeight = 0;
        bool m_hasLatestFrame = false;

        uint32_t m_rtvDescriptorSize = 0;
        uint32_t m_width = 0;
        uint32_t m_height = 0;
        uint64_t m_fenceValue = 0;
        HANDLE m_fenceEvent = nullptr;
    };
}

namespace winrt::Ripcord::Media::Interop::factory_implementation
{
    struct VideoRenderer : VideoRendererT<VideoRenderer, implementation::VideoRenderer>
    {
    };
}
