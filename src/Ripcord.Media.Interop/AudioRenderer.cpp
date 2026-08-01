#include "pch.h"
#include "AudioRenderer.h"
#include "Ripcord.Media.Interop.AudioRenderer.g.cpp"

#include <mmreg.h>
#include <ksmedia.h>
#include <cmath>

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

    constexpr REFERENCE_TIME OneMillisecond = 10000; // 100ns units
}

namespace winrt::Ripcord::Media::Interop::implementation
{
    AudioRenderer::~AudioRenderer()
    {
        Stop();
        if (m_mixFormat != nullptr) { CoTaskMemFree(m_mixFormat); m_mixFormat = nullptr; }
        if (m_bufferEvent != nullptr) { CloseHandle(m_bufferEvent); m_bufferEvent = nullptr; }
        if (m_stopEvent != nullptr) { CloseHandle(m_stopEvent); m_stopEvent = nullptr; }
    }

    void AudioRenderer::Initialize()
    {
        m_bufferEvent = CreateEvent(nullptr, FALSE, FALSE, nullptr);
        m_stopEvent = CreateEvent(nullptr, TRUE, FALSE, nullptr);
        if (m_bufferEvent == nullptr || m_stopEvent == nullptr)
        {
            ThrowIfFailed(HRESULT_FROM_WIN32(GetLastError()));
        }

        OpenDevice();
    }

    void AudioRenderer::OpenDevice()
    {
        m_renderClient.Reset();
        m_audioClient.Reset();
        if (m_mixFormat != nullptr) { CoTaskMemFree(m_mixFormat); m_mixFormat = nullptr; }

        ComPtr<IMMDeviceEnumerator> enumerator;
        ThrowIfFailed(CoCreateInstance(
            __uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)));

