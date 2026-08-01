#pragma once
#include "Ripcord.Media.Interop.AudioRenderer.g.h"

#include <audioclient.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <atomic>
#include <deque>
#include <mutex>
#include <thread>

namespace winrt::Ripcord::Media::Interop::implementation
{
    struct AudioRenderer : AudioRendererT<AudioRenderer>
    {
        AudioRenderer() = default;
        ~AudioRenderer();

        void Initialize();
        uint32_t SampleRate();
        uint32_t Channels();
        void SubmitFloatPcm(winrt::array_view<float const> interleaved, uint32_t sampleCount);
        void Start();
        void Stop();

    private:
        void OpenDevice();
        bool ReopenDefaultDevice(); // after a device change/loss; false if stop requested
        void RenderLoop();
        void FillBuffer(uint8_t* destination, uint32_t frames);

        Microsoft::WRL::ComPtr<IAudioClient> m_audioClient;
        Microsoft::WRL::ComPtr<IAudioRenderClient> m_renderClient;
        WAVEFORMATEX* m_mixFormat = nullptr;
        HANDLE m_bufferEvent = nullptr;
        HANDLE m_stopEvent = nullptr;
        std::thread m_renderThread;
        std::atomic<bool> m_running{ false };

        std::mutex m_mutex;
        std::deque<float> m_ring;

        uint32_t m_sampleRate = 0;
        uint32_t m_channels = 0;
        uint32_t m_bufferFrameCount = 0;
        bool m_isFloat = true;       // device sample type (float vs integer PCM)
        uint32_t m_bitsPerSample = 32;
    };
}

namespace winrt::Ripcord::Media::Interop::factory_implementation
{
    struct AudioRenderer : AudioRendererT<AudioRenderer, implementation::AudioRenderer>
    {
    };
}