        ComPtr<IMMDevice> device;
        ThrowIfFailed(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device));
        ThrowIfFailed(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &m_audioClient));
        ThrowIfFailed(m_audioClient->GetMixFormat(&m_mixFormat));

        m_sampleRate = m_mixFormat->nSamplesPerSec;
        m_channels = m_mixFormat->nChannels;
        m_bitsPerSample = m_mixFormat->wBitsPerSample;

        // Detect the device sample type so PCM is written in the endpoint's actual format rather than
        // assuming float32 (almost always true in shared mode, but not guaranteed).
        m_isFloat = false;
        if (m_mixFormat->wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
        {
            m_isFloat = true;
        }
        else if (m_mixFormat->wFormatTag == WAVE_FORMAT_EXTENSIBLE)
        {
            auto* ext = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(m_mixFormat);
            m_isFloat = (ext->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
        }

        ThrowIfFailed(m_audioClient->Initialize(
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            40 * OneMillisecond, 0, m_mixFormat, nullptr));

        ThrowIfFailed(m_audioClient->GetBufferSize(&m_bufferFrameCount));
        ThrowIfFailed(m_audioClient->SetEventHandle(m_bufferEvent));
        ThrowIfFailed(m_audioClient->GetService(IID_PPV_ARGS(&m_renderClient)));
    }

    bool AudioRenderer::ReopenDefaultDevice()
    {
        // Retry until the default endpoint is usable again (e.g. after unplugging headphones), or a
        // stop is requested. Buffered PCM is left intact so playback resumes on the new device.
        while (m_running.load())
        {
            try
            {
                if (m_audioClient) { m_audioClient->Stop(); }
                OpenDevice();
                ThrowIfFailed(m_audioClient->Start());
                return true;
            }
            catch (...)
            {
                if (WaitForSingleObject(m_stopEvent, 500) == WAIT_OBJECT_0)
                {
                    return false;
                }
            }
        }

        return false;
    }

    uint32_t AudioRenderer::SampleRate() { return m_sampleRate; }
    uint32_t AudioRenderer::Channels() { return m_channels; }

    void AudioRenderer::SubmitFloatPcm(winrt::array_view<float const> interleaved, uint32_t sampleCount)
    {
        // 0 means "the whole array"; otherwise take only the valid prefix (the caller reuses its buffer, so the
        // tail holds stale samples from a previous, longer frame).
        const uint32_t available = static_cast<uint32_t>(interleaved.size());
        const uint32_t count = (sampleCount == 0 || sampleCount > available) ? available : sampleCount;
        if (count == 0)
        {
            return;
        }

        std::lock_guard<std::mutex> lock(m_mutex);
        m_ring.insert(m_ring.end(), interleaved.begin(), interleaved.begin() + count);

        // Keep audio near-live. Video latency is now ~250 ms; if the audio backlog is allowed to grow, audio
        // drifts audibly behind video (the ring previously banked up to ~0.5 s and dropped NEW samples once
        // full — so audio lagged AND gapped). Instead, when the backlog exceeds a ceiling, drop the OLDEST
        // samples down to a small target so playback re-syncs toward live. The drop is channel-aligned so the
        // L/R interleave never shifts, and the target/ceiling hysteresis keeps trims rare (only when clock
        // drift or a delivery burst has accumulated real backlog). Under-runs are handled in FillBuffer.
        constexpr size_t TargetLatencyMs = 100;
        constexpr size_t CeilingLatencyMs = 200;
        const size_t perMs = static_cast<size_t>(m_sampleRate) * m_channels / 1000;
        const size_t ceilingSamples = perMs * CeilingLatencyMs;
        if (perMs > 0 && m_ring.size() > ceilingSamples)
        {
            size_t drop = m_ring.size() - perMs * TargetLatencyMs;
            drop -= drop % m_channels; // keep the stereo interleave aligned
            m_ring.erase(m_ring.begin(), m_ring.begin() + static_cast<std::ptrdiff_t>(drop));
        }
    }

    void AudioRenderer::Start()
    {
        if (m_running.exchange(true) || !m_audioClient)
        {
            return;
        }

        ResetEvent(m_stopEvent);
        ThrowIfFailed(m_audioClient->Start());
        m_renderThread = std::thread([this] { RenderLoop(); });
    }

    void AudioRenderer::Stop()
    {
        if (!m_running.exchange(false))
        {
            return;
        }

        if (m_stopEvent != nullptr) { SetEvent(m_stopEvent); }
        if (m_renderThread.joinable()) { m_renderThread.join(); }
        if (m_audioClient) { m_audioClient->Stop(); }
    }

    void AudioRenderer::RenderLoop()
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);

        HANDLE waits[] = { m_stopEvent, m_bufferEvent };
        while (m_running.load())
        {
            DWORD result = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
            if (result == WAIT_OBJECT_0)
            {
                break; // stop requested
            }

            UINT32 padding = 0;
            HRESULT hr = m_audioClient->GetCurrentPadding(&padding);
            if (hr == AUDCLNT_E_DEVICE_INVALIDATED)
            {
                if (!ReopenDefaultDevice()) { break; }
                continue;
            }
            if (FAILED(hr))
            {
                continue;
            }

            const UINT32 framesToWrite = m_bufferFrameCount - padding;
            if (framesToWrite == 0)
            {
                continue;
            }

            BYTE* buffer = nullptr;
            hr = m_renderClient->GetBuffer(framesToWrite, &buffer);
            if (hr == AUDCLNT_E_DEVICE_INVALIDATED)
            {
                if (!ReopenDefaultDevice()) { break; }
                continue;
            }
            if (FAILED(hr))
            {
                continue;
            }

            FillBuffer(buffer, framesToWrite);
            m_renderClient->ReleaseBuffer(framesToWrite, 0);
        }

        winrt::uninit_apartment();
    }

    void AudioRenderer::FillBuffer(uint8_t* destination, uint32_t frames)
    {
        const uint32_t needed = frames * m_channels;
        const uint32_t bytesPerSample = m_bitsPerSample / 8;

        std::lock_guard<std::mutex> lock(m_mutex);
        for (uint32_t i = 0; i < needed; i++)
        {
            float sample = 0.0f; // underrun -> silence
            if (!m_ring.empty())
            {
                sample = m_ring.front();
                m_ring.pop_front();
            }

            sample = sample > 1.0f ? 1.0f : (sample < -1.0f ? -1.0f : sample);
            uint8_t* dst = destination + static_cast<size_t>(i) * bytesPerSample;

            if (m_isFloat)
            {
                *reinterpret_cast<float*>(dst) = sample;
            }
            else if (m_bitsPerSample == 16)
            {
                *reinterpret_cast<int16_t*>(dst) = static_cast<int16_t>(lrintf(sample * 32767.0f));
            }
            else if (m_bitsPerSample == 32)
            {
                *reinterpret_cast<int32_t*>(dst) =
                    static_cast<int32_t>(llrint(static_cast<double>(sample) * 2147483647.0));
            }
            else
            {
                for (uint32_t b = 0; b < bytesPerSample; b++) { dst[b] = 0; } // unsupported depth
            }
        }
    }
}
